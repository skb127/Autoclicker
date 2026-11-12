using System;
using Autoclicker.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Autoclicker.Views;

public partial class MainView : UserControl
{
    // Minimum pointer travel distance (in pixels) before a press turns into a drag operation.
    // Prevents normal item selection clicks from accidentally initiating a drag.
    private const double DragDistanceThresholdPx = 6.0;

    // In-process data format carrying the source index of the dragged coordinate.
    // We transfer the index rather than the Coordinate value because Coordinate is a record
    // and duplicate coordinates in the pattern would otherwise be indistinguishable.
    private static readonly DataFormat<string> PatternIndexFormat =
        DataFormat.CreateStringApplicationFormat("autoclicker-pattern-index");

    private Point? _dragStartPoint;
    private int _dragSourceIndex = -1;
    private bool _isDragging;

    public MainView()
    {
        InitializeComponent();
        SetupPatternDragAndDrop();
    }

    private void SetupPatternDragAndDrop()
    {
        // Use RoutingStrategies.Tunnel on PointerPressed so we still observe the press
        // even after ListBoxItem marks it as handled for selection.
        PatternListBox.AddHandler(
            InputElement.PointerPressedEvent,
            OnPatternListPointerPressed,
            RoutingStrategies.Tunnel);

        PatternListBox.AddHandler(
            InputElement.PointerMovedEvent,
            OnPatternListPointerMoved,
            RoutingStrategies.Tunnel);

        PatternListBox.AddHandler(
            InputElement.PointerReleasedEvent,
            OnPatternListPointerReleased,
            RoutingStrategies.Tunnel);

        PatternListBox.AddHandler(DragDrop.DragOverEvent, OnPatternListDragOver);
        PatternListBox.AddHandler(DragDrop.DropEvent, OnPatternListDrop);
    }

    private void OnPatternListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ResetDragState();

        if (DataContext is not MainViewModel { IsClicking: false })
        {
            return;
        }

        var point = e.GetCurrentPoint(PatternListBox);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual sourceVisual)
        {
            var listBoxItem = sourceVisual.FindAncestorOfType<ListBoxItem>();
            if (listBoxItem is not null)
            {
                int index = PatternListBox.IndexFromContainer(listBoxItem);
                if (index >= 0)
                {
                    _dragStartPoint = point.Position;
                    _dragSourceIndex = index;
                }
            }
        }
    }

    private async void OnPatternListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging || _dragStartPoint is null || _dragSourceIndex < 0)
        {
            return;
        }

        if (DataContext is not MainViewModel { IsClicking: false })
        {
            ResetDragState();
            return;
        }

        var currentPoint = e.GetCurrentPoint(PatternListBox);
        if (!currentPoint.Properties.IsLeftButtonPressed)
        {
            ResetDragState();
            return;
        }

        double deltaX = currentPoint.Position.X - _dragStartPoint.Value.X;
        double deltaY = currentPoint.Position.Y - _dragStartPoint.Value.Y;
        if (double.Hypot(deltaX, deltaY) < DragDistanceThresholdPx)
        {
            return;
        }

        int sourceIndex = _dragSourceIndex;
        _isDragging = true;

        try
        {
            // Note: Do not dispose DataTransfer; Avalonia disposes it automatically when the drag completes.
            var dataTransfer = new DataTransfer();
            dataTransfer.Add(DataTransferItem.Create(PatternIndexFormat, sourceIndex.ToString()));

            await DragDrop.DoDragDropAsync(e, dataTransfer, DragDropEffects.Move);
        }
        finally
        {
            ResetDragState();
        }
    }

    private void OnPatternListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
        {
            ResetDragState();
        }
    }

    private void OnPatternListDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel { IsClicking: false } &&
            e.DataTransfer.Contains(PatternIndexFormat))
        {
            e.DragEffects = DragDropEffects.Move;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnPatternListDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel { IsClicking: false } vm)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        string? indexText = e.DataTransfer.TryGetValue(PatternIndexFormat);
        if (!int.TryParse(indexText, out int fromIndex) ||
            fromIndex < 0 ||
            fromIndex >= vm.ClickPatterns.Count)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        int targetIndex = ComputeDropTargetIndex(e, vm.ClickPatterns.Count, fromIndex);
        vm.MovePattern(fromIndex, targetIndex);

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>
    /// Determines the destination index inside the ListBox based on which ListBoxItem
    /// is under the pointer and whether the pointer is in its top or bottom half.
    /// </summary>
    private int ComputeDropTargetIndex(DragEventArgs e, int itemCount, int fromIndex)
    {
        if (itemCount <= 1)
        {
            return 0;
        }

        if (e.Source is Visual sourceVisual)
        {
            var targetItem = sourceVisual.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (targetItem is not null)
            {
                int itemIndex = PatternListBox.IndexFromContainer(targetItem);
                if (itemIndex >= 0)
                {
                    var posInItem = e.GetPosition(targetItem);
                    bool insertAfter = posInItem.Y > targetItem.Bounds.Height / 2.0;

                    int insertionSlot = insertAfter ? itemIndex + 1 : itemIndex;
                    if (fromIndex < insertionSlot)
                    {
                        insertionSlot--;
                    }

                    return Math.Clamp(insertionSlot, 0, itemCount - 1);
                }
            }
        }

        // Fallback if dropped on empty space inside the ListBox: place at the start or end
        var posInList = e.GetPosition(PatternListBox);
        return posInList.Y <= PatternListBox.Bounds.Height / 2.0 ? 0 : itemCount - 1;
    }

    private void ResetDragState()
    {
        _dragStartPoint = null;
        _dragSourceIndex = -1;
        _isDragging = false;
    }
}
