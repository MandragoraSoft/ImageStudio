using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ImageStudio.Tabs.Edit;

public partial class EditPanel : UserControl
{
    private ListBoxItem? _dragItem;
    private Point _dragStart;

    public EditPanel()
    {
        InitializeComponent();
    }

    private static DependencyObject? GetParent(DependencyObject node) =>
        node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private ListBoxItem? ItemAt(object source, bool rejectButtons = false)
    {
        var node = source as DependencyObject;
        while (node is not null && node != ReferenceList)
        {
            if (rejectButtons && node is ButtonBase) return null;
            if (node is ListBoxItem item)
                return ReferenceList.ItemContainerGenerator.IndexFromContainer(item) >= 0 ? item : null;
            node = GetParent(node);
        }
        return null;
    }

    private void ReferencesMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = ItemAt(e.OriginalSource, rejectButtons: true);
        _dragStart = e.GetPosition(ReferenceList);
    }

    private void ReferencesMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(ReferenceList);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(item, new DataObject(typeof(ListBoxItem), item), DragDropEffects.Move);
    }

    private void ReferencesMouseUp(object sender, MouseButtonEventArgs e) => _dragItem = null;

    private void ReferencesDragOver(object sender, DragEventArgs e)
    {
        var dragged = e.Data.GetData(typeof(ListBoxItem)) as ListBoxItem;
        var target = ItemAt(e.OriginalSource);
        e.Effects = dragged is not null && target is not null &&
                    ReferenceList.ItemContainerGenerator.IndexFromContainer(dragged) >= 0
            ? DragDropEffects.Move : DragDropEffects.None;
        if (dragged is not null && ReferenceList.ItemContainerGenerator.IndexFromContainer(dragged) >= 0 &&
            FindScrollViewer(ReferenceList) is { } scroll)
        {
            var y = e.GetPosition(scroll).Y;
            if (y < 24) scroll.LineUp();
            else if (y > scroll.ActualHeight - 24) scroll.LineDown();
        }
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private void ReferencesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ListBoxItem)) is ListBoxItem dragged && ItemAt(e.OriginalSource) is { } target)
        {
            var from = ReferenceList.ItemContainerGenerator.IndexFromContainer(dragged);
            var to = ReferenceList.ItemContainerGenerator.IndexFromContainer(target);
            if (ReferenceList.Tag is ICommand command && command.CanExecute(new[] { from, to }))
                command.Execute(new[] { from, to });
        }
        e.Handled = true;
    }
}
