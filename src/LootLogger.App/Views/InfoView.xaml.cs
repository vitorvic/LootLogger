using System.Windows;
using System.Windows.Controls;

namespace LootLogger.App.Views;

/// <summary>Novidades, Ajuda and Sobre: plain text pages that share one view.</summary>
public partial class InfoView : UserControl
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(string), typeof(InfoView), new PropertyMetadata(string.Empty, (d, _) => ((InfoView)d).ShowMode()));

    public InfoView()
    {
        InitializeComponent();
    }

    /// <summary>"News", "Help" or "About".</summary>
    public string Mode
    {
        get => (string)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    private void ShowMode()
    {
        NewsPanel.Visibility = Mode == "News" ? Visibility.Visible : Visibility.Collapsed;
        HelpPanel.Visibility = Mode == "Help" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = Mode == "About" ? Visibility.Visible : Visibility.Collapsed;
    }
}
