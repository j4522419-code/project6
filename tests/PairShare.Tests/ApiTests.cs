using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PairShare.Tests;

public class ApiTests
{
    [Fact]
    public async Task Unpaired_devices_get_the_pair_page_and_nothing_else()
    {
        await using var app = new PairShareApp();
        var device = app.Device();

        Assert.Contains("pair.js", await device.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/files")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.GetAsync("/api/host/info")).StatusCode);
    }

    [Fact]
    public async Task The_host_computer_gets_the_dashboard()
    {
        await using var app = new PairShareApp();
        var host = app.Host();

        Assert.Contains("host.js", await host.GetStringAsync("/"));
        var info = await host.GetFromJsonAsync<JsonElement>("/api/host/info");
        Assert.Matches("^[0-9]{6}$", info.GetProperty("code").GetString());
        Assert.StartsWith("http://", info.GetProperty("addresses")[0].GetProperty("link").GetString());
    }

    [Fact]
    public async Task Pairing_with_the_code_gives_access_and_the_code_only_works_once()
    {
        await using var app = new PairShareApp();
        var host = app.Host();
        var device = app.Device();
        var code = await CurrentCode(host);

        var wrong = await device.PostAsJsonAsync("/api/pair", new { code = "12" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var paired = await device.PostAsJsonAsync("/api/pair", new { code, name = "Test phone" });
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);

        var me = await device.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("device", me.GetProperty("role").GetString());
        Assert.Equal("Test phone", me.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/api/files")).StatusCode);

        var reused = await app.Device("192.168.1.77").PostAsJsonAsync("/api/pair", new { code });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.NotEqual(code, await CurrentCode(host));
    }

    [Fact]
    public async Task Repeated_wrong_codes_are_rate_limited()
    {
        await using var app = new PairShareApp();
        var device = app.Device();

        HttpResponseMessage response = null!;
        for (var i = 0; i < 5; i++)
        {
            response = await device.PostAsJsonAsync("/api/pair", new { code = "not a code" });
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Files_can_be_shared_in_both_directions()
    {
        await using var app = new PairShareApp();
        var host = app.Host();
        var device = app.Device();
        await Pair(host, device, "Test phone");

        var photo = new byte[300_000];
        Random.Shared.NextBytes(photo);
        var upload = await device.PostAsync("/api/files?name=photo.jpg", new ByteArrayContent(photo));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var hostView = await host.GetFromJsonAsync<JsonElement[]>("/api/files");
        var shared = Assert.Single(hostView!);
        Assert.Equal("photo.jpg", shared.GetProperty("name").GetString());
        Assert.Equal("Test phone", shared.GetProperty("addedBy").GetString());
        Assert.Equal(photo, await host.GetByteArrayAsync($"/api/files/{shared.GetProperty("id").GetString()}"));
        Assert.Equal(photo, await File.ReadAllBytesAsync(Path.Combine(app.Folder, "photo.jpg")));

        await host.PostAsync("/api/files?name=notes.txt", new StringContent("from the computer"));
        var deviceView = await device.GetFromJsonAsync<JsonElement[]>("/api/files");
        var note = deviceView!.Single(f => f.GetProperty("name").GetString() == "notes.txt");
        Assert.False(note.GetProperty("canDelete").GetBoolean());
        Assert.Equal("from the computer", await device.GetStringAsync($"/api/files/{note.GetProperty("id").GetString()}"));
    }

    [Fact]
    public async Task Only_the_host_or_the_sender_can_remove_a_file()
    {
        await using var app = new PairShareApp();
        var host = app.Host();
        var sender = app.Device();
        var other = app.Device("192.168.1.60");
        await Pair(host, sender);
        await Pair(host, other);

        var uploaded = await (await sender.PostAsync("/api/files?name=a.txt", new StringContent("a")))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = uploaded.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync($"/api/files/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await sender.DeleteAsync($"/api/files/{id}")).StatusCode);
        Assert.False(File.Exists(Path.Combine(app.Folder, "a.txt")));

        await other.PostAsync("/api/files?name=b.txt", new StringContent("b"));
        var b = (await host.GetFromJsonAsync<JsonElement[]>("/api/files"))!.Single();
        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/files/{b.GetProperty("id").GetString()}")).StatusCode);
    }

    [Fact]
    public async Task Uploaded_names_cannot_escape_the_shared_folder()
    {
        await using var app = new PairShareApp();
        var host = app.Host();
        var device = app.Device();
        await Pair(host, device);

        var response = await device.PostAsync("/api/files?name=" + Uri.EscapeDataString("../../escape.txt"), new StringContent("x"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(app.Folder, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(app.Folder, "..", "escape.txt")));
    }

    [Fact]
    public async Task Quick_pair_link_pairs_a_phone_once()
    {
        await using var app = new PairShareApp();
        var host = app.Host();
        var info = await host.GetFromJsonAsync<JsonElement>("/api/host/info");
        var quickPath = new Uri(info.GetProperty("addresses")[0].GetProperty("quickLink").GetString()!).PathAndQuery;

        var phone = app.Device();
        phone.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)");
        var first = await phone.GetAsync(quickPath);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("/", first.Headers.Location?.OriginalString);

        var me = await phone.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("device", me.GetProperty("role").GetString());
        Assert.Equal("iPhone", me.GetProperty("name").GetString());

        var second = await app.Device("192.168.1.70").GetAsync(quickPath);
        Assert.Equal("/?quick=expired", second.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Unpaired_devices_lose_access_immediately()
    {
        await using var app = new PairShareApp();
        var host = app.Host();
        var device = app.Device();
        var id = await Pair(host, device);

        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/host/devices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/files")).StatusCode);
        Assert.Empty((await host.GetFromJsonAsync<JsonElement[]>("/api/host/devices"))!);
    }

    [Fact]
    public async Task State_changing_requests_need_the_api_header()
    {
        await using var app = new PairShareApp();
        var host = app.Host();
        host.DefaultRequestHeaders.Remove("X-PairShare");

        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsync("/api/host/regenerate", null)).StatusCode);
    }

    [Fact]
    public async Task Requests_for_unknown_host_names_are_rejected()
    {
        await using var app = new PairShareApp();

        var response = await app.Host().GetAsync("http://attacker.example/api/host/info");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Qr_codes_are_served_as_svg()
    {
        await using var app = new PairShareApp();

        var response = await app.Host().GetAsync("/api/host/qr?kind=quick");

        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("<svg", await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> CurrentCode(HttpClient host) =>
        (await host.GetFromJsonAsync<JsonElement>("/api/host/info")).GetProperty("code").GetString()!;

    private static async Task<string> Pair(HttpClient host, HttpClient device, string name = "Device")
    {
        var response = await device.PostAsJsonAsync("/api/pair", new { code = await CurrentCode(host), name });
        response.EnsureSuccessStatusCode();
        return (await device.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("id").GetString()!;
    }
}

/// <summary>The real app on an in-memory server, with a throwaway shared folder.</summary>
internal sealed class PairShareApp : WebApplicationFactory<Program>
{
    private const string FromHeader = "X-Test-From";

    public string Folder { get; } = Directory.CreateTempSubdirectory("pairshare-api-").FullName;

    /// <summary>A browser on the host computer itself (loopback).</summary>
    public HttpClient Host() => Client("127.0.0.1");

    /// <summary>A browser on another device on the network.</summary>
    public HttpClient Device(string ip = "192.168.1.50") => Client(ip);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("PairShare:Dir", Folder);
        builder.UseSetting("PairShare:OpenBrowser", "false");
        builder.UseSetting("PairShare:Quiet", "true");
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, FakeRemoteAddress>());
    }

    private HttpClient Client(string ip)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        client.DefaultRequestHeaders.Add(FromHeader, ip);
        client.DefaultRequestHeaders.Add("X-PairShare", "1");
        return client;
    }

    // The in-memory server has no sockets, so let each client say which address it "comes from".
    private sealed class FakeRemoteAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext ctx, RequestDelegate nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Parse(ctx.Request.Headers[FromHeader].FirstOrDefault() ?? "192.168.1.50");
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }
}
