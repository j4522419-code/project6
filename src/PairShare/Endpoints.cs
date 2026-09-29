using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using PairShare.Services;

namespace PairShare;

internal static class Endpoints
{
    public sealed record PairRequest(string? Code, string? Name);

    public static void MapPairShare(this WebApplication app, IFileProvider pages)
    {
        // One URL for everybody: the host gets the dashboard, paired devices get the
        // file-sharing app, and everyone else gets the pair-code screen.
        app.MapGet("/", (HttpContext ctx) => Page(ctx, pages, ctx.GetCaller().Role switch
        {
            CallerRole.Host => "host.html",
            CallerRole.Device => "app.html",
            _ => "pair.html",
        }));

        // Target of the "Quick pair" QR code.
        app.MapGet("/q/{token}", QuickPair);

        var api = app.MapGroup("/api");
        api.MapGet("/me", Me);
        api.MapPost("/pair", Pair);
        api.MapPost("/unpair", Unpair).AddEndpointFilter(RequirePaired);
        api.MapGet("/events", Events).AddEndpointFilter(RequirePaired);

        var files = api.MapGroup("/files").AddEndpointFilter(RequirePaired);
        files.MapGet("", ListFiles);
        files.MapPost("", Upload);
        files.MapGet("/{id}", Download);
        files.MapDelete("/{id}", DeleteFile);

        var host = api.MapGroup("/host").AddEndpointFilter(RequireHost);
        host.MapGet("/info", HostInfo);
        host.MapGet("/qr", QrCode);
        host.MapPost("/regenerate", Regenerate);
        host.MapGet("/devices", ListDevices);
        host.MapDelete("/devices/{id}", RevokeDevice);
        host.MapPost("/open-folder", OpenFolder);
    }

    // ---- pages & pairing -------------------------------------------------------------

