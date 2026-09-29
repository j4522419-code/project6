namespace PairShare.Services;

public enum CallerRole { Anonymous, Host, Device }

/// <summary>
/// Who is making a request. Requests from this computer (loopback) are the host;
/// everyone else must present a session cookie obtained by pairing.
/// </summary>
public sealed record Caller(CallerRole Role, Device? Device)
{
    public const string HostId = "host";

    public static readonly Caller Anonymous = new(CallerRole.Anonymous, null);
    public static readonly Caller Host = new(CallerRole.Host, null);

    public bool IsHost => Role == CallerRole.Host;
    public bool IsPaired => Role != CallerRole.Anonymous;
    public string Id => Role == CallerRole.Host ? HostId : Device?.Id ?? "";
    public string DisplayName => Role == CallerRole.Host ? Environment.MachineName : Device?.Name ?? "Unknown";
}

internal static class SessionCookie
{
    public const string Name = "pairshare_session";

    public static void Set(HttpContext ctx, string token) =>
        ctx.Response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = TimeSpan.FromDays(30),
            IsEssential = true,
        });

    public static void Clear(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(Name, new CookieOptions { Path = "/" });
}

internal static class CallerExtensions
{
    private const string ItemKey = "PairShare.Caller";

    public static Caller GetCaller(this HttpContext ctx) =>
        ctx.Items.TryGetValue(ItemKey, out var value) && value is Caller caller ? caller : Caller.Anonymous;

    public static string ClientAddress(this HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null)
        {
            return "unknown";
        }

        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    public static bool IsLoopback(this HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip is not null && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
    }

    /// <summary>Middleware that works out who the caller is once per request.</summary>
    public static IApplicationBuilder UseCallerResolution(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            Caller caller;
            if (ctx.IsLoopback())
            {
                caller = Caller.Host;
            }
            else
            {
                var devices = ctx.RequestServices.GetRequiredService<DeviceRegistry>();
                if (devices.TryGet(ctx.Request.Cookies[SessionCookie.Name], out var device))
                {
                    devices.Touch(device, ctx.ClientAddress());
                    caller = new Caller(CallerRole.Device, device);
                }
                else
                {
                    caller = Caller.Anonymous;
                }
            }

            ctx.Items[ItemKey] = caller;
            await next(ctx);
        });
}
