using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FilesPlusPlus.App.Models;

/// <summary>
/// A node in the navigation pane tree. Nodes are either non-navigable section headers
/// (Quick access, Libraries, This PC) or folder/drive entries that lazily load their
/// child folders when expanded.
/// </summary>
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

    /// <summary>True once the real child folders have been enumerated.</summary>
    public bool AreChildrenLoaded { get; set; }

    [ObservableProperty]
    private bool _hasUnrealizedChildren;

    [ObservableProperty]
    private bool _isExpanded;
}
