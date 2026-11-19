using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DinamikAda.Core.Config;
using DinamikAda.Platform.Win32.Interop;
using DinamikAda.Platform.Win32.Windowing;
using DinamikAda.App.Views.DockWidgets;

namespace DinamikAda.App.Views;

/// <summary>Top/bottom screen edge for the dock, and dragging the dock by an empty spot to switch edges.</summary>
public partial class DockWindow
{
    /// <summary>Leaves room for the island at the top centre when the dock sits on the top edge.</summary>
    private const double TopEdgeIslandInsetDip = 40.0;
    private const double EdgeDragThresholdPx = 14.0;

    private bool _isEdgeDragPending;
    private bool _isEdgeDragging;
    private NativeMethods.POINT _edgeDragStartCursor;
    private int _edgeDragStartTopPx;

    private bool IsDockAtTop => _orch.Config.DockEdge == DockScreenEdge.Top;

    private int DockTopInsetPx() =>
        IsDockAtTop ? (int)Math.Round(TopEdgeIslandInsetDip * Math.Max(1.0, _activeMonitor.DpiScale)) : 0;

    /// <summary>Aligns the capsule and flips every popup to open away from the dock's edge.</summary>
    private void ApplyDockEdgeLayout()
    {
        bool top = IsDockAtTop;
        DockPillContainer.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _appliedHostMargin = new Thickness(double.NaN);
        ApplyCapsuleSpread(); // capsule + row margins for this edge, including any live magnification spread

        var placement = top ? PlacementMode.Bottom : PlacementMode.Top;
        ClockCalendarPopup.Placement = placement;
        ClockCalendarPopup.VerticalOffset = top ? 20 : -20;
        TrayOverflowPopup.Placement = placement;
        TrayOverflowPopup.VerticalOffset = top ? 20 : -20;
        StartLaunchPopup.Placement = placement;
        StartLaunchPopup.VerticalOffset = top ? 24 : -24;
    }

    public void SetDockEdge(DockScreenEdge edge)
    {
        if (_isClosed) return;
        if (_orch.Config.DockEdge == edge)
        {
            UpdateHostPlacement();
            return;
        }
        CloseWidgetCard();
        StartLaunchPopup.IsOpen = false;
        TrayOverflowPopup.IsOpen = false;
        ClockCalendarPopup.IsOpen = false;
        _orch.ConfigStore.Update(_orch.Config, c => c.DockEdge = edge);
        ApplyConfigAndSync();
        PlayEdgeArriveAnimation();
    }

    private void PlayEdgeArriveAnimation()
    {
        if (_isSmokeOrAuditMode) return;
        var dur = TimeSpan.FromMilliseconds(320);
        var ease = Motion.Soft;
        DockItemsHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, dur));
        var tt = new System.Windows.Media.TranslateTransform(0, IsDockAtTop ? -18 : 18);
        DockItemsHost.RenderTransform = tt;
        tt.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(IsDockAtTop ? -18 : 18, 0, dur) { EasingFunction = ease });
    }

    // ---- Drag the dock by an empty spot ----------------------------------------------------------

    private bool IsEmptyDockSpot(object originalSource)
    {
        var d = originalSource as DependencyObject;
        while (d is not null && !ReferenceEquals(d, DockPillContainer))
        {
            if (d is FrameworkElement fe && _slots.Any(s => ReferenceEquals(s.Container, fe))) return false;
            if (d is FrameworkElement el && ReferenceEquals(el, _clockChip)) return false;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        }
        return d is not null;
    }

    private void OnDockPillMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isSmokeOrAuditMode || _hwnd == 0 || !IsEmptyDockSpot(e.OriginalSource)) return;
        if (!NativeMethods.GetCursorPos(out _edgeDragStartCursor)) return;
        if (!NativeMethods.GetWindowRect(_hwnd, out var rect)) return;
        _edgeDragStartTopPx = rect.Top;
        _isEdgeDragPending = true;
        DockPillContainer.CaptureMouse();
        e.Handled = true;
    }

    private void OnDockPillEdgeDragMove(object sender, MouseEventArgs e)
    {
        if (!_isEdgeDragPending) return;
        if (e.LeftButton != MouseButtonState.Pressed || !NativeMethods.GetCursorPos(out var pt))
        {
            CancelEdgeDrag();
            return;
        }

        int dy = pt.Y - _edgeDragStartCursor.Y;
        if (!_isEdgeDragging && Math.Abs(dy) < EdgeDragThresholdPx) return;
        if (!_isEdgeDragging)
        {
            _isEdgeDragging = true;
            CloseWidgetCard();
            DockTooltipBorder.Opacity = 0;
            UpdateMagnificationTargets(null);
            DockPillContainer.Opacity = 0.9;
        }

        var bounds = DockLayoutCalculator.ComputeHostWindowBounds(_activeMonitor, HostHeightDip);
        int minTop = _activeMonitor.TopPx;
        int maxTop = _activeMonitor.TopPx + _activeMonitor.HeightPx - bounds.HeightPx;
        int newTop = Math.Clamp(_edgeDragStartTopPx + dy, minTop, maxTop);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, bounds.LeftPx, newTop, bounds.WidthPx, bounds.HeightPx,
            NativeMethods.SWP_NOACTIVATE);
        SyncDockGlass();
        e.Handled = true;
    }

    private void OnDockPillMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isEdgeDragPending) return;
        bool wasDragging = _isEdgeDragging;
        NativeMethods.GetCursorPos(out var pt);
        CancelEdgeDrag();
        if (!wasDragging) return;
        e.Handled = true;

        var edge = DockLayoutCalculator.ShouldSnapDockToTop(pt.Y, _activeMonitor) ? DockScreenEdge.Top : DockScreenEdge.Bottom;
        SetDockEdge(edge);
    }

    private void CancelEdgeDrag()
    {
        bool wasDragging = _isEdgeDragging;
        _isEdgeDragPending = false;
        _isEdgeDragging = false;
        DockPillContainer.Opacity = 1.0;
        if (DockPillContainer.IsMouseCaptured) DockPillContainer.ReleaseMouseCapture();
        if (wasDragging) UpdateHostPlacement();
    }

    private void OnDockPillMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsEmptyDockSpot(e.OriginalSource)) return;
        e.Handled = true;
        HandleSlotContextMenu(null);
    }
}
