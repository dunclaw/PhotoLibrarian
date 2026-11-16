// Copyright (c) Duncan Lawler. All Rights Reserved.
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Views;

public partial class EffectGalleryItem(PhotoEffect effect, string name) : ObservableObject
{
    public PhotoEffect Effect { get; } = effect;
    public string Name { get; } = name;
    public string AutomationId => $"EditorEffect{Effect}";

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }
}
