// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Models;

/// <summary>Stable recipe identifiers. New effects must not change existing definitions.</summary>
public enum PhotoEffect
{
    None,
    BlackAndWhite,
    RedFilter,
    OrangeFilter,
    YellowFilter,
    Sepia,
    Cyanotype,
    WarmTint,
    CoolTint
}
