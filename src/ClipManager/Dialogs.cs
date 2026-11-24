using System.Windows;
using System.Windows.Controls;
using UiMessageBox = Wpf.Ui.Controls.MessageBox;
using UiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace ClipManager;

/// <summary>Kleine Dialoge im Look des Programms.</summary>
internal static class Dialogs
{
    public static async Task<bool> ConfirmAsync(string title, string text, string primaryButton)
    {
        var box = new UiMessageBox
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = primaryButton,
            CloseButtonText = "Abbrechen",
            IsSecondaryButtonEnabled = false,
        };
        return await box.ShowDialogAsync() == UiMessageBoxResult.Primary;
    }

    public static async Task ErrorAsync(string text)
    {
        var box = new UiMessageBox
        {
            Title = "Das hat nicht geklappt",
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            CloseButtonText = "OK",
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
        };
        await box.ShowDialogAsync();
    }

    /// <summary>Fragt nach einem Text. Gibt null zurück, wenn abgebrochen wurde.</summary>
    public static async Task<string?> PromptAsync(string title, string label, string initialText, string primaryButton)
    {
        var input = new TextBox { Text = initialText, MinWidth = 380 };
        input.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
        panel.Children.Add(input);

        var box = new UiMessageBox
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = primaryButton,
            CloseButtonText = "Abbrechen",
            IsSecondaryButtonEnabled = false,
        };
        return await box.ShowDialogAsync() == UiMessageBoxResult.Primary ? input.Text : null;
    }
}
