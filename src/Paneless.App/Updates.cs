using Velopack;

namespace Paneless.App;

/// <summary>Update checks against the source set in Settings (a web address or a folder with Velopack releases).
/// A found update is downloaded and installed when Paneless closes.</summary>
public static class Updates
{
    static bool _pending;

    public static async Task<string> CheckAsync(AppServices s, bool interactive)
    {
        var source = s.Data.Settings.UpdateSource.Trim();
        if (source.Length == 0) return "Set where updates come from first.";
        if (_pending) return "An update is ready: it's installed when you close Paneless.";
        try
        {
            var um = new UpdateManager(source);
            if (!um.IsInstalled) return "Only installed copies of Paneless can update themselves (this one runs from a build folder).";
            var info = await um.CheckForUpdatesAsync();
            if (info == null) return $"Paneless {AppServices.Version} is the newest version.";
            await um.DownloadUpdatesAsync(info);
            um.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false);
            _pending = true;
            return $"Version {info.TargetFullRelease.Version} is downloaded. It's installed when you close Paneless.";
        }
        catch (Exception ex)
        {
            return interactive ? "Couldn't check for updates: " + ex.Message : "";
        }
    }
}
