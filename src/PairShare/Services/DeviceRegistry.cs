using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PairShare.Services;

public sealed class Device
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset PairedAt { get; init; }
    public string Address { get; set; } = "";
    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>Paired devices, keyed by their secret session token (held in a cookie on the device).</summary>
public sealed class DeviceRegistry(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Device> _byToken = new(StringComparer.Ordinal);
    private readonly object _registerGate = new();

    public event Action? Changed;

    public (Device Device, string Token) Register(string? requestedName, string? userAgent, string address)
    {
        var token = Tokens.New(32);
        Device device;
        lock (_registerGate)
        {
            var name = UniqueName(DeviceNames.Clean(requestedName) ?? DeviceNames.Guess(userAgent));
            var now = time.GetUtcNow();
            device = new Device { Id = Tokens.ShortId(), Name = name, PairedAt = now, Address = address, LastSeen = now };
            _byToken[token] = device;
        }

        Changed?.Invoke();
        return (device, token);
    }

    public bool TryGet(string? token, [NotNullWhen(true)] out Device? device)
    {
        device = null;
        return !string.IsNullOrEmpty(token) && _byToken.TryGetValue(token, out device);
    }

    public void Touch(Device device, string address)
    {
        device.Address = address;
        device.LastSeen = time.GetUtcNow();
    }

    public bool Remove(string deviceId, [NotNullWhen(true)] out Device? removed)
    {
        removed = null;
        foreach (var (token, device) in _byToken)
        {
            if (device.Id == deviceId && _byToken.TryRemove(token, out removed))
            {
                break;
            }
        }

        if (removed is not null)
        {
            Changed?.Invoke();
        }

        return removed is not null;
    }

    public IReadOnlyList<Device> List() => _byToken.Values.OrderBy(d => d.PairedAt).ToList();

    private string UniqueName(string name)
    {
        var taken = _byToken.Values.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}

internal static class DeviceNames
{
    public const int MaxLength = 40;

    public static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > MaxLength)
        {
            cleaned = cleaned[..MaxLength].TrimEnd();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    public static string Guess(string? userAgent)
    {
        var ua = userAgent ?? "";
        bool Has(string s) => ua.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("iPhone")) return "iPhone";
        if (Has("iPad")) return "iPad";
        if (Has("Android")) return Has("Mobile") ? "Android phone" : "Android tablet";
        if (Has("CrOS")) return "Chromebook";
        if (Has("Windows")) return "Windows PC";
        if (Has("Macintosh") || Has("Mac OS X")) return "Mac";
        if (Has("Linux")) return "Linux PC";
        return "Device";
    }
}
