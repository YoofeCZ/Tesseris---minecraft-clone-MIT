using System.Text.Json;
using OpenTK.Mathematics;
using Tesseris.Game.Entities;

namespace Tesseris.Game.World;

/// <summary>
/// Data-driven bridge between the Mobs Redo asset manifest and Tesseris' dynamic mob mesh.
/// Gameplay remains in <see cref="MobDefinitions"/>; this registry owns only visual assets.
/// </summary>
public sealed class MobVisualRegistry
{
    private readonly Dictionary<string, Visual> visuals = new(StringComparer.Ordinal);
    private readonly HashSet<string> textureNames = new(StringComparer.Ordinal);

    private MobVisualRegistry()
    {
    }

    public IReadOnlyCollection<string> TextureNames => textureNames;

    public static MobVisualRegistry ReadManifest(string manifestPath) => ReadManifests(manifestPath);

    /// <summary>
    /// Načte několik manifestů do jednoho rejstříku. Balíky mají různé licence — Mobs Redo je
    /// smíšený a jeho textury ovcí jsou nekomerční, kdežto Animalia je celá MIT — takže si každý
    /// manifest nese vlastní příznak a kontroluje se proti vlastnímu <c>usage</c>.
    /// </summary>
    public static MobVisualRegistry ReadManifests(params string[] manifestPaths)
    {
        ArgumentNullException.ThrowIfNull(manifestPaths);
        var registry = new MobVisualRegistry();
        foreach (string path in manifestPaths) registry.ReadInto(path);

        // Stable Tesseris IDs keep old saves working while using the exact imported visuals.
        if (registry.visuals.TryGetValue("mobs_animal:sheep_white", out Visual? white))
            registry.visuals["tesseris:sheep"] = white;
        return registry;
    }

    private void ReadInto(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = document.RootElement;
        string usage = root.TryGetProperty("usage", out JsonElement usageElement)
            ? usageElement.GetString() ?? string.Empty
            : string.Empty;

        // Balík označený jako testovací se nesmí tvářit komerčně. Nejde o celý Mobs Redo:
        // jediná skutečně nekomerční položka jsou textury ovcí ze Summer Field packu
        // (references/mobs_animal/license.txt:95, CC BY-SA 4.0 NC). Zbytek je MIT, CC0,
        // WTFPL a CC BY-SA, tedy komerčně použitelný — jen s uvedením autorů.
        if (usage.Equals("test-only", StringComparison.Ordinal)
            && root.GetProperty("commercialDistributionAllowed").GetBoolean())
        {
            throw new InvalidDataException($"Manifest '{manifestPath}' je testovací, ale tvrdí komerční užití.");
        }

        foreach (JsonElement mob in root.GetProperty("mobs").EnumerateArray())
        {
            JsonElement visual = mob.GetProperty("visual");
            string type = visual.GetProperty("type").GetString() ?? "mesh";
            string? model = visual.TryGetProperty("model", out JsonElement modelElement)
                ? NormaliseAssetPath(modelElement.GetString())
                : null;
            float scale = ReadScale(visual);
            string[] textures = visual.GetProperty("textures").EnumerateArray()
                .Select(value => TextureName(value.GetString()))
                .ToArray();
            foreach (string texture in textures) textureNames.Add(texture);

            B3dAnimationProfile profile = ReadAnimation(mob.GetProperty("animation"));
            foreach (JsonElement idElement in mob.GetProperty("luaIds").EnumerateArray())
            {
                string id = idElement.GetString() ?? throw new InvalidDataException("A mob ID is empty.");
                if (Forbidden(id)) throw new InvalidDataException($"Forbidden exploding mob '{id}'.");
                string[] actualTextures = id.StartsWith("mobs_animal:sheep_", StringComparison.Ordinal)
                    ? [SheepTexture(id)]
                    : textures;
                foreach (string texture in actualTextures) textureNames.Add(texture);
                visuals[id] = new Visual(type, model, actualTextures, scale, profile);
            }
        }
    }

