using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace PairShare.Services;

public sealed record SharedFile(string Id, string Name, long Size, DateTimeOffset Modified, string AddedBy, string AddedById)
{
    internal string Path { get; init; } = "";
}

public sealed class FileTooLargeException(long limit) : Exception($"File is larger than the {limit / (1024 * 1024)} MB limit.");

/// <summary>
/// The shared folder on the host is the source of truth: whatever is in it is shared,
/// including files the host drops in with Explorer / Finder.
/// </summary>
public sealed class FileStore : IDisposable
{
    private const string IncomingDirName = ".pairshare-incoming";

    private static readonly StringComparer NameComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "Thumbs.db",
    };

    private readonly ConcurrentDictionary<string, (string Name, string Id)> _origins = new(NameComparer);
    private readonly object _moveGate = new();
    private readonly string _incoming;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;

    public FileStore(AppOptions options, ILogger<FileStore> logger)
    {
        Root = Path.GetFullPath(options.SharedFolder);
        Directory.CreateDirectory(Root);

        _incoming = Path.Combine(Root, IncomingDirName);
        TryDeleteDirectory(_incoming); // leftovers from an interrupted run
        EnsureDirectories();

        _debounce = new Timer(_ => Changed?.Invoke());

        try
        {
            _watcher = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            _watcher.Created += (_, _) => NotifyChanged();
            _watcher.Deleted += (_, _) => NotifyChanged();
            _watcher.Renamed += (_, _) => NotifyChanged();
            _watcher.Changed += (_, _) => NotifyChanged();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            logger.LogWarning("Not watching {Folder} for changes: {Message}", Root, ex.Message);
        }
    }

    public string Root { get; }

    /// <summary>Raised (debounced) whenever the set of shared files may have changed.</summary>
    public event Action? Changed;

    public void NotifyChanged() => _debounce.Change(250, Timeout.Infinite);

    public IReadOnlyList<SharedFile> List()
    {
        EnsureDirectories();
        return new DirectoryInfo(Root)
            .EnumerateFiles()
            .Where(f => !IsHidden(f))
            .Select(ToShared)
            .OrderByDescending(f => f.Modified)
            .ToList();
    }

    public SharedFile? Find(string id)
    {
        EnsureDirectories();
        return new DirectoryInfo(Root)
            .EnumerateFiles()
            .Where(f => !IsHidden(f) && IdFor(f.Name) == id)
            .Select(ToShared)
            .FirstOrDefault();
    }

    public async Task<SharedFile> SaveAsync(string? requestedName, Stream content, long maxBytes, Caller from, CancellationToken ct)
    {
        EnsureDirectories();
        var safeName = FileNames.Sanitize(requestedName);
        var temp = Path.Combine(_incoming, Guid.NewGuid().ToString("N") + ".part");
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new FileTooLargeException(maxBytes);
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            string finalPath;
            lock (_moveGate)
            {
                finalPath = FileNames.Unique(Root, safeName);
                File.Move(temp, finalPath);
                _origins[Path.GetFileName(finalPath)] = (from.DisplayName, from.Id);
            }

            NotifyChanged();
            return ToShared(new FileInfo(finalPath));
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <exception cref="IOException">The file is in use.</exception>
    public void Delete(SharedFile file)
    {
        File.Delete(file.Path);
        _origins.TryRemove(file.Name, out _);
        NotifyChanged();
    }

    public static string IdFor(string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0, 12).ToLowerInvariant();

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }

    private SharedFile ToShared(FileInfo f)
    {
        var (by, byId) = _origins.TryGetValue(f.Name, out var origin) ? origin : (Environment.MachineName, Caller.HostId);
        return new SharedFile(IdFor(f.Name), f.Name, f.Length, f.LastWriteTimeUtc, by, byId) { Path = f.FullName };
    }

    private static bool IsHidden(FileInfo f) =>
        f.Name.StartsWith('.') ||
        IgnoredNames.Contains(f.Name) ||
        (OperatingSystem.IsWindows() && (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0);

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        if (!Directory.Exists(_incoming))
        {
            var dir = Directory.CreateDirectory(_incoming);
            if (OperatingSystem.IsWindows())
            {
                dir.Attributes |= FileAttributes.Hidden;
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
