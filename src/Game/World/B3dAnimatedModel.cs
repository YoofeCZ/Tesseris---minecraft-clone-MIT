using System.Text;
using OpenTK.Mathematics;
using Tesseris.Game.Entities;

namespace Tesseris.Game.World;

/// <summary>
/// Minimal Blitz3D skeletal-mesh reader for Luanti creature assets. B3D itself is a public-domain
/// format. The loader supports the chunks used by Animalia and Mobs Animal: NODE, MESH, VRTS, TRIS, BONE,
/// KEYS and ANIM, and performs low-count mob skinning on the CPU before the dynamic mesh upload.
/// </summary>
public sealed class B3dAnimatedModel
{
    private const int MaximumVertices = 100_000;
    private const int MaximumNodes = 512;

    private readonly List<Vertex> vertices = [];
    private readonly List<int> indices = [];
    private readonly List<Node> nodes = [];
    private readonly List<B3dTextureInfo> textures = [];
    private readonly List<B3dMaterialInfo> materials = [];
    private readonly List<B3dSubmeshInfo> submeshes = [];
    private readonly B3dAnimationProfile animation;
    private readonly float worldScale;
    private readonly Vector3 worldOffset;
    private int lastMeshStart;

    private B3dAnimatedModel(B3dAnimationProfile animation, float worldScale, Vector3 worldOffset)
    {
        this.animation = animation;
        this.worldScale = worldScale;
        this.worldOffset = worldOffset;
    }

    public int VertexCount => vertices.Count;
    public int IndexCount => indices.Count;
    public int NodeCount => nodes.Count;
    public IReadOnlyList<B3dTextureInfo> Textures => textures;
    public IReadOnlyList<B3dMaterialInfo> Materials => materials;
    public IReadOnlyList<B3dSubmeshInfo> Submeshes => submeshes;
    public int AnimationFrames { get; private set; }
    public float AnimationFps { get; private set; }