    /// <param name="layerUvScale">
    /// Jakou část dlaždice atlasu zabírá skin dané vrstvy. Skiny se do dlaždice zvětšují jen
    /// celým násobkem, aby se pixel art nerozmazal, takže model si musí UV stáhnout na tu
    /// použitou část. Bez tohohle vyjde pandě 92×44 zvětšení 1,39× vodorovně a 2,91× svisle.
    /// </param>
    public void LoadModels(
        string assetsRoot, Func<string, int> textureLayer, Func<int, Vector2>? layerUvScale = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);
        ArgumentNullException.ThrowIfNull(textureLayer);

        var loaded = new Dictionary<string, B3dAnimatedModel>(StringComparer.OrdinalIgnoreCase);

        foreach ((string _, Visual visual) in visuals)
        {
            if (visual.TextureLayers is null)
            {
                visual.TextureLayers = visual.TextureNames.Select(textureLayer).ToArray();
                visual.UvScales = visual.TextureLayers
                    .Select(layer => layerUvScale?.Invoke(layer) ?? Vector2.One)
                    .ToArray();
            }
            if (!visual.Type.Equals("mesh", StringComparison.Ordinal) || visual.Model is not null || visual.ModelPath is null)
                continue;

            string path = ResolveAsset(assetsRoot, visual.ModelPath);
            string cacheKey = $"{path}|{visual.WorldScale:R}|{visual.Animation}";
            if (!loaded.TryGetValue(cacheKey, out B3dAnimatedModel? model))
            {
                try
                {
                    B3dAnimatedModel probe = B3dAnimatedModel.Load(path, visual.Animation);
                    (Vector3 minimum, _) = probe.BoundsAt(visual.Animation.Idle.Start);
                    float offsetY = -minimum.Y * visual.WorldScale;
                    model = B3dAnimatedModel.Load(
                        path, visual.Animation, visual.WorldScale, new Vector3(0f, offsetY, 0f));
                }
                catch (InvalidDataException exception)
                {
                    throw new InvalidDataException($"Failed to load imported mob model '{path}'.", exception);
                }
                loaded.Add(cacheKey, model);
            }

            visual.Model = model;
        }

    }

    /// <summary>Má tenhle druh naimportovaný model, nebo spadne na náhradní kvádr?</summary>
    public bool HasVisual(string id) =>
        !string.IsNullOrEmpty(id) && visuals.TryGetValue(id, out Visual? visual)
        && visual.TextureLayers is { Length: > 0 }
        && (visual.Model is not null || visual.Type.Equals("sprite", StringComparison.Ordinal));

    public bool Append(MeshBuffer mesh, AnimalEntity entity)
    {
        MobDefinition definition = MobDefinitions.For(entity);
        if (!visuals.TryGetValue(definition.Id, out Visual? visual)
            && !visuals.TryGetValue(definition.SourceId, out visual))
            return false;
        if (visual.TextureLayers is not { Length: > 0 }) return false;

        int slot = (int)((ulong)entity.Id % (uint)visual.TextureLayers.Length);
        float layer = visual.TextureLayers[slot];
        Vector2 uvScale = visual.UvScales is { Length: > 0 } scales && slot < scales.Length
            ? scales[slot]
            : Vector2.One;
        if (visual.Model is not null)
        {
            visual.Model.Append(mesh, entity, layer, uvScale);
            return true;
        }
        if (visual.Type.Equals("sprite", StringComparison.Ordinal))
        {
            AppendCrossedSprite(mesh, entity, layer, visual.WorldScale);
            return true;
        }
        return false;
    }

    private static void AppendCrossedSprite(MeshBuffer mesh, AnimalEntity entity, float layer, float visualScale)
    {
        Vector3 centre = entity.VisualInitialized ? entity.RenderPosition : entity.Position;
        float width = Math.Max(0.25f, visualScale);
        float height = Math.Max(0.4f, visualScale);
        float y0 = centre.Y;
        float y1 = centre.Y + height;
        Vector2 uv00 = new(0f, 1f);
        Vector2 uv10 = new(1f, 1f);
        Vector2 uv11 = new(1f, 0f);
        Vector2 uv01 = new(0f, 0f);
        const float shade = 0.92f;

        foreach ((Vector3 a, Vector3 b) in new[]
        {
            (new Vector3(-width, 0f, -width), new Vector3(width, 0f, width)),
            (new Vector3(-width, 0f, width), new Vector3(width, 0f, -width)),
        })
        {
            Vector3 p0 = centre + new Vector3(a.X, y0 - centre.Y, a.Z);
            Vector3 p1 = centre + new Vector3(b.X, y0 - centre.Y, b.Z);
            Vector3 p2 = centre + new Vector3(b.X, y1 - centre.Y, b.Z);
            Vector3 p3 = centre + new Vector3(a.X, y1 - centre.Y, a.Z);
            mesh.AddQuad(p0, p1, p2, p3, uv00, uv10, uv11, uv01, layer, shade, shade, shade, shade, false);
        }
    }

    private static B3dAnimationProfile ReadAnimation(JsonElement animation)
    {
        float fps = animation.TryGetProperty("fps", out JsonElement fpsElement)
            && fpsElement.ValueKind == JsonValueKind.Number
            ? fpsElement.GetSingle()
            : 15f;
        JsonElement clips = animation.GetProperty("clips");
        B3dAnimationClip idle = Clip(clips, "stand", fps, new(0f, 0f, fps));
        B3dAnimationClip walk = Clip(clips, "walk", fps, idle);
        B3dAnimationClip run = Clip(clips, "run", fps, walk);
        B3dAnimationClip graze = Clip(clips, "replace", fps, idle);
        return new(idle, walk, run, graze);
    }

    private static B3dAnimationClip Clip(
        JsonElement clips, string name, float fps, B3dAnimationClip fallback)
    {
        if (!clips.TryGetProperty(name, out JsonElement value)) return fallback;
        float[] numbers = value.EnumerateArray().Select(number => number.GetSingle()).ToArray();
        return numbers.Length >= 2
            ? new(numbers[0], numbers[1], numbers.Length >= 3 ? numbers[2] : fps)
            : fallback;
    }

    private static float ReadScale(JsonElement visual)
    {
        if (!visual.TryGetProperty("scale", out JsonElement scale)) return 0.1f;
        float x = scale.EnumerateArray().FirstOrDefault().GetSingle();
        // Luanti's visual_size is multiplied by BS=10. B3D model units are Irrlicht units,
        // so one Tesseris block uses visual_size / 10.
        return Math.Max(0.001f, x / 10f);
    }

    private static string SheepTexture(string id)
    {
        int colourStart = id.LastIndexOf("sheep_", StringComparison.Ordinal) + "sheep_".Length;
        if (colourStart < "sheep_".Length || colourStart >= id.Length)
            throw new InvalidDataException($"Invalid coloured sheep ID '{id}'.");
        return $"mobs_sheep_{id[colourStart..]}_test_nc";
    }

    private static string TextureName(string? path) =>
        Path.GetFileNameWithoutExtension(path ?? throw new InvalidDataException("A mob texture path is empty."));

    private static string? NormaliseAssetPath(string? path) => path?.Replace('/', Path.DirectorySeparatorChar);

    private static string ResolveAsset(string assetsRoot, string path)
    {
        const string assetsPrefix = "assets" + "/";
        string normal = path.Replace('\\', '/');
        if (normal.StartsWith(assetsPrefix, StringComparison.Ordinal)) normal = normal[assetsPrefix.Length..];
        return Path.Combine(assetsRoot, normal.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool Forbidden(string value) =>
        value.Contains("creeper", StringComparison.OrdinalIgnoreCase)
        || value.Contains("explode", StringComparison.OrdinalIgnoreCase)
        || value.Contains("tree_monster6", StringComparison.OrdinalIgnoreCase);

    private sealed class Visual(
        string type,
        string? modelPath,
        string[] textureNames,
        float worldScale,
        B3dAnimationProfile animation)
    {
        public string Type { get; } = type;
        public string? ModelPath { get; } = modelPath;
        public string[] TextureNames { get; } = textureNames;
        public float WorldScale { get; } = worldScale;
        public B3dAnimationProfile Animation { get; } = animation;
        public B3dAnimatedModel? Model { get; set; }
        public int[]? TextureLayers { get; set; }
        public Vector2[]? UvScales { get; set; }
    }
}
