using Avalonia.Controls;
using Avalonia.Interactivity;
using Paneless.Core;

namespace Paneless.App.Views;

public sealed record ConnectionExportChoice(IConnectionExporter Exporter, ExportDisplayMode DisplayMode, string RootFolder);

/// <summary>Picks the target remote tool format, embedded/external display mode, and vault folder before saving.</summary>
public partial class ConnectionExportWindow : Window
{
    public ConnectionExportWindow()
    {
        InitializeComponent();
        FormatBox.ItemsSource = ConnectionExporters.All;
        FormatBox.SelectedIndex = 0;
        SyncFormatCapabilities();
    }

    public static async Task<ConnectionExportChoice?> AskAsync(Window owner, string summary, string defaultFolder = "Paneless")
    {
        var w = new ConnectionExportWindow();
        w.SummaryText.Text = summary;
        w.FolderBox.Text = defaultFolder;
        return await w.ShowDialog<ConnectionExportChoice?>(owner);
    }

    void OnFormatChanged(object? sender, SelectionChangedEventArgs e) => SyncFormatCapabilities();

    void SyncFormatCapabilities()
    {
        if (FormatBox.SelectedItem is not IConnectionExporter exporter) return;
        EmbeddedRadio.IsEnabled = exporter.SupportsEmbedded;
        if (!exporter.SupportsEmbedded && EmbeddedRadio.IsChecked == true)
            ExternalRadio.IsChecked = true;
    }

    void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    void OnExport(object? sender, RoutedEventArgs e)
    {
        if (FormatBox.SelectedItem is not IConnectionExporter exporter) return;
        var mode = exporter.SupportsEmbedded && EmbeddedRadio.IsChecked == true
            ? ExportDisplayMode.Embedded
            : ExportDisplayMode.External;
        Close(new ConnectionExportChoice(exporter, mode, (FolderBox.Text ?? "").Trim()));
    }
}
