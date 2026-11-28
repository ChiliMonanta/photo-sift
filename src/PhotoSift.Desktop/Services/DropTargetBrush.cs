using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PhotoSift.Desktop.Services;

/// <summary>Highlights a folder while photos are dragged over it.</summary>
public static class DropTargetBrush
{
    private static readonly IBrush Highlight = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x78, 0xD4));

    public static IValueConverter Converter { get; } =
        new FuncValueConverter<bool, IBrush>(isTarget => isTarget ? Highlight : Brushes.Transparent);
}
