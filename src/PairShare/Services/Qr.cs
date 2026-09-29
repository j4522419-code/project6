using System.Text;
using QRCoder;

namespace PairShare.Services;

/// <summary>Renders QR codes as SVG (for the dashboard) and as block characters (for the console).</summary>
public static class Qr
{
    private const int QuietZone = 4; // QRCoder's module matrix includes a 4-module border

    public static string ToSvg(string text)
    {
        var modules = Modules(text);
        var size = modules.Count;
        var path = new StringBuilder();

        for (var y = 0; y < size; y++)
        {
            var row = modules[y];
            for (var x = 0; x < size;)
            {
                if (!row[x])
                {
                    x++;
                    continue;
                }

                var start = x;
                while (x < size && row[x])
                {
                    x++;
                }

                path.Append($"M{start} {y}h{x - start}v1h-{x - start}z");
            }
        }

        return $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" shape-rendering="crispEdges"><rect width="{size}" height="{size}" fill="#fff"/><path fill="#000" d="{path}"/></svg>""";
    }

    /// <summary>
    /// Two QR rows per text line using half blocks. Light modules are drawn, dark ones are
    /// left as the terminal background, so it scans correctly on a dark terminal.
    /// </summary>
    public static string ToTerminal(string text, int quietZone = 2)
    {
        var modules = Modules(text);
        var trim = QuietZone - Math.Clamp(quietZone, 0, QuietZone);
        var end = modules.Count - trim;
        var sb = new StringBuilder();

        bool Light(int x, int y) => y < end && !modules[y][x];

        for (var y = trim; y < end; y += 2)
        {
            sb.Append("  ");
            for (var x = trim; x < end; x++)
            {
                sb.Append((Light(x, y), Light(x, y + 1)) switch
                {
                    (true, true) => '█',
                    (true, false) => '▀',
                    (false, true) => '▄',
                    _ => ' ',
                });
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static List<System.Collections.BitArray> Modules(string text)
    {
        using var data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return data.ModuleMatrix;
    }
}
