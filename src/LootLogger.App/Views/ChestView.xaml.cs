using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LootLogger.App.ViewModels;

namespace LootLogger.App.Views;

public partial class ChestView : UserControl
{
    private Brush? _outline;

    public ChestView()
    {
        InitializeComponent();
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        OnDragOver(sender, e);
        if (e.Effects != DragDropEffects.None)
        {
            _outline ??= DropOutline.Stroke;
            DropOutline.Stroke = (Brush) FindResource("GoldBrush");
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => ResetOutline();

    private void OnDrop(object sender, DragEventArgs e)
    {
        ResetOutline();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && DataContext is MainViewModel vm)
        {
            vm.AddFiles(files);
        }
    }

    /// <summary>Clicking an item picture writes its name under the player's items; clicking it again hides it.</summary>
    private void OnTileClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ItemTile tile } element)
        {
            return;
        }

        DependencyObject? parent = element;
        while (parent is not null && (parent is not FrameworkElement fe || fe.DataContext is not ChestCard))
        {
            parent = VisualTreeHelper.GetParent(parent);
        }

        if (parent is FrameworkElement { DataContext: ChestCard card })
        {
            card.SelectedItemText = card.SelectedItemText == tile.ToolTip ? string.Empty : tile.ToolTip;
        }
    }

    private void ResetOutline()
    {
        if (_outline is not null)
        {
            DropOutline.Stroke = _outline;
        }
    }
}
