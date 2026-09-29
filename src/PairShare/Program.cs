using System.Text;
using Microsoft.Extensions.FileProviders;
using PairShare;
using PairShare.Services;

CommandLine.Result cli;
try
{
    cli = CommandLine.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Run with --help to see the options.");
    return 1;
}

if (cli.ShowHelp)
{
    CommandLine.PrintHelp();
    return 0;
}

try
{
    Console.OutputEncoding = Encoding.UTF8; // QR code block characters
}
catch (IOException)
{
}

var builder = WebApplication.CreateBuilder(cli.Passthrough);
builder.Configuration.AddInMemoryCollection(cli.Settings);
builder.Logging
    .AddFilter("Microsoft", LogLevel.Warning)
    .AddFilter("System", LogLevel.Warning)
    .AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.Critical); // startup failures are reported below

builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    // Listen on every network interface so phones on the same Wi-Fi can connect.
    kestrel.ListenAnyIP(AppOptions.From(context.Configuration).Port);
    kestrel.Limits.MaxRequestBodySize = 1024 * 1024; // the upload endpoint raises this per request
});

builder.Services.AddSingleton(sp => AppOptions.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PairingService>();
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<FileStore>();
builder.Services.AddSingleton<NetworkInfo>();
builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddHostedService<PairShareBackground>();

WebApplication app;
AppOptions options;
try
{
    app = builder.Build();
    options = app.Services.GetRequiredService<AppOptions>();
    _ = app.Services.GetRequiredService<FileStore>(); // creates the shared folder, so problems show up now
}
catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"PairShare couldn't start: {ex.Message}");
    return 1;
}

var assembly = typeof(Program).Assembly;
var assets = new ManifestEmbeddedFileProvider(assembly, "wwwroot/assets");
var pages = new ManifestEmbeddedFileProvider(assembly, "wwwroot/pages");

app.UseMiddleware<RequestGuard>();
app.UseCallerResolution();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = assets,
    RequestPath = "/assets",
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapPairShare(pages);

app.Lifetime.ApplicationStarted.Register(() => Banner.Show(app.Services));

try
{
    await app.RunAsync();
    return 0;
}
catch (IOException ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  Couldn't listen on port {options.Port}: {ex.Message}");
    Console.Error.WriteLine("  Is PairShare already running? Close it, or start with --port <another number>.");
    return 1;
}

public partial class Program;