    private static IResult Page(HttpContext ctx, IFileProvider pages, string name)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Stream(pages.GetFileInfo(name).CreateReadStream(), "text/html; charset=utf-8");
    }

    private static IResult QuickPair(string token, HttpContext ctx, PairingService pairing, DeviceRegistry devices, ActivityLog log)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (ctx.GetCaller().IsPaired)
        {
            return Results.Redirect("/");
        }

        if (!pairing.TryQuickToken(token))
        {
            log.Warn($"Expired quick-pair QR code scanned from {ctx.ClientAddress()}");
            return Results.Redirect("/?quick=expired");
        }

        var device = Register(ctx, devices, name: null);
        log.Success($"{device.Name} paired using quick pair ({device.Address})");
        return Results.Redirect("/");
    }

    private static IResult Me(HttpContext ctx, AppOptions options)
    {
        var caller = ctx.GetCaller();
        return Results.Ok(new
        {
            role = caller.Role.ToString().ToLowerInvariant(),
            id = caller.Id,
            name = caller.IsPaired ? caller.DisplayName : null,
            hostName = Environment.MachineName,
            maxUploadBytes = options.MaxUploadBytes,
        });
    }

    private static IResult Pair(PairRequest? body, HttpContext ctx, PairingService pairing, DeviceRegistry devices, ActivityLog log)
    {
        var caller = ctx.GetCaller();
        if (caller.IsPaired)
        {
            return Results.Ok(new { name = caller.DisplayName });
        }

        var attempt = pairing.TryCode(ctx.ClientAddress(), body?.Code);
        switch (attempt.Outcome)
        {
            case PairOutcome.Success:
                var device = Register(ctx, devices, body?.Name);
                log.Success($"{device.Name} paired using the pair code ({device.Address})");
                return Results.Ok(new { name = device.Name });

            case PairOutcome.LockedOut:
                var seconds = (int)Math.Ceiling(attempt.RetryAfter.TotalSeconds);
                ctx.Response.Headers.RetryAfter = seconds.ToString();
                if (attempt.JustLocked)
                {
                    log.Warn($"Too many wrong pair codes from {ctx.ClientAddress()} - paused for {seconds}s");
                }
                return Results.Json(
                    new { error = $"Too many wrong codes. Try again in {seconds} seconds.", retryAfterSeconds = seconds },
                    statusCode: StatusCodes.Status429TooManyRequests);

            default:
                log.Warn($"Wrong pair code from {ctx.ClientAddress()}");
                return Results.Json(
                    new { error = "That code isn't right. Check the code on the computer.", attemptsLeft = attempt.AttemptsLeft },
                    statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static IResult Unpair(HttpContext ctx, DeviceRegistry devices, EventHub hub, ActivityLog log)
    {
        if (ctx.GetCaller().Device is { } device && devices.Remove(device.Id, out _))
        {
            hub.Disconnect(device.Id);
            log.Info($"{device.Name} disconnected");
        }

        SessionCookie.Clear(ctx);
        return Results.NoContent();
    }

    private static Device Register(HttpContext ctx, DeviceRegistry devices, string? name)
    {
        var (device, token) = devices.Register(name, ctx.Request.Headers.UserAgent, ctx.ClientAddress());
        SessionCookie.Set(ctx, token);
        return device;
    }

    // ---- live updates ----------------------------------------------------------------

    private static async Task Events(HttpContext ctx, EventHub hub, IHostApplicationLifetime lifetime)
    {
        // End the stream when the tab closes *or* PairShare is shutting down, so Ctrl+C is instant.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, lifetime.ApplicationStopping);
        var ct = stop.Token;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache, no-store";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var subscription = hub.Subscribe(ctx.GetCaller());
        try
        {
            await ctx.Response.WriteAsync("retry: 3000\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (!await subscription.Reader.WaitToReadAsync(heartbeat.Token))
                    {
                        break; // the device was unpaired
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await ctx.Response.WriteAsync(": ping\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    continue;
                }

                while (subscription.Reader.TryRead(out var frame))
                {
                    await ctx.Response.WriteAsync(frame, ct);
                }

                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Browser tab closed, or shutting down.
        }
    }

    // ---- files -----------------------------------------------------------------------

    private static IResult ListFiles(HttpContext ctx, FileStore store)
    {
        var caller = ctx.GetCaller();
        return Results.Ok(store.List().Select(f => ToDto(f, caller)));
    }

    private static async Task<IResult> Upload(string? name, HttpContext ctx, FileStore store, AppOptions options, ActivityLog log)
    {
        var caller = ctx.GetCaller();
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = options.MaxUploadBytes;
        }

        if (ctx.Request.ContentLength > options.MaxUploadBytes)
        {
            return TooLarge(options);
        }

        try
        {
            var file = await store.SaveAsync(name, ctx.Request.Body, options.MaxUploadBytes, caller, ctx.RequestAborted);
            log.Transfer("↑", $"{caller.DisplayName} shared {file.Name} ({Sizes.Format(file.Size)})");
            return Results.Ok(ToDto(file, caller));
        }
        catch (FileTooLargeException)
        {
            return TooLarge(options);
        }
        catch (BadHttpRequestException ex)
        {
            return ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? TooLarge(options) : Error(ex.StatusCode, "Upload was interrupted.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && ctx.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty; // the device cancelled or lost connection
        }
        catch (IOException ex)
        {
            log.Warn($"Couldn't save upload from {caller.DisplayName}: {ex.Message}");
            return Error(StatusCodes.Status500InternalServerError, "The computer couldn't save the file (is the disk full?).");
        }
    }

    private static IResult Download(string id, HttpContext ctx, FileStore store, ActivityLog log)
    {
        var file = store.Find(id);
        if (file is null)
        {
            return Error(StatusCodes.Status404NotFound, "That file is no longer shared.");
        }

        var caller = ctx.GetCaller();
        if (!caller.IsHost && !ctx.Request.Headers.ContainsKey("Range"))
        {
            log.Transfer("↓", $"{caller.DisplayName} downloaded {file.Name}");
        }

        return Results.File(file.Path, "application/octet-stream", file.Name, file.Modified, enableRangeProcessing: true);
    }

    private static IResult DeleteFile(string id, HttpContext ctx, FileStore store, ActivityLog log)
    {
        var caller = ctx.GetCaller();
        var file = store.Find(id);
        if (file is null)
        {
            return Error(StatusCodes.Status404NotFound, "That file is no longer shared.");
        }

        if (!CanDelete(file, caller))
        {
            return Error(StatusCodes.Status403Forbidden, "Only the computer or the device that shared a file can remove it.");
        }

        try
        {
            store.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error(StatusCodes.Status409Conflict, "The file is in use. Try again in a moment.");
        }

        log.Info($"{caller.DisplayName} removed {file.Name}");
        return Results.NoContent();
    }

    private static object ToDto(SharedFile file, Caller caller) => new
    {
        id = file.Id,
        name = file.Name,
        size = file.Size,
        modified = file.Modified,
        addedBy = file.AddedBy,
        addedById = file.AddedById,
        mine = file.AddedById == caller.Id,
        canDelete = CanDelete(file, caller),
    };

    private static bool CanDelete(SharedFile file, Caller caller) => caller.IsHost || file.AddedById == caller.Id;

    private static IResult TooLarge(AppOptions options) =>
        Error(StatusCodes.Status413PayloadTooLarge, $"That file is too big (limit {Sizes.Format(options.MaxUploadBytes)}).");

    // ---- host only -------------------------------------------------------------------

    private static IResult HostInfo(PairingService pairing, NetworkInfo network, FileStore store, AppOptions options, TimeProvider time)
    {
        var ticket = pairing.Current;
        return Results.Ok(new
        {
            code = ticket.Code,
            version = ticket.Version,
            secondsLeft = Math.Max(0, (int)Math.Ceiling((ticket.ExpiresAt - time.GetUtcNow()).TotalSeconds)),
            lifetimeSeconds = (int)pairing.Lifetime.TotalSeconds,
            hostName = Environment.MachineName,
            port = options.Port,
            folder = store.Root,
            addresses = network.GetAddresses().Select(a => new
            {
                address = a,
                link = network.AppLink(a),
                quickLink = network.QuickPairLink(a, ticket.QuickToken),
            }),
        });
    }

    private static IResult QrCode(string? kind, string? addr, PairingService pairing, NetworkInfo network)
    {
        var addresses = network.GetAddresses();
        var address = addr is not null && addresses.Contains(addr) ? addr : addresses[0];
        var text = kind == "quick" ? network.QuickPairLink(address, pairing.Current.QuickToken) : network.AppLink(address);
        return Results.Text(Qr.ToSvg(text), "image/svg+xml");
    }

    private static IResult Regenerate(PairingService pairing, ActivityLog log)
    {
        pairing.Regenerate();
        log.Info("New pair code generated");
        return Results.NoContent();
    }

    private static IResult ListDevices(DeviceRegistry devices, EventHub hub) =>
        Results.Ok(devices.List().Select(d => new
        {
            id = d.Id,
            name = d.Name,
            address = d.Address,
            pairedAt = d.PairedAt,
            lastSeen = d.LastSeen,
            online = hub.IsOnline(d.Id),
        }));

    private static IResult RevokeDevice(string id, DeviceRegistry devices, EventHub hub, ActivityLog log)
    {
        if (!devices.Remove(id, out var device))
        {
            return Error(StatusCodes.Status404NotFound, "That device isn't paired.");
        }

        hub.Disconnect(device.Id);
        log.Info($"Unpaired {device.Name}");
        return Results.NoContent();
    }

    private static IResult OpenFolder(FileStore store) =>
        Launcher.TryOpen(store.Root)
            ? Results.NoContent()
            : Error(StatusCodes.Status500InternalServerError, "Couldn't open the folder on this computer.");

    // ---- helpers ---------------------------------------------------------------------

    private static async ValueTask<object?> RequirePaired(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.GetCaller().IsPaired
            ? await next(context)
            : Error(StatusCodes.Status401Unauthorized, "This device isn't paired. Enter the pair code first.");

    private static async ValueTask<object?> RequireHost(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.GetCaller().IsHost
            ? await next(context)
            : Error(StatusCodes.Status403Forbidden, "Only available on the host computer.");

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
