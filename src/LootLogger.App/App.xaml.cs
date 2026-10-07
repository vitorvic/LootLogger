using System.IO;
using System.Windows;
using System.Windows.Threading;
using LootLogger.App.Localization;
using LootLogger.App.Services;
using LootLogger.App.ViewModels;

namespace LootLogger.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var settings = AppSettings.Load();
        Loc.Instance.SetLanguage(settings.Language);

        _viewModel = new MainViewModel(new CaptureService(), settings);
        var window = new MainWindow { DataContext = _viewModel };

        window.Show();

        // Always capturing while open, like other loggers; there is no start button.
        _viewModel.StartCapture();

        // Install a newer LootLogger if one was released.
        _ = UpdateAppAsync();

        // Pull the newest item list in the background; the built-in copy is used meanwhile.
        _ = Task.Run(async () =>
        {
            try
            {
                if (_viewModel is not null)
                {
                    await _viewModel.UpdateItemsSilentlyAsync();
                }
            }
            catch (Exception)
            {
            }
        });
    }

    private async Task UpdateAppAsync()
    {
        try
        {
            await AppUpdater.CheckAndApplyAsync(() => { if (_viewModel is not null) _viewModel.Message = Loc.Instance["Updating"]; });
        }
        catch (Exception e)
        {
            // Offline or GitHub unreachable: keep running this version.
            LogError(e);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        e.Handled = true;
    }

    private static void LogError(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.AppendAllText(Path.Combine(AppPaths.DataFolder, "erros.log"), $"{DateTime.Now:O} {exception}\n\n");
        }
        catch (IOException)
        {
        }
    }
}
