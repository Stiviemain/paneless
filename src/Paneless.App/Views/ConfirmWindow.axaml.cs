using Avalonia.Controls;

namespace Paneless.App.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
        YesButton.Click += (_, _) => Close(true);
        NoButton.Click += (_, _) => Close(false);
    }

    public static Task<bool> AskAsync(Window owner, string title, string message, string? warning, string yes = "Go ahead")
    {
        var w = new ConfirmWindow();
        w.TitleText.Text = title;
        w.MessageText.Text = message;
        w.WarningText.Text = warning ?? "";
        w.WarningText.IsVisible = !string.IsNullOrEmpty(warning);
        w.YesButton.Content = yes;
        return w.ShowDialog<bool>(owner);
    }
}
