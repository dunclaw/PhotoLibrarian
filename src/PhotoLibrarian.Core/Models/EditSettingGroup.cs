// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Models;

[Flags]
public enum EditSettingGroup
{
    None = 0,
    Tone = 1 << 0,
    Color = 1 << 1,
    Effect = 1 << 2,
    Detail = 1 << 3,
    ToneCurves = 1 << 4,
    ColorAdjustments = 1 << 5,
    Geometry = 1 << 6,
    Retouch = 1 << 7,
    RawDevelopment = 1 << 8,
    All = Tone | Color | Effect | Detail | ToneCurves | ColorAdjustments |
        Geometry | Retouch | RawDevelopment
}
