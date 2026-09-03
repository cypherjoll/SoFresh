using System.Globalization;
using System.Windows.Data;

namespace SoFresh.App.Converters;

public sealed class BytesToReadableSizeConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes || bytes < 0)
        {
            return "—";
        }

        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var digits = size >= 100 || unit == 0 ? 0 : 1;
        return $"{size.ToString($"N{digits}", culture)} {Units[unit]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