    public static B3dAnimatedModel Load(
        string path,
        B3dAnimationProfile? animation = null,
        float worldScale = 1f,
        Vector3? worldOffset = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!float.IsFinite(worldScale) || worldScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(worldScale));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var model = new B3dAnimatedModel(
            animation ?? B3dAnimationProfile.Animalia,
            worldScale,
            worldOffset ?? Vector3.Zero);
        Chunk root = ReadChunk(reader, stream.Length);
        if (root.Name != "BB3D")
            throw new InvalidDataException("The model is not a BB3D file.");
        int version = reader.ReadInt32();
        if (version is < 1 or > 2)
            throw new InvalidDataException($"Unsupported B3D version {version}.");
        model.ReadChildren(reader, root.End, parent: -1);
        if (reader.BaseStream.Position != root.End || root.End != reader.BaseStream.Length)
            throw new InvalidDataException("The B3D root chunk has an invalid length.");
        if (model.vertices.Count == 0 || model.indices.Count == 0)
            throw new InvalidDataException("The B3D model contains no renderable mesh.");
        model.ValidateMaterials();
        return model;
    }

    /// <summary>Adds one animated instance in Tesseris world coordinates.</summary>
    /// <param name="uvScale">
    /// Jakou část dlaždice atlasu skin skutečně zabírá. Skin se do dlaždice zvětšuje jen celým
    /// násobkem, aby se pixel art nerozmazal, takže zbytek dlaždice je prázdný a UV se musí
    /// stáhnout na tu použitou část. Viz <c>TextureArray.SkinScale</c>.
    /// </param>
    public void Append(MeshBuffer mesh, AnimalEntity animal, float textureLayer, Vector2 uvScale)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(animal);
        float frame = FrameFor(animal);
        Matrix4[] animated = AnimatedGlobals(frame);

        Vector3 origin = animal.VisualInitialized ? animal.RenderPosition : animal.Position;
        float yaw = animal.VisualInitialized ? animal.RenderYaw : animal.Yaw;
        foreach (B3dSubmeshInfo submesh in submeshes)
            AppendRange(mesh, origin, yaw, textureLayer, uvScale, animated, submesh.FirstIndex, submesh.IndexCount);
    }

    /// <summary>
    /// Přidá model na zadané místo v zadané póze. Nezávislé na zvířeti — tudy se kreslí
    /// postava hráče, která žádnou <see cref="AnimalEntity"/> nemá.
    /// </summary>
    /// <param name="boneOverride">
    /// Kost, která se má nad rámec animace přiklonit, a o kolik radiánů. Takhle Luanti otáčí
    /// hlavu podle pohledu hráče — `player_api` volá `set_bone_override("Head", …)` a klip
    /// chůze přitom běží dál.
    /// </param>
    public void AppendAt(
        MeshBuffer mesh, Vector3 origin, float yaw, float frame, float textureLayer, Vector2 uvScale,
        (string Bone, float Pitch)? boneOverride = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        Matrix4[] animated = AnimatedGlobals(frame, boneOverride);
        foreach (B3dSubmeshInfo submesh in submeshes)
            AppendRange(mesh, origin, yaw, textureLayer, uvScale, animated, submesh.FirstIndex, submesh.IndexCount);
    }

    public void Append(MeshBuffer mesh, AnimalEntity animal, float textureLayer) =>
        Append(mesh, animal, textureLayer, Vector2.One);

    /// <summary>Adds one material-preserving B3D submesh for renderers which batch materials separately.</summary>
    public void AppendSubmesh(MeshBuffer mesh, AnimalEntity animal, int submeshIndex, float textureLayer)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(animal);
        if ((uint)submeshIndex >= (uint)submeshes.Count)
            throw new ArgumentOutOfRangeException(nameof(submeshIndex));

        B3dSubmeshInfo submesh = submeshes[submeshIndex];
        AppendRange(
            mesh,
            animal.VisualInitialized ? animal.RenderPosition : animal.Position,
            animal.VisualInitialized ? animal.RenderYaw : animal.Yaw,
            textureLayer,
            Vector2.One,
            AnimatedGlobals(FrameFor(animal)),
            submesh.FirstIndex,
            submesh.IndexCount);
    }

    private void AppendRange(
        MeshBuffer mesh,
        Vector3 origin,
        float yaw,
        float textureLayer,
        Vector2 uvScale,
        Matrix4[] animated,
        int firstIndex,
        int indexCount)
    {
        int end = checked(firstIndex + indexCount);
        for (int triangle = firstIndex; triangle < end; triangle += 3)
        {
            Vertex a = vertices[indices[triangle]];
            Vertex b = vertices[indices[triangle + 1]];
            Vertex c = vertices[indices[triangle + 2]];
            Vector3 p0 = ToWorld(origin, yaw, Skin(a, animated));
            Vector3 p1 = ToWorld(origin, yaw, Skin(b, animated));
            Vector3 p2 = ToWorld(origin, yaw, Skin(c, animated));
            Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0);
            float shade = 0.82f;
            if (normal.LengthSquared > 1e-8f)
            {
                normal.Normalize();
                shade = 0.68f + (0.32f * MathF.Abs(normal.Y));
            }
            mesh.AddTriangle(
                p0, p1, p2, a.Uv * uvScale, b.Uv * uvScale, c.Uv * uvScale, textureLayer, shade);
        }
    }

    internal (Vector3 Minimum, Vector3 Maximum) BoundsAt(float frame)
    {
        Matrix4[] animated = AnimatedGlobals(frame);
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        foreach (Vertex vertex in vertices)
        {
            Vector3 point = Skin(vertex, animated);
            minimum = Vector3.ComponentMin(minimum, point);
            maximum = Vector3.ComponentMax(maximum, point);
        }
        return (minimum, maximum);
    }

    private float FrameFor(AnimalEntity animal)
    {
        // SMYČKA JE POLOOTEVŘENÁ: délka klipu je `end - start`, ne `end - start + 1`.
        // Irrlicht wrapuje přes `max_frame - min_frame` (irr/src/AnimSpec.cpp:16-22), takže
        // koncový snímek je jen mez a nikdy se nezobrazí. S tím `+1` jsme na konci každého
        // cyklu vzorkovali o snímek dál — tedy do mezery mezi klipy, případně rovnou do
        // sousedního klipu. U krávy to bylo 30 snímků místo 29, tedy tempo o 3,4 % vedle.
        static float Cycle(float start, float end, float time, float speed)
        {
            float length = end - start;
            if (length <= 0f) return start;
            float offset = time * speed % length;
            if (offset < 0f) offset += length;
            return start + offset;
        }

        if (animal.Activity == AnimalActivity.Graze)
            return Cycle(animation.Graze.Start, animation.Graze.End, animal.VisualTime, animation.Graze.Speed);
        if (animal.Moving)
        {
            // ČAS JE V SEKUNDÁCH, ne v krocích.
            //
            // Luanti pouští klip přes `set_animation({x=start, y=end}, frame_speed)`, kde
            // `frame_speed` jsou snímky za sekundu. Dřív se sem místo sekund posílal
            // `WalkPhase / τ`, tedy počet ušlých kroků — a ten roste s rychlostí zvířete.
            // Prchající kráva (rychlost 2) tak hnala běžecký klip o 21 snímcích rychlostí
            // 89 snímků/s, tedy přes čtyři cykly za sekundu. Odtud ty nohy točící se dokola.
            // ÚTĚK HRAJE CHŮZI, NE BĚH. V mobs_redo se ve stavu `runaway` nastaví
            // `run_velocity`, ale hned na dalším řádku `set_animation("walk")`
            // (references/mobs_redo/api.lua:2084-2085). Prchající zvíře se tedy hýbe rychle,
            // ale animuje pomalu. Běžecký klip má jen pronásledování v boji (dogfight).
            bool running = animal.Activity is AnimalActivity.Hunt;
            B3dAnimationClip clip = running ? animation.Run : animation.Walk;
            return Cycle(clip.Start, clip.End, animal.VisualTime, clip.Speed);
        }
        return Cycle(animation.Idle.Start, animation.Idle.End, animal.VisualTime, animation.Idle.Speed);
    }

    private Vector3 ToWorld(Vector3 origin, float yaw, Vector3 local)
    {
        local = (local * worldScale) + worldOffset;
        float cosine = MathF.Cos(yaw);
        float sine = MathF.Sin(yaw);
        return origin + new Vector3(
            (local.X * cosine) + (local.Z * sine),
            local.Y,
            (local.Z * cosine) - (local.X * sine));
    }

    private Vector3 Skin(Vertex vertex, Matrix4[] animated)
    {
        if (vertex.Weights.Count == 0)
            return vertex.Position;
        Vector3 result = Vector3.Zero;
        float total = 0f;
        foreach (Weight weight in vertex.Weights)
        {
            Node node = nodes[weight.Node];
            Vector3 bindLocal = Vector3.TransformPosition(vertex.Position, node.InverseBindGlobal);
            result += Vector3.TransformPosition(bindLocal, animated[weight.Node]) * weight.Strength;
            total += weight.Strength;
        }
        return total > 1e-6f ? result / total : vertex.Position;
    }

    private Matrix4[] AnimatedGlobals(float frame, (string Bone, float Pitch)? boneOverride = null)
    {
        var result = new Matrix4[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            Node node = nodes[i];
            Vector3 position = Interpolate(node.Keys, frame, key => key.Position, node.BindPosition);
            Vector3 scale = Interpolate(node.Keys, frame, key => key.Scale, node.BindScale);
            Quaternion rotation = InterpolateRotation(node.Keys, frame, node.BindRotation);
            Matrix4 local = LocalMatrix(position, scale, rotation);

            // PŘIKLONĚNÍ KOSTI NAD RÁMEC ANIMACE. Násobí se zleva, tedy v prostoru kosti před
            // její vlastní transformací — hlava se tak nakloní kolem svého krku, ne kolem
            // počátku modelu, a děti kosti se otočí s ní.
            if (boneOverride is { } bone
                && node.Name.Equals(bone.Bone, StringComparison.OrdinalIgnoreCase))
            {
                local = Matrix4.CreateRotationX(bone.Pitch) * local;
            }

            result[i] = node.Parent >= 0 ? local * result[node.Parent] : local;
        }
        return result;
    }

    private static Vector3 Interpolate(
        List<KeyFrame> keys,
        float frame,
        Func<KeyFrame, Vector3?> selector,
        Vector3 fallback)
    {
        KeyFrame? previous = null;
        KeyFrame? next = null;
        foreach (KeyFrame key in keys)
        {
            if (selector(key) is null) continue;
            if (key.Frame <= frame) previous = key;
            if (key.Frame >= frame) { next = key; break; }
        }
        previous ??= next;
        next ??= previous;
        if (previous is null || next is null) return fallback;
        Vector3 from = selector(previous) ?? fallback;
        Vector3 to = selector(next) ?? from;
        float span = next.Frame - previous.Frame;
        float amount = span <= 1e-6f ? 0f : Math.Clamp((frame - previous.Frame) / span, 0f, 1f);
        return Vector3.Lerp(from, to, amount);
    }

    private static Quaternion InterpolateRotation(List<KeyFrame> keys, float frame, Quaternion fallback)
    {
        KeyFrame? previous = null;
        KeyFrame? next = null;
        foreach (KeyFrame key in keys)
        {
            if (key.Rotation is null) continue;
            if (key.Frame <= frame) previous = key;
            if (key.Frame >= frame) { next = key; break; }
        }
        previous ??= next;
        next ??= previous;
        if (previous?.Rotation is not Quaternion from || next?.Rotation is not Quaternion to)
            return fallback;
        float span = next.Frame - previous.Frame;
        float amount = span <= 1e-6f ? 0f : Math.Clamp((frame - previous.Frame) / span, 0f, 1f);
        return Quaternion.Slerp(from, to, amount).Normalized();
    }

    private void ReadChildren(BinaryReader reader, long end, int parent)
    {
        while (reader.BaseStream.Position < end)
        {
            Chunk chunk = ReadChunk(reader, end);
            switch (chunk.Name)
            {
                case "NODE": ReadNode(reader, chunk, parent); break;
                case "TEXS": ReadTextures(reader, chunk); break;
                case "BRUS": ReadMaterials(reader, chunk); break;
                default: reader.BaseStream.Position = chunk.End; break;
            }
        }
        if (reader.BaseStream.Position != end)
            throw new InvalidDataException("A B3D child chunk exceeds its parent.");
    }

    private void ReadNode(BinaryReader reader, Chunk chunk, int parent)
    {
        if (nodes.Count >= MaximumNodes)
            throw new InvalidDataException("The B3D model has too many nodes.");
        string name = ReadNullString(reader, chunk.End);
        Vector3 position = ReadVector3(reader);
        Vector3 scale = ReadVector3(reader);
        Quaternion rotation = ReadQuaternion(reader);
        Matrix4 local = LocalMatrix(position, scale, rotation);
        Matrix4 global = parent >= 0 ? local * nodes[parent].BindGlobal : local;
        Matrix4.Invert(global, out Matrix4 inverse);
        int nodeIndex = nodes.Count;
        nodes.Add(new Node(name, parent, position, scale, rotation, global, inverse));

        while (reader.BaseStream.Position < chunk.End)
        {
            Chunk child = ReadChunk(reader, chunk.End);
            switch (child.Name)
            {
                case "NODE": ReadNode(reader, child, nodeIndex); break;
                case "MESH": ReadMesh(reader, child, nodeIndex); break;
                case "BONE": ReadBone(reader, child, nodeIndex); break;
                case "KEYS": ReadKeys(reader, child, nodeIndex); break;
                case "ANIM": ReadAnimation(reader, child); break;
                default: reader.BaseStream.Position = child.End; break;
            }
        }
    }

    private void ReadMesh(BinaryReader reader, Chunk chunk, int nodeIndex)
    {
        int defaultBrush = reader.ReadInt32();
        lastMeshStart = vertices.Count;
        while (reader.BaseStream.Position < chunk.End)
        {
            Chunk child = ReadChunk(reader, chunk.End);
            if (child.Name == "VRTS") ReadVertices(reader, child, nodeIndex);
            else if (child.Name == "TRIS") ReadTriangles(reader, child, lastMeshStart, defaultBrush);
            else reader.BaseStream.Position = child.End;
        }
    }

    private void ReadVertices(BinaryReader reader, Chunk chunk, int nodeIndex)
    {
        int flags = reader.ReadInt32();
        int sets = reader.ReadInt32();
        int setSize = reader.ReadInt32();
        if (sets is < 0 or > 8 || setSize is < 0 or > 4)
            throw new InvalidDataException("The B3D vertex texture-coordinate layout is invalid.");
        int floats = 3 + ((flags & 1) != 0 ? 3 : 0) + ((flags & 2) != 0 ? 4 : 0) + (sets * setSize);
        long bytes = chunk.End - reader.BaseStream.Position;
        if (bytes < 0 || bytes % (floats * sizeof(float)) != 0)
            throw new InvalidDataException("The B3D vertex chunk has an invalid length.");
        int count = checked((int)(bytes / (floats * sizeof(float))));
        if (vertices.Count + count > MaximumVertices)
            throw new InvalidDataException("The B3D model has too many vertices.");

        Matrix4 bind = nodes[nodeIndex].BindGlobal;
        for (int i = 0; i < count; i++)
        {
            Vector3 position = Vector3.TransformPosition(ReadVector3(reader), bind);
            if ((flags & 1) != 0) _ = ReadVector3(reader);
            if ((flags & 2) != 0)
                for (int component = 0; component < 4; component++) _ = reader.ReadSingle();
            Vector2 uv = Vector2.Zero;
            for (int set = 0; set < sets; set++)
            for (int component = 0; component < setSize; component++)
            {
                float value = reader.ReadSingle();
                if (set == 0 && component == 0) uv.X = value;
                if (set == 0 && component == 1) uv.Y = value;
            }
            vertices.Add(new Vertex(position, uv));
        }
    }

    private void ReadTriangles(BinaryReader reader, Chunk chunk, int start, int defaultBrush)
    {
        int triangleBrush = reader.ReadInt32();
        int materialIndex = triangleBrush >= 0 ? triangleBrush : defaultBrush;
        long bytes = chunk.End - reader.BaseStream.Position;
        if (bytes < 0 || bytes % (3 * sizeof(int)) != 0)
            throw new InvalidDataException("The B3D triangle chunk has an invalid length.");
        int firstIndex = indices.Count;
        Vector2 minimumUv = new(float.PositiveInfinity);
        Vector2 maximumUv = new(float.NegativeInfinity);
        while (reader.BaseStream.Position < chunk.End)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                int index = checked(start + reader.ReadInt32());
                if ((uint)index >= (uint)vertices.Count)
                    throw new InvalidDataException("A B3D triangle references an invalid vertex.");
                indices.Add(index);
                minimumUv = Vector2.ComponentMin(minimumUv, vertices[index].Uv);
                maximumUv = Vector2.ComponentMax(maximumUv, vertices[index].Uv);
            }
        }
        submeshes.Add(new B3dSubmeshInfo(
            firstIndex,
            indices.Count - firstIndex,
            materialIndex,
            minimumUv,
            maximumUv));
    }

    private void ReadTextures(BinaryReader reader, Chunk chunk)
    {
        while (reader.BaseStream.Position < chunk.End)
        {
            string name = ReadNullString(reader, chunk.End).Replace('\\', '/');
            EnsureAvailable(reader, chunk.End, 28, "B3D texture");
            int flags = reader.ReadInt32();
            int blend = reader.ReadInt32();
            Vector2 position = new(reader.ReadSingle(), reader.ReadSingle());
            Vector2 scale = new(reader.ReadSingle(), reader.ReadSingle());
            float angle = reader.ReadSingle();
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)
                || !float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || !float.IsFinite(angle))
                throw new InvalidDataException("A B3D texture transform is not finite.");
            textures.Add(new B3dTextureInfo(name, flags, blend, position, scale, angle));
        }
    }

    private void ReadMaterials(BinaryReader reader, Chunk chunk)
    {
        EnsureAvailable(reader, chunk.End, sizeof(int), "B3D material header");
        int textureSlots = reader.ReadInt32();
        if (textureSlots is < 0 or > 64)
            throw new InvalidDataException("The B3D material texture-slot count is invalid.");

        while (reader.BaseStream.Position < chunk.End)
        {
            string name = ReadNullString(reader, chunk.End);
            long bytes = checked(28L + ((long)textureSlots * sizeof(int)));
            EnsureAvailable(reader, chunk.End, bytes, "B3D material");
            Vector4 color = new(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            float shininess = reader.ReadSingle();
            int blend = reader.ReadInt32();
            int effects = reader.ReadInt32();
            if (!float.IsFinite(color.X) || !float.IsFinite(color.Y)
                || !float.IsFinite(color.Z) || !float.IsFinite(color.W) || !float.IsFinite(shininess))
                throw new InvalidDataException("A B3D material value is not finite.");
            var textureIndices = new int[textureSlots];
            for (int slot = 0; slot < textureSlots; slot++)
                textureIndices[slot] = reader.ReadInt32();
            materials.Add(new B3dMaterialInfo(name, color, shininess, blend, effects, textureIndices));
        }
    }

    private void ValidateMaterials()
    {
        foreach (B3dMaterialInfo material in materials)
        foreach (int textureIndex in material.TextureIndices)
        {
            // Several official Mobs Redo meshes intentionally omit TEXS and receive their skin
            // from Lua's `textures` property. Irrlicht accepts the dangling brush slot; Tesseris
            // likewise supplies the manifest texture layer at draw time.
            if (textures.Count == 0 && textureIndex >= 0)
                continue;
            if (textureIndex < -1 || textureIndex >= textures.Count)
                throw new InvalidDataException(
                    $"B3D material '{material.Name}' references texture {textureIndex}, " +
                    $"but the file defines {textures.Count} textures.");
        }

        foreach (B3dSubmeshInfo submesh in submeshes)
        {
            if (submesh.MaterialIndex < -1 || submesh.MaterialIndex >= materials.Count)
                throw new InvalidDataException("A B3D submesh references an invalid material.");
        }
    }

    private static void EnsureAvailable(BinaryReader reader, long end, long bytes, string description)
    {
        if (bytes < 0 || end - reader.BaseStream.Position < bytes)
            throw new InvalidDataException($"A {description} is truncated.");
    }

    private void ReadBone(BinaryReader reader, Chunk chunk, int nodeIndex)
    {
        long bytes = chunk.End - reader.BaseStream.Position;
        if (bytes < 0 || bytes % 8 != 0)
            throw new InvalidDataException("The B3D bone chunk has an invalid length.");
        while (reader.BaseStream.Position < chunk.End)
        {
            int vertexIndex = checked(lastMeshStart + reader.ReadInt32());
            float strength = reader.ReadSingle();
            if ((uint)vertexIndex >= (uint)vertices.Count || !float.IsFinite(strength))
                throw new InvalidDataException("A B3D bone weight is invalid.");
            if (strength > 0f)
                vertices[vertexIndex].Weights.Add(new Weight(nodeIndex, strength));
        }
    }

    private void ReadKeys(BinaryReader reader, Chunk chunk, int nodeIndex)
    {
        int flags = reader.ReadInt32();
        int floatCount = ((flags & 1) != 0 ? 3 : 0)
            + ((flags & 2) != 0 ? 3 : 0)
            + ((flags & 4) != 0 ? 4 : 0);
        int stride = sizeof(int) + (floatCount * sizeof(float));
        long bytes = chunk.End - reader.BaseStream.Position;
        if (bytes < 0 || stride <= 4 || bytes % stride != 0)
            throw new InvalidDataException("The B3D keyframe chunk has an invalid length.");
        List<KeyFrame> keys = nodes[nodeIndex].Keys;
        while (reader.BaseStream.Position < chunk.End)
        {
            // SNÍMKY SE POSOUVAJÍ O JEDNA. V souboru jsou od jedničky, Irrlicht je ukládá od nuly
            // (irr/src/CB3DMeshFileLoader.cpp:591-603, `frame - 1`, se stejným ořezem snímků < 1).
            // Rozsahy klipů v našem manifestu jsou opsané z Lua modů, tedy z Luantiho posunutého
            // prostoru — bez tohohle posunu bychom vzorkovali celý klip o snímek vedle.
            float frame = Math.Max(1, reader.ReadInt32()) - 1;
            Vector3? position = (flags & 1) != 0 ? ReadVector3(reader) : null;
            Vector3? scale = (flags & 2) != 0 ? ReadVector3(reader) : null;
            Quaternion? rotation = (flags & 4) != 0 ? ReadQuaternion(reader) : null;
            keys.Add(new KeyFrame(frame, position, scale, rotation));
        }
        keys.Sort((left, right) => left.Frame.CompareTo(right.Frame));
    }

    private void ReadAnimation(BinaryReader reader, Chunk chunk)
    {
        if (chunk.End - reader.BaseStream.Position != 12)
            throw new InvalidDataException("The B3D animation chunk has an invalid length.");
        _ = reader.ReadInt32();
        AnimationFrames = Math.Max(AnimationFrames, reader.ReadInt32());
        AnimationFps = reader.ReadSingle();
    }

    /// <summary>
    /// Lokální transformace uzlu, bit po bitu jako Irrlichtův <c>Transform::buildMatrix</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Rotace je konjugovaná, a není to překlep.</b> Irrlicht staví matici kosti přes
    /// <c>rotation.getMatrix_transposed()</c> (irr/include/Transform.h:30), což je matice
    /// <b>konjugovaného</b> kvaternionu, ne toho uloženého. Běžné knihovny — OpenTK i
    /// System.Numerics — dávají standardní rotaci. Kdo to nechá být, otáčí každou kost na
    /// opačnou stranu.</para>
    ///
    /// <para>V klidové póze to není vidět: klíč na prvním snímku se rovná klidové transformaci
    /// z NODE, takže skinovací matice vyjde identita a chyba se vyruší. Projeví se teprve
    /// v pohybu — a násobí se hloubkou hierarchie, protože (A·B)ᵀ ≠ Aᵀ·Bᵀ. Proto byly rozhozené
    /// hlavně končetiny a ocas (Mid Spine → Rear Spine → Upper Tail → Tail), zatímco trup
    /// vypadal v pořádku.</para>
    ///
    /// <para>Naměřeno na skutečných modelech přes celý chodicí klip: divočákovi se v původní
    /// konvenci rozestoupily dvě části o 4,48 jednotky, po opravě o 0,34. Kráva se hýbala
    /// o 8,0 jednotky místo 3,1.</para>
    ///
    /// <para>Pořadí zůstává S·R·T v řádkové konvenci OpenTK, což je totéž co Irrlichtovo
    /// sloupcové R·(S·v)+T — translace se rotací ani měřítkem neotáčí.</para>
    /// </remarks>
    private static Matrix4 LocalMatrix(Vector3 position, Vector3 scale, Quaternion rotation) =>
        Matrix4.CreateScale(scale)
        * Matrix4.CreateFromQuaternion(new Quaternion(-rotation.X, -rotation.Y, -rotation.Z, rotation.W))
        * Matrix4.CreateTranslation(position);

    private static Vector3 ReadVector3(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static Quaternion ReadQuaternion(BinaryReader reader)
    {
        float w = reader.ReadSingle();
        return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), w).Normalized();
    }

    private static string ReadNullString(BinaryReader reader, long end)
    {
        var bytes = new List<byte>();
        while (reader.BaseStream.Position < end)
        {
            byte value = reader.ReadByte();
            if (value == 0) return Encoding.UTF8.GetString(bytes.ToArray());
            if (bytes.Count >= 4096) throw new InvalidDataException("A B3D string is too long.");
            bytes.Add(value);
        }
        throw new InvalidDataException("A B3D string is not null terminated.");
    }

    private static Chunk ReadChunk(BinaryReader reader, long parentEnd)
    {
        if (parentEnd - reader.BaseStream.Position < 8)
            throw new InvalidDataException("A B3D chunk header is truncated.");
        string name = Encoding.ASCII.GetString(reader.ReadBytes(4));
        int size = reader.ReadInt32();
        long end = checked(reader.BaseStream.Position + size);
        if (size < 0 || end > parentEnd)
            throw new InvalidDataException($"B3D chunk '{name}' exceeds its parent.");
        return new Chunk(name, end);
    }

    private sealed class Vertex(Vector3 position, Vector2 uv)
    {
        public Vector3 Position { get; } = position;
        public Vector2 Uv { get; } = uv;
        public List<Weight> Weights { get; } = [];
    }

    private sealed class Node(
        string name,
        int parent,
        Vector3 bindPosition,
        Vector3 bindScale,
        Quaternion bindRotation,
        Matrix4 bindGlobal,
        Matrix4 inverseBindGlobal)
    {
        public string Name { get; } = name;
        public int Parent { get; } = parent;
        public Vector3 BindPosition { get; } = bindPosition;
        public Vector3 BindScale { get; } = bindScale;
        public Quaternion BindRotation { get; } = bindRotation;
        public Matrix4 BindGlobal { get; } = bindGlobal;
        public Matrix4 InverseBindGlobal { get; } = inverseBindGlobal;
        public List<KeyFrame> Keys { get; } = [];
    }

    private sealed record KeyFrame(float Frame, Vector3? Position, Vector3? Scale, Quaternion? Rotation);
    private readonly record struct Weight(int Node, float Strength);
    private readonly record struct Chunk(string Name, long End);
}

