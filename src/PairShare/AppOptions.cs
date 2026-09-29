namespace PairShare;

public sealed class AppOptions
{
    public int Port { get; init; } = 5050;
    public string SharedFolder { get; init; } = DefaultFolder();
    public string? Address { get; init; }
    public TimeSpan CodeLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public long MaxUploadBytes { get; init; } = 4096L * 1024 * 1024;
    public bool OpenBrowser { get; init; } = true;
    public bool Quiet { get; init; }

    public static AppOptions From(IConfiguration config)
    {
        var section = config.GetSection("PairShare");

        var port = ReadInt(section, "Port", 5050, 1, 65535);
        var minutes = ReadInt(section, "CodeMinutes", 5, 1, 24 * 60);
        var maxUploadMb = ReadInt(section, "MaxUploadMb", 4096, 1, int.MaxValue);
        var dir = section["Dir"];

        return new AppOptions
        {
            Port = port,
            SharedFolder = Path.GetFullPath(string.IsNullOrWhiteSpace(dir) ? DefaultFolder() : ExpandHome(dir)),
            Address = string.IsNullOrWhiteSpace(section["Address"]) ? null : section["Address"]!.Trim(),
            CodeLifetime = TimeSpan.FromMinutes(minutes),
            MaxUploadBytes = maxUploadMb * 1024L * 1024L,
            OpenBrowser = ReadBool(section, "OpenBrowser", true),
            Quiet = ReadBool(section, "Quiet", false),
        };
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback, int min, int max)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, out var value) || value < min || value > max)
        {
            throw new ArgumentException($"Invalid value '{raw}' for {key} (expected a number from {min} to {max}).");
        }

        return value;
    }

    private static bool ReadBool(IConfigurationSection section, string key, bool fallback) =>
        bool.TryParse(section[key], out var value) ? value : fallback;

    private static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Home(), path.Length > 2 ? path[2..] : "")
            : path;

    private static string Home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? AppContext.BaseDirectory : home;
    }

    private static string DefaultFolder()
    {
        var downloads = Path.Combine(Home(), "Downloads");
        return Path.Combine(Directory.Exists(downloads) ? downloads : Home(), "PairShare");
    }
}
