using Avalonia.Controls;
using Paneless.App.Assistants;

namespace Paneless.App.Views;

/// <summary>Asks before an AI assistant changes something on a device: once, for 15 minutes, or not at all.
/// Stays on top so it's seen even when Paneless is in the notification area. Closing it means no.</summary>
public partial class AssistantConfirmWindow : Window
{
    ConfirmChoice _choice = ConfirmChoice.Deny;

    public AssistantConfirmWindow()
    {
        InitializeComponent();
        OnceButton.Click += (_, _) => { _choice = ConfirmChoice.Once; Close(); };
        For15Button.Click += (_, _) => { _choice = ConfirmChoice.For15Minutes; Close(); };
        DenyButton.Click += (_, _) => { _choice = ConfirmChoice.Deny; Close(); };
    }

    public static Task<ConfirmChoice> AskAsync(string title, string message)
    {
        var w = new AssistantConfirmWindow();
        w.TitleText.Text = title;
        w.MessageText.Text = message;
        var done = new TaskCompletionSource<ConfirmChoice>();
        w.Closed += (_, _) => done.TrySetResult(w._choice);
        w.Show();
        w.Activate();
        return done.Task;
    }
}
