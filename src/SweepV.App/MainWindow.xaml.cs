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
        if (SweepV.Core.Platform.Elevation.IsElevated)
            Title += " (Administrator)";

        // Keep the latest chat message in view.
        viewModel.Chat.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd());
        };
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
