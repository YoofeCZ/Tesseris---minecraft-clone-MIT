using OpenTK.Mathematics;

namespace Tesseris.Game.World;

/// <summary>
/// Rostoucí pole vrcholů a indexů, do kterého sype mesher.
///
/// Vrchol je 7 floatů: pozice v souřadnicích chunku (0..32), dlaždicovací UV, index vrstvy
/// v texture array a stínění rohu. Buffer se mezi meshováními recykluje přes
/// <see cref="Clear"/>, aby se pole nealokovala pro každý chunk znovu.
/// </summary>
public sealed class MeshBuffer
{
    public const int FloatsPerVertex = 8;

    private float[] _vertices = new float[FloatsPerVertex * 1024];
    private uint[] _indices = new uint[1536];

    public int VertexCount { get; private set; }

    public int IndexCount { get; private set; }

    public bool IsEmpty => IndexCount == 0;

    public ReadOnlySpan<float> Vertices => _vertices.AsSpan(0, VertexCount * FloatsPerVertex);

    public ReadOnlySpan<uint> Indices => _indices.AsSpan(0, IndexCount);

    /// <summary>
    /// Vnitřní pole vrcholů. Bývá delší než využitá část — platných je prvních
    /// <see cref="VertexCount"/> × <see cref="FloatsPerVertex"/> položek.
    ///
    /// Vystaveno kvůli nahrávání do GL: přetížení <c>NamedBufferSubData</c> berou pole
    /// a kopie ze spanu by u každého chunku znamenala alokaci navíc.
    /// </summary>
    public float[] RawVertices => _vertices;

    /// <summary>Vnitřní pole indexů. Platných je prvních <see cref="IndexCount"/> položek.</summary>
    public uint[] RawIndices => _indices;

    public void Clear()
    {
        VertexCount = 0;
        IndexCount = 0;
    }

    /// <summary>Nastavi blokove svetlo vsem uz zapsanym vrcholum teto davky.</summary>
    public void SetBlockLight(float light)
    {
        light = Math.Clamp(light, 0f, 1f);
        for (int vertex = 0; vertex < VertexCount; vertex++)
        {
            _vertices[(vertex * FloatsPerVertex) + 7] = light;
        }
    }

    /// <summary>Vynasobi zakladni osvetleni vsech jiz zapsanych vrcholu.</summary>
    public void MultiplyShade(float light)
    {
        light = Math.Clamp(light, 0f, 1f);
        for (int vertex = 0; vertex < VertexCount; vertex++)
        {
            int shade = (vertex * FloatsPerVertex) + 6;
            _vertices[shade] *= light;
        }
    }

    /// <summary>
    /// Přidá obdélník jako dva trojúhelníky. Vrcholy musí přijít v pořadí proti směru
    /// hodinových ručiček při pohledu zvenčí, jinak je backface culling zahodí.
    /// </summary>
    /// <param name="flipDiagonal">
    /// Rozdělit obdélník po druhé úhlopříčce. Používá se, když je stínění rohů nesymetrické:
    /// úhlopříčka musí spojovat dva světlejší rohy, jinak přes obdélník vznikne tmavý pruh.
    /// </param>
    public void AddQuad(
        Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3,
        float layer,
        float ao0, float ao1, float ao2, float ao3,
        bool flipDiagonal)
    {
        AddQuadLit(
            p0, p1, p2, p3, uv0, uv1, uv2, uv3, layer,
            ao0, ao1, ao2, ao3, 0f, 0f, 0f, 0f, flipDiagonal);
    }

