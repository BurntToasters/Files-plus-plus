using Microsoft.UI.Xaml.Data;

namespace FilesPlusPlus.App.Converters;

public sealed class FileItemGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isDirectory && isDirectory)
        {
            return "\uE8B7";
        }

        return "\uE8A5";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
