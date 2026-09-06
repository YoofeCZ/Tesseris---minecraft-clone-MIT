using Tesseris.Engine.MathLib;

namespace Tesseris.Game.Micro;

/// <summary>
/// Hotová geometrie a kolizní tělesa jednoho otesaného tvaru.
///
/// Souřadnice jsou v rozsahu 0 až 1, tedy vztažené k rohu bloku. Při vkládání do chunku se
/// jen přičte poloha bloku, takže tentýž tvar obslouží libovolné množství bloků.
///
/// Objekt je neměnný a sdílený mezi vlákny — vzniká jednou v <see cref="MicroShapeCache"/>
/// a od té chvíle se jen čte.
/// </summary>
public sealed class MicroShape
{
    public MicroShape(float[] vertices, uint[] indices, Aabb[] colliders, long contentHash)
    {
        Vertices = vertices;
        Indices = indices;
        Colliders = colliders;
        ContentHash = contentHash;
    }

    /// <summary>Vrcholy ve formátu shodném s <c>MeshBuffer</c>: pozice, UV, vrstva textury, stínění.</summary>
    public float[] Vertices { get; }

    public uint[] Indices { get; }

    /// <summary>Sloučené kvádry pro kolize, ve stejném měřítku jako geometrie.</summary>
    public Aabb[] Colliders { get; }

    /// <summary>Hash obsahu, ze kterého tvar vznikl.</summary>
    public long ContentHash { get; }

    public int TriangleCount => Indices.Length / 3;

    public bool IsEmpty => Indices.Length == 0;
}
