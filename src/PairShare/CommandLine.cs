namespace PairShare;

/// <summary>
/// Turns friendly switches like <c>--port 6000</c> into configuration keys under <c>PairShare:</c>.
/// Anything it doesn't recognise is passed through to ASP.NET Core untouched.
/// </summary>
internal static class CommandLine
{
    public sealed record Result(string[] Passthrough, Dictionary<string, string?> Settings, bool ShowHelp);

    private static readonly Dictionary<string, string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--port"] = "PairShare:Port",
        ["--dir"] = "PairShare:Dir",
        ["--address"] = "PairShare:Address",
        ["--code-minutes"] = "PairShare:CodeMinutes",
        ["--max-upload-mb"] = "PairShare:MaxUploadMb",
    };

    public static Result Parse(string[] args)
    {
        var passthrough = new List<string>();
        var settings = new Dictionary<string, string?>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help" or "/?")
            {
                return new Result([], settings, ShowHelp: true);
            }

            if (string.Equals(arg, "--no-browser", StringComparison.OrdinalIgnoreCase))
            {
                settings["PairShare:OpenBrowser"] = "false";
                continue;
            }

            var eq = arg.IndexOf('=');
            var name = eq > 0 ? arg[..eq] : arg;
            if (ValueOptions.TryGetValue(name, out var key))
            {
                var value = eq > 0 ? arg[(eq + 1)..] : (i + 1 < args.Length ? args[++i] : null);
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException($"Missing value for {name}.");
                }

                settings[key] = value;
                continue;
            }

            passthrough.Add(arg);
        }

        return new Result(passthrough.ToArray(), settings, ShowHelp: false);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            PairShare - share files between this computer and your phone over Wi-Fi.

            Usage: PairShare [options]

              --port <number>         Port to listen on (default 5050)
              --dir <path>            Folder that holds shared files (default ~/Downloads/PairShare)
              --address <ip-or-name>  Address to put in the link / QR codes (default: auto-detect)
              --code-minutes <n>      How long a pair code stays valid (default 5)
              --max-upload-mb <n>     Largest file a device may send (default 4096)
              --no-browser            Don't open the host dashboard automatically
              -h, --help              Show this help
            """);
    }
}
