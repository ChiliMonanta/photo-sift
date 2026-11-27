using System.Collections.Concurrent;
using Avalonia.Data.Converters;

namespace PhotoSift.Desktop.Services;

/// <summary>
/// Human-readable names for folder locations in lists. Local paths are shown as they are;
/// Google Drive folders are registered with their path when added, e.g. "Google Drive: My Drive/Photos".
/// </summary>
public static class LocationLabels
{
    private static readonly ConcurrentDictionary<string, string> Labels = new(StringComparer.Ordinal);

    public static IValueConverter Converter { get; } =
        new FuncValueConverter<string?, string>(location => location is null ? string.Empty : Get(location));

    public static void Register(string location, string label) => Labels[location] = label;

    public static string Get(string location) => Labels.TryGetValue(location, out var label) ? label : location;
}

