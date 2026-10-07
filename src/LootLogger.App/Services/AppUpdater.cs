using Velopack;
using Velopack.Sources;

namespace LootLogger.App.Services;

/// <summary>Installs new versions from the project's GitHub releases on its own.</summary>
public static class AppUpdater
{
    private const string ReleasesUrl = "https://github.com/vitorvic/LootLogger";

    /// <summary>
    /// Checks for a newer version and, if there is one, downloads it and restarts into it.
    /// Does nothing when running a local build that wasn't installed.
    /// </summary>
    public static async Task CheckAndApplyAsync(Action onRestarting)
    {
        var manager = new UpdateManager(new GithubSource(ReleasesUrl, null, false));
        if (!manager.IsInstalled)
        {
            return;
        }

        var update = await manager.CheckForUpdatesAsync();
        if (update is null)
        {
            return;
        }

        await manager.DownloadUpdatesAsync(update);
        onRestarting();
        manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }
}