    public void AddQuadLit(
        Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3,
        float layer,
        float ao0, float ao1, float ao2, float ao3,
        float light0, float light1, float light2, float light3,
        bool flipDiagonal)
    {
        EnsureCapacity(4, 6);

        uint baseVertex = (uint)VertexCount;

        AddVertex(p0, uv0, layer, ao0, light0);
        AddVertex(p1, uv1, layer, ao1, light1);
        AddVertex(p2, uv2, layer, ao2, light2);
        AddVertex(p3, uv3, layer, ao3, light3);

        if (flipDiagonal)
        {
            AddTriangle(baseVertex + 1, baseVertex + 2, baseVertex + 3);
            AddTriangle(baseVertex + 1, baseVertex + 3, baseVertex + 0);
        }
        else
        {
            AddTriangle(baseVertex + 0, baseVertex + 1, baseVertex + 2);
            AddTriangle(baseVertex + 0, baseVertex + 2, baseVertex + 3);
        }
    }

    /// <summary>Přidá jednu samostatnou texturovanou trojúhelníkovou plochu.</summary>
    public void AddTriangle(
        Vector3 p0, Vector3 p1, Vector3 p2,
        Vector2 uv0, Vector2 uv1, Vector2 uv2,
        float layer, float shade)
    {
        EnsureCapacity(3, 3);
        uint first = (uint)VertexCount;
        AddVertex(p0, uv0, layer, shade, 0f);
        AddVertex(p1, uv1, layer, shade, 0f);
        AddVertex(p2, uv2, layer, shade, 0f);
        AddTriangle(first, first + 1, first + 2);
    }

    /// <summary>
    /// Připojí hotovou geometrii posunutou o zadaný vektor.
    ///
    /// Tudy se do chunku dostávají otesané bloky: tvar se spočítá jednou, uloží do sdílené
    /// zásoby a pak se jen kopíruje s posunem podle polohy bloku.
    /// </summary>
    public void AppendTranslated(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, Vector3 offset)
    {
        if (indices.Length == 0)
        {
            return;
        }

        int addedVertices = vertices.Length / FloatsPerVertex;
        EnsureCapacity(addedVertices, indices.Length);

        uint baseVertex = (uint)VertexCount;

        for (int i = 0; i < addedVertices; i++)
        {
            int source = i * FloatsPerVertex;
            int destination = (VertexCount + i) * FloatsPerVertex;

            _vertices[destination + 0] = vertices[source + 0] + offset.X;
            _vertices[destination + 1] = vertices[source + 1] + offset.Y;
            _vertices[destination + 2] = vertices[source + 2] + offset.Z;
            _vertices[destination + 3] = vertices[source + 3];
            _vertices[destination + 4] = vertices[source + 4];
            _vertices[destination + 5] = vertices[source + 5];
            _vertices[destination + 6] = vertices[source + 6];
            _vertices[destination + 7] = vertices[source + 7];
        }

        VertexCount += addedVertices;

        for (int i = 0; i < indices.Length; i++)
        {
            _indices[IndexCount + i] = baseVertex + indices[i];
        }

        IndexCount += indices.Length;
    }

    private void AddVertex(Vector3 position, Vector2 uv, float layer, float ao, float blockLight)
    {
        int offset = VertexCount * FloatsPerVertex;

        _vertices[offset + 0] = position.X;
        _vertices[offset + 1] = position.Y;
        _vertices[offset + 2] = position.Z;
        _vertices[offset + 3] = uv.X;
        _vertices[offset + 4] = uv.Y;
        _vertices[offset + 5] = layer;
        _vertices[offset + 6] = ao;
        _vertices[offset + 7] = blockLight;

        VertexCount++;
    }

    private void AddTriangle(uint a, uint b, uint c)
    {
        _indices[IndexCount + 0] = a;
        _indices[IndexCount + 1] = b;
        _indices[IndexCount + 2] = c;
        IndexCount += 3;
    }

    private void EnsureCapacity(int extraVertices, int extraIndices)
    {
        int neededFloats = (VertexCount + extraVertices) * FloatsPerVertex;
        if (neededFloats > _vertices.Length)
        {
            Array.Resize(ref _vertices, Math.Max(neededFloats, _vertices.Length * 2));
        }

        if (IndexCount + extraIndices > _indices.Length)
        {
            Array.Resize(ref _indices, Math.Max(IndexCount + extraIndices, _indices.Length * 2));
        }
    }
}
