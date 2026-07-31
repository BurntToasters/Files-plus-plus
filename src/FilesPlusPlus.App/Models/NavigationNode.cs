using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FilesPlusPlus.App.Models;

// Why: Section headers are non-navigable; folder nodes lazy-load children on expand.
public sealed partial class NavigationNode : ObservableObject
{
    public NavigationNode(
        string label,
        string? fullPath,
        string glyph,
        bool isSection = false,
        bool canHaveChildren = false)
    {
        Label = label;
        FullPath = fullPath;
        Glyph = glyph;
        IsSection = isSection;
        HasUnrealizedChildren = canHaveChildren;
        AreChildrenLoaded = !canHaveChildren;
    }

    public string Label { get; }

    public string? FullPath { get; }

    public string Glyph { get; }

    public bool IsSection { get; }

    public bool IsNavigable => !IsSection && !string.IsNullOrWhiteSpace(FullPath);

    public ObservableCollection<NavigationNode> Children { get; } = new();

    public bool AreChildrenLoaded { get; set; }

    [ObservableProperty]
    private bool _hasUnrealizedChildren;

    [ObservableProperty]
    private bool _isExpanded;
}
