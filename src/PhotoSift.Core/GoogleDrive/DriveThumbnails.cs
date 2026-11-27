using System.Text.RegularExpressions;

namespace PhotoSift.Core.GoogleDrive;

public static partial class DriveThumbnails
{
    private static readonly string[] TrustedHostSuffixes = [".googleusercontent.com"];
    private static readonly string[] TrustedHosts = ["drive.google.com", "docs.google.com"];

    /// <summary>Whether a thumbnail link points to Google over HTTPS, so the access token may be sent to it.</summary>
    public static bool IsTrustedLink(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        (TrustedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) ||
         TrustedHostSuffixes.Any(s => uri.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Asks for a thumbnail of at most <paramref name="size"/> pixels. Drive's links end with a size
    /// such as <c>=s220</c>; links without one are returned unchanged.
    /// </summary>
    public static string WithSize(string link, int size) => SizeSuffix().Replace(link, $"=s{size}");

    [GeneratedRegex(@"=s\d+$")]
    private static partial Regex SizeSuffix();
}
