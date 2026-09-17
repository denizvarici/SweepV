using SweepV.App.ViewModels;
using SweepV.Core.Models;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SweepV.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        DataContext = viewModel;

        // Keep the latest chat message in view.
        viewModel.Chat.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd());
        };
    }

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.Chat.SaveApiKey(ApiKeyBox.Password);
        ApiKeyBox.Clear();
    }

    private void ApiKeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SaveApiKey_Click(sender, e);
    }

    private void ListViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: ScanNode node } &&
            DataContext is MainViewModel viewModel)
        {
            viewModel.OpenFolderCommand.Execute(node);
        }
    }
}
