using System.Reflection;

namespace PairShare;

internal static class AppInfo
{
    /// <summary>"1.2.3" (the build metadata after '+' is dropped).</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+')[0];
}
