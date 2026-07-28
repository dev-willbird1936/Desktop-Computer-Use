// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Native;

namespace ShadowUse.Tools;

internal static class WindowCoordinateMapper
{
    public static NativeMethods.POINT RebaseFromSnapshot(
        NativeMethods.RECT snapshotBounds,
        NativeMethods.RECT currentBounds,
        int screenX,
        int screenY)
    {
        bool insideSnapshot = screenX >= snapshotBounds.Left
            && screenX < snapshotBounds.Right
            && screenY >= snapshotBounds.Top
            && screenY < snapshotBounds.Bottom;
        return insideSnapshot
            ? new NativeMethods.POINT
            {
                X = screenX + currentBounds.Left - snapshotBounds.Left,
                Y = screenY + currentBounds.Top - snapshotBounds.Top,
            }
            : new NativeMethods.POINT { X = screenX, Y = screenY };
    }
}
