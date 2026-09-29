using PairShare.Services;

namespace PairShare;

/// <summary>What the host sees in the console when PairShare starts.</summary>
internal static class Banner
{
    public static void Show(IServiceProvider services)
    {
        var options = services.GetRequiredService<AppOptions>();
        if (options.Quiet)
        {
            return;
        }

        var network = services.GetRequiredService<NetworkInfo>();
        var pairing = services.GetRequiredService<PairingService>();
        var store = services.GetRequiredService<FileStore>();

        var address = network.GetAddresses()[0];
        var link = network.AppLink(address);
        var dashboard = $"http://localhost:{options.Port}/";
        var code = pairing.Current.Code;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("  PairShare");
        Console.ResetColor();
        Console.WriteLine($" {AppInfo.Version} is running on {Environment.MachineName}");
        Console.WriteLine();
        Row("Dashboard", dashboard);
        Row("Link", link);
        Row("Pair code", $"{code[..3]} {code[3..]}   (single-use; the dashboard always shows the current one)");
        Row("Folder", store.Root);
        Console.WriteLine();
        Console.WriteLine("  Scan this with your phone to open PairShare:");
        Console.WriteLine();
        Console.Write(Qr.ToTerminal(link));
        Console.WriteLine();

        if (address == "localhost")
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  No network connection found - other devices won't be able to reach this computer.");
            Console.ResetColor();
        }

        Console.WriteLine("  Keep this window open while sharing. Press Ctrl+C to stop.");
        Console.WriteLine();

        if (options.OpenBrowser)
        {
            Launcher.TryOpen(dashboard);
        }
    }

    private static void Row(string label, string value)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  {label,-10} ");
        Console.ResetColor();
        Console.WriteLine(value);
    }
}
