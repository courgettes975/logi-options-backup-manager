using System.Collections.Specialized;
using System.Windows;

namespace LogiBackup.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = (MainViewModel)DataContext;

        // Keep the last log line visible.
        vm.Log.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };

        // Analyse the PC as soon as the window is shown.
        Loaded += (_, _) => vm.DetectCommand.Execute(null);
    }
}
