using Microsoft.UI.Xaml.Data;
using Windows.Media.Core;

namespace FilesPlusPlus.App.Converters;

public sealed class FilePathToMediaSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string filePath || string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            var uri = new UriBuilder
            {
                Scheme = Uri.UriSchemeFile,
                Host = string.Empty,
                Path = fullPath
            }.Uri;

            return MediaSource.CreateFromUri(uri);
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
