// Copyright (c) Duncan Lawler. All Rights Reserved.
using Windows.System;

namespace PhotoLibrarian.Services;

public enum EditorComparisonMode { After, Before, SideBySide, Split }

internal static class EditorComparison
{
    internal static bool TryGetShortcut(VirtualKey key, VirtualKeyModifiers modifiers,
        bool isEditingControl, out EditorComparisonMode mode)
    {
        mode = EditorComparisonMode.After;
        if (isEditingControl || modifiers != VirtualKeyModifiers.None) return false;
        mode = key switch
        {
            VirtualKey.B => EditorComparisonMode.Before,
            VirtualKey.N => EditorComparisonMode.SideBySide,
            VirtualKey.M => EditorComparisonMode.Split,
            _ => EditorComparisonMode.After
        };
        return mode != EditorComparisonMode.After;
    }
}
