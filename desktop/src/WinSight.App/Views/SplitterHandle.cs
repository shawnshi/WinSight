using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace WinSight.App.Views;

/// <summary>
/// A thin vertical splitter with a wide hit area: drag with the pointer, nudge with Left/Right, reset with a double tap.
/// It only reports pixel deltas; the owner decides what resizes.
/// </summary>
public sealed class SplitterHandle : Grid
{
    private double lastX;
    private bool dragging;

    public event EventHandler<double>? Dragged;
    public event EventHandler? DragCompleted;
    public event EventHandler? ResetRequested;

    public SplitterHandle()
    {
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)); // transparent but hit-testable
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        AutomationProperties.SetName(this, "调整结果列表宽度");
        AutomationProperties.SetHelpText(this, "左右方向键调整宽度，双击恢复默认");
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => Finish();
        DoubleTapped += (_, _) => ResetRequested?.Invoke(this, EventArgs.Empty);
        KeyDown += OnKeyDown;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        dragging = CapturePointer(e.Pointer);
        lastX = e.GetCurrentPoint(this).Position.X;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!dragging) return;
        var x = e.GetCurrentPoint(this).Position.X;
        var delta = x - lastX; // the handle moves with the column edge, so x stays comparable between events
        if (Math.Abs(delta) < 0.5) return;
        Dragged?.Invoke(this, delta);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        Finish();
        e.Handled = true;
    }

    private void Finish()
    {
        if (!dragging) return;
        dragging = false;
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var step = e.Key switch { VirtualKey.Left => -16d, VirtualKey.Right => 16d, _ => 0d };
        if (step == 0) return;
        Dragged?.Invoke(this, step);
        DragCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
