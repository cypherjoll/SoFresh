using System.Windows;
using System.Windows.Input;
using SoFresh.App.ViewModels;

namespace SoFresh.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;

    public MainWindow()
    {
        InitializeComponent();
        viewModel = new MainViewModel();
        DataContext = viewModel;
        StateChanged += OnWindowStateChanged;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = isMaximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = isMaximized ? "Restore" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(
            MaximizeButton,
            isMaximized ? "Restore window" : "Maximize window");
    }

    protected override void OnClosed(EventArgs e)
    {
        StateChanged -= OnWindowStateChanged;
        viewModel.Dispose();
        base.OnClosed(e);
    }
}
