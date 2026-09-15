using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SweepV.Core.Ai;

namespace SweepV.App.Converters
{
    public sealed class BytesToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is long bytes ? GeminiFolderAdvisor.FormatBytes(bytes) : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
