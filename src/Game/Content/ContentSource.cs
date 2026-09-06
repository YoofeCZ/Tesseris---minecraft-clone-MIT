namespace Tesseris.Game.Content;

/// <summary>A content-pack root and the namespace owned by that pack.</summary>
public sealed record ContentSource(
    string Namespace,
    string Root,
    int LoadOrder = 0,
    bool LegacyFlat = false);
