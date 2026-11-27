using System.Reflection;

namespace PhotoSift.Desktop;

/// <summary>Application name and version as stamped at build time (release builds pass -p:Version=...).</summary>
public static class AppInfo
{
    public const string Name = "PhotoSift";

    public static string Version { get; } = FormatVersion(
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string WindowTitle => $"{Name} {Version}";

    /// <summary>Strips SourceLink build metadata ("0.1.0+3f2a1c") so only the user-facing version remains.</summary>
    public static string FormatVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "unknown";
        }

        var plus = informationalVersion.IndexOf('+');
        return plus >= 0 ? informationalVersion[..plus] : informationalVersion;
    }
}
