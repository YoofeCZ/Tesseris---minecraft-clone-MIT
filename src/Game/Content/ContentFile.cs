namespace Tesseris.Game.Content;

/// <summary>A file discovered inside one content source.</summary>
public sealed record ContentFile(
    ResourceId Id,
    string FullPath,
    string RelativePath,
    ContentSource Source);
