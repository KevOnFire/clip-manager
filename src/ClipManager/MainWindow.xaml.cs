using System.Windows;
using ClipManager.Views;
using Wpf.Ui.Controls;

namespace ClipManager;

public partial class MainWindow : FluentWindow
{
    private readonly LibraryView _library = new();
    private readonly SettingsView _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        ShowLibrary();
    }

    private void LibraryNav_Click(object sender, RoutedEventArgs e) => ShowLibrary();

    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ShowLibrary()
    {
        PageHost.Content = _library;
        LibraryNav.Appearance = ControlAppearance.Primary;
        SettingsNav.Appearance = ControlAppearance.Transparent;
        _ = _library.RefreshAsync();
    }

    private void ShowSettings()
    {
        PageHost.Content = _settings;
        LibraryNav.Appearance = ControlAppearance.Transparent;
        SettingsNav.Appearance = ControlAppearance.Primary;
        _settings.Refresh();
    }
}
