using FilesPlusPlus.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace FilesPlusPlus.App.Converters;

public sealed class ViewModeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not FolderViewMode currentMode || parameter is not string expectedModeText)
        {
            return Visibility.Collapsed;
        }

        if (!Enum.TryParse<FolderViewMode>(expectedModeText, ignoreCase: true, out var expectedMode))
        {
            return Visibility.Collapsed;
        }

        return currentMode == expectedMode ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}