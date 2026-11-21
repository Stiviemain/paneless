using Paneless.App.Localization;
using Velopack;
using Velopack.Sources;

namespace Paneless.App;

/// <summary>Updates from the GitHub releases of Paneless (or another source set in Settings).
/// A new version is downloaded in the background and installed when Paneless closes.</summary>
public static class Updates
{
    public const string GitHubRepo = AppLinks.Repository;
    static bool _pending;

    public sealed record Check(bool Downloaded, string Message);

    public static async Task<Check> CheckAsync(AppServices s, bool interactive)
    {
        if (_pending) return new(true, Loc.T("An update is ready: it’s installed when you close Paneless."));
        try
        {
            var custom = s.Data.Settings.UpdateSource.Trim();
            var um = custom.Length > 0
                ? new UpdateManager(custom)
                : new UpdateManager(new GithubSource(GitHubRepo, accessToken: null, prerelease: s.Data.Settings.BetaUpdates));
            if (!um.IsInstalled) return new(false, Loc.T("Only installed copies of Paneless can update themselves (this one runs from a build folder)."));
            var info = await um.CheckForUpdatesAsync();
            if (info == null) return new(false, Loc.F("Paneless {0} is the newest version.", AppServices.Version));
            await um.DownloadUpdatesAsync(info);
            um.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false);
            _pending = true;
            return new(true, Loc.F("Version {0} is downloaded. It’s installed when you close Paneless.", info.TargetFullRelease.Version));
        }
        catch (Exception ex)
        {
            return new(false, interactive ? Loc.F("Couldn’t check for updates: {0}", ex.Message) : "");
        }
    }
}
