using System.Windows;
using LootLogger.App.Localization;
using Forms = System.Windows.Forms;

namespace LootLogger.App.Services;

/// <summary>
/// Icon next to the clock. Closing the window hides it there so capture keeps running;
/// the icon brings it back, and its menu really exits.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Window _window;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _open = new();
    private readonly Forms.ToolStripMenuItem _exit = new();
    private bool _exiting;
    private bool _hintShown;

    public TrayIcon(Window window)
    {
        _window = window;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_open);
        menu.Items.Add(_exit);
        menu.Opening += (_, _) => Relabel();
        _open.Click += (_, _) => Show();
        _exit.Click += (_, _) => Exit();

        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico")).Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "LootLogger",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                Show();
            }
        };
        Relabel();

        window.Closing += OnClosing;
    }

    private void Relabel()
    {
        _open.Text = Loc.Instance["TrayOpen"];
        _exit.Text = Loc.Instance["TrayExit"];
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        _window.Hide();
        if (!_hintShown)
        {
            _hintShown = true;
            _icon.ShowBalloonTip(4000, "LootLogger", Loc.Instance["TrayHint"], Forms.ToolTipIcon.None);
        }
    }

    private void Show()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void Exit()
    {
        _exiting = true;
        _window.Close();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
