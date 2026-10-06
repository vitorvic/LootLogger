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

        var startMinimized = e.Args.Contains(StartupRegistration.MinimizedArgument);
        if (startMinimized)
        {
            window.WindowState = WindowState.Minimized;
        }

        window.Show();

        if (startMinimized)
        {
            _viewModel.StartCapture();
        }

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

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.AppendAllText(Path.Combine(AppPaths.DataFolder, "erros.log"), $"{DateTime.Now:O} {e.Exception}\n\n");
        }
        catch (IOException)
        {
        }

        e.Handled = true;
    }
}
