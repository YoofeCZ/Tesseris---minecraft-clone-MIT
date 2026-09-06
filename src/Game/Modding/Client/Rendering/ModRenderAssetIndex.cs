using Tesseris.Game.Content;
using Tesseris.ModApi;
using ModResourceId = Tesseris.ModApi.ResourceId;

namespace Tesseris.Game.Modding.Client.Rendering;

internal sealed class ModRenderAssetIndex
{
    private readonly ContentAssetResolver assets;
    private readonly IReadOnlyDictionary<ModResourceId, string> models;

    public ModRenderAssetIndex(ContentCatalog catalog, ContentAssetResolver assets)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
        models = catalog.GetFiles("models")
            .ToDictionary(file => new ModResourceId(file.Id.ToString()), file => file.FullPath);
    }

    internal string? ResolveTexture(ModResourceId id)
    {
        string? candidate = assets.ResolveTexturePath(id.Value);
        return candidate is not null && File.Exists(candidate) ? candidate : null;
    }

    internal string? ResolveModel(ModResourceId id) => models.GetValueOrDefault(id);
}
