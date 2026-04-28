namespace FilesPlusPlus.App.Models;

public sealed record SidebarLocation(
    string Label,
    string Path,
    string Glyph,
    bool IsPinned = true);
