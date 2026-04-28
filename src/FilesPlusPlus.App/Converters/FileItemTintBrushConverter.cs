using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace FilesPlusPlus.App.Converters;

public sealed class FileItemTintBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is bool isDirectory && isDirectory
            ? "ShellFolderIconBrush"
            : "ShellFileIconBrush";

        if (Application.Current?.Resources is { } resources && resources.TryGetValue(key, out var brush) && brush is Brush)
        {
            return brush;
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Black);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
