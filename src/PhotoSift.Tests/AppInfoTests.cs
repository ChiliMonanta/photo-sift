using PhotoSift.Desktop;

namespace PhotoSift.Tests;

public class AppInfoTests
{
    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0+3f2a1c9d", "0.1.0")]
    [InlineData("0.2.0-rc.1+abc", "0.2.0-rc.1")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    public void Build_metadata_is_stripped_from_version(string? informational, string expected) =>
        Assert.Equal(expected, AppInfo.FormatVersion(informational));

    [Fact]
    public void Window_title_includes_version() =>
        Assert.Equal($"PhotoSift {AppInfo.Version}", AppInfo.WindowTitle);
}
