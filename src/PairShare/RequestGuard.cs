namespace PairShare;

/// <summary>
/// Cheap protections for a LAN web app:
/// <list type="bullet">
///   <item>Security headers on every response.</item>
///   <item>Only IP-literal / localhost / this machine's name are accepted as Host,
///         which stops DNS-rebinding pages from reading the host dashboard.</item>
///   <item>State-changing API calls must carry an <c>X-PairShare</c> header. Browsers only send
///         custom headers cross-origin after a CORS preflight we never approve, so other
///         websites can't forge these requests (CSRF).</item>
/// </list>
/// </summary>
internal sealed class RequestGuard(RequestDelegate next)
{
    public const string ApiHeader = "X-PairShare";

    private static readonly HashSet<string> LocalSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "local", "lan", "home", "home.arpa", "localdomain",
    };

    public async Task InvokeAsync(HttpContext ctx)
    {
        var headers = ctx.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers.ContentSecurityPolicy =
            "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; " +
            "connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

        if (!IsAllowedHost(ctx.Request.Host.Host))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Unknown host name. Open PairShare using its IP address.");
            return;
        }

        var isApi = ctx.Request.Path.StartsWithSegments("/api");
        if (isApi)
        {
            headers.CacheControl = "no-store";
        }

        var method = ctx.Request.Method;
        if (isApi &&
            !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method) &&
            ctx.Request.Headers[ApiHeader] != "1")
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "Missing request header." });
            return;
        }

        await next(ctx);
    }

    internal static bool IsAllowedHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (IPAddress.TryParse(host.Trim('[', ']'), out _) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dot = host.IndexOf('.');
        var label = dot < 0 ? host : host[..dot];
        var suffix = dot < 0 ? "" : host[(dot + 1)..].TrimEnd('.');
        return label.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) && LocalSuffixes.Contains(suffix);
    }
}
