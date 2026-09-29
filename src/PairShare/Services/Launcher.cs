using System.Diagnostics;

namespace PairShare.Services;

/// <summary>Opens a URL or folder with the operating system's default handler.</summary>
internal static class Launcher
{
    public static bool TryOpen(string target)
    {
        try
        {
            ProcessStartInfo info;
            if (OperatingSystem.IsWindows())
            {
                info = new ProcessStartInfo(target) { UseShellExecute = true };
            }
            else
            {
                info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                info.ArgumentList.Add(target);
            }

            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
