using System.Windows;

namespace SweepV.App
{
    internal static class Dialogs
    {
        public static bool Confirm(string message, string title) =>
            MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

        public static void Info(string message, string title) =>
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
