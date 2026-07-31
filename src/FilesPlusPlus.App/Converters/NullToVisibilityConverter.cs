using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace FilesPlusPlus.App.Converters;

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var invert = parameter is string s &&
            s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        var isNull = value is null;
        var visible = invert ? isNull : !isNull;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
