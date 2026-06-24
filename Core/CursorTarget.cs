using System;
using SoftScroll.Native;

namespace SoftScroll.Core;

/// <summary>
/// Tracks where an injected wheel will be delivered (the window/region under the cursor) so an
/// engine can drop residual scroll/momentum/zoom the moment the cursor leaves that target —
/// instead of leaking the tail into a different scroll region or window.
///
/// An injected <c>MOUSEEVENTF_WHEEL</c> is delivered by Windows to whatever window is under the
/// cursor at SendInput time. So the precise "did the delivery target change" signal is
/// <see cref="NativeMethods.WindowFromPoint"/>. That alone catches window switches and separate
/// child-HWND regions, but NOT two scroll regions that share one HWND (Chromium/Electron panes,
/// scrollable divs) — those only differ by cursor position, so we also treat a large cursor move
/// as a target change.
/// </summary>
internal static class CursorTarget
{
    /// <summary>
    /// Captures the current wheel-delivery target (cursor position + window under it).
    /// Returns false (and leaves the scroll unguarded) only if the cursor can't be read.
    /// </summary>
    public static bool TryCapture(out IntPtr hwnd, out NativeMethods.POINT pos)
    {
        if (NativeMethods.GetCursorPos(out pos))
        {
            hwnd = NativeMethods.WindowFromPoint(pos);
            return true;
        }
        hwnd = IntPtr.Zero;
        return false;
    }

    /// <summary>
    /// True if the cursor has moved to a different delivery target since the anchor was captured:
    /// either the window under it changed, or it moved more than <paramref name="moveThresholdPx"/>
    /// (a likely region switch inside a single-HWND app). If the cursor can't be read we report
    /// "unchanged" so a transient failure never aborts a legitimate glide.
    /// </summary>
    public static bool HasChanged(IntPtr anchorHwnd, NativeMethods.POINT anchorPos, int moveThresholdPx)
    {
        if (!NativeMethods.GetCursorPos(out var cur))
            return false;

        long dx = cur.x - anchorPos.x;
        long dy = cur.y - anchorPos.y;
        if (dx * dx + dy * dy > (long)moveThresholdPx * moveThresholdPx)
            return true;

        return NativeMethods.WindowFromPoint(cur) != anchorHwnd;
    }
}
