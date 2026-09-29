namespace PairShare.Services;

/// <summary>Short, human-friendly activity lines in the host's console window.</summary>
public sealed class ActivityLog(AppOptions options)
{
    private readonly object _gate = new();

    public void Info(string message) => Write("•", message, ConsoleColor.Gray);

    public void Success(string message) => Write("✓", message, ConsoleColor.Green);

    public void Warn(string message) => Write("!", message, ConsoleColor.Yellow);

    public void Transfer(string arrow, string message) => Write(arrow, message, ConsoleColor.Cyan);

    private void Write(string symbol, string message, ConsoleColor color)
    {
        if (options.Quiet)
        {
            return;
        }

        lock (_gate)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  {DateTime.Now:HH:mm:ss}  ");
            Console.ForegroundColor = color;
            Console.Write(symbol);
            Console.ResetColor();
            Console.WriteLine($" {message}");
        }
    }
}

internal static class Sizes
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
