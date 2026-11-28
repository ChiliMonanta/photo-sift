namespace PhotoSift.Desktop.ViewModels;

internal static class Formatting
{
    public static string FileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    /// <summary>A rough remaining time for a transfer, e.g. "about 5 min left".</summary>
    public static string TimeLeft(double seconds) => seconds switch
    {
        < 60 => "less than a minute left",
        < 3600 => $"about {Math.Ceiling(seconds / 60):0} min left",
        _ => $"about {seconds / 3600:0.0} h left",
    };
}