/// <summary>Texture declaration embedded in a B3D TEXS chunk.</summary>
public sealed record B3dTextureInfo(
    string Name,
    int Flags,
    int Blend,
    Vector2 Position,
    Vector2 Scale,
    float Angle);

/// <summary>Material declaration embedded in a B3D BRUS chunk.</summary>
public sealed record B3dMaterialInfo(
    string Name,
    Vector4 Color,
    float Shininess,
    int Blend,
    int Effects,
    IReadOnlyList<int> TextureIndices);

/// <summary>
/// One B3D triangle group. Each TRIS chunk is kept separate because it can select a different
/// brush even when it shares the parent MESH vertex array with adjacent groups.
/// </summary>
public readonly record struct B3dSubmeshInfo(
    int FirstIndex,
    int IndexCount,
    int MaterialIndex,
    Vector2 MinimumUv,
    Vector2 MaximumUv);

public readonly record struct B3dAnimationClip(float Start, float End, float Speed);

public readonly record struct B3dAnimationProfile(
    B3dAnimationClip Idle,
    B3dAnimationClip Walk,
    B3dAnimationClip Run,
    B3dAnimationClip Graze)
{
    public static B3dAnimationProfile Animalia { get; } = new(
        new(1f, 59f, 10f),
        new(70f, 89f, 20f),
        new(100f, 119f, 30f),
        new(130f, 149f, 20f));

    /// <summary>
    /// Postava hráče. Rozsahy jsou doslova ty, které Luanti používá v `player_api`
    /// pro `character.b3d`: stání 0–79, chůze 168–187, kopání 189–198, chůze s kopáním
    /// 200–219. Model má 220 snímků, takže sedí přesně.
    /// </summary>
    public static B3dAnimationProfile Character { get; } = new(
        new(0f, 79f, 30f),
        new(168f, 187f, 30f),
        new(200f, 219f, 30f),
        new(189f, 198f, 30f));

    public static B3dAnimationProfile ClassicMobsSheep { get; } = new(
        new(0f, 80f, 15f),
        new(81f, 100f, 15f),
        new(81f, 100f, 15f),
        new(0f, 80f, 15f));
}
