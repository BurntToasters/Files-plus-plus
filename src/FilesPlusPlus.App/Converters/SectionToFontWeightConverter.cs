using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Data;
using Windows.UI.Text;

namespace FilesPlusPlus.App.Converters;

public sealed class SectionToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool isSection && isSection ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
