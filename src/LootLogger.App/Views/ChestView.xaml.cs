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

    private void ResetOutline()
    {
        if (_outline is not null)
        {
            DropOutline.Stroke = _outline;
        }
    }
}
