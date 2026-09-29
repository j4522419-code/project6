using System.Text;

namespace PairShare.Services;

/// <summary>Makes untrusted file names safe to write on Windows, macOS and Linux.</summary>
public static class FileNames
{
    public const int MaxLength = 180;

    private static readonly HashSet<char> Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    // Bidirectional overrides can make "evil\u202Etxt.exe" display as "evilexe.txt".
    private static readonly HashSet<char> Bidi =
        ['\u200E', '\u200F', '\u202A', '\u202B', '\u202C', '\u202D', '\u202E', '\u2066', '\u2067', '\u2068', '\u2069'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? name)
    {
        name ??= "";

        // Keep only the last path segment: "../../x" and "C:\\x" become "x".
        var lastSeparator = name.LastIndexOfAny(['/', '\\']);
        if (lastSeparator >= 0)
        {
            name = name[(lastSeparator + 1)..];
        }

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (Bidi.Contains(ch))
            {
                continue;
            }

            sb.Append(char.IsControl(ch) || Invalid.Contains(ch) ? '_' : ch);
        }

        // No hidden dot-files, and Windows silently drops trailing dots and spaces.
        var cleaned = sb.ToString().Trim().Trim('.').Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "file";
        }

        var firstDot = cleaned.IndexOf('.');
        var baseName = firstDot < 0 ? cleaned : cleaned[..firstDot];
        if (Reserved.Contains(baseName))
        {
            cleaned = "_" + cleaned;
        }

        if (cleaned.Length > MaxLength)
        {
            var ext = Path.GetExtension(cleaned);
            if (ext.Length > 16)
            {
                ext = "";
            }

            var stem = cleaned[..(MaxLength - ext.Length)];
            if (char.IsHighSurrogate(stem[^1]))
            {
                stem = stem[..^1];
            }

            cleaned = stem.TrimEnd('.', ' ') + ext;
        }

        return cleaned;
    }

    /// <summary>Returns a path in <paramref name="directory"/> that doesn't exist yet: "a.txt", "a (1).txt", ...</summary>
    public static string Unique(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }
        }
    }
}
