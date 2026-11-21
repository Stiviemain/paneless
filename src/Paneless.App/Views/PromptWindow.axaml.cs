using Avalonia.Controls;

namespace Paneless.App.Views;

/// <summary>Asks for one line of text, e.g. a group name.</summary>
public partial class PromptWindow : Window
{
    public PromptWindow()
    {
        InitializeComponent();
        YesButton.Click += (_, _) => Close(Input.Text ?? "");
        NoButton.Click += (_, _) => Close(null);
        Opened += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    public static Task<string?> AskAsync(Window owner, string title, string message, string initial)
    {
        var w = new PromptWindow();
        w.TitleText.Text = title;
        w.MessageText.Text = message;
        w.Input.Text = initial;
        return w.ShowDialog<string?>(owner);
    }
}
