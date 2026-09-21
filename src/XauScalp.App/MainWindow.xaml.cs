using System.Windows;
using XauScalp.App.Core;

namespace XauScalp.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
