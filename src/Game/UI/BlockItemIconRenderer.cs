using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;

namespace Tesseris.Game.UI;

/// <summary>KreslĂ­ skuteÄŤnĂ˝ tvar voxelovĂ©ho bloku do 2D slotu.</summary>
internal static class BlockItemIconRenderer
{
    private const float Half = 0.5f;

    public static void Draw(
        SpriteRenderer sprites,
        float x,
        float y,
        float size,
        in BlockItemIcon icon,
        float brightness)
    {
        byte mask = icon.Pieces;

        // BoÄŤnĂ­ plochy prvnĂ­, hornĂ­ nakonec. Ikona nepotĹ™ebuje depth buffer a schody
        // pĹ™esto zachovajĂ­ ÄŤitelnou hornĂ­ hranu obou stupĹĹŻ.
        for (int j = 0; j < PieceMask.Steps; j++)
        {
            for (int k = 0; k < PieceMask.Steps; k++)
            {
                for (int i = 0; i < PieceMask.Steps; i++)
                {
                    if (!PieceMask.Has(mask, i, j, k)) continue;

                    float minX = i * Half;
                    float minY = j * Half;
                    float minZ = k * Half;
                    float maxX = minX + Half;
                    float maxY = minY + Half;
                    float maxZ = minZ + Half;

                    if (i == PieceMask.Steps - 1 || !PieceMask.Has(mask, i + 1, j, k))
                    {
                        Quad(
                            sprites, x, y, size,
                            new Vector3(maxX, minY, minZ), new Vector3(maxX, minY, maxZ),
                            new Vector3(maxX, maxY, maxZ), new Vector3(maxX, maxY, minZ),
                            new Vector2(minZ, 1f - minY), new Vector2(maxZ, 1f - minY),
                            new Vector2(maxZ, 1f - maxY), new Vector2(minZ, 1f - maxY),
                            icon.XLayer, brightness * 0.72f);
                    }

                    if (k == PieceMask.Steps - 1 || !PieceMask.Has(mask, i, j, k + 1))
                    {
                        Quad(
                            sprites, x, y, size,
                            new Vector3(minX, minY, maxZ), new Vector3(maxX, minY, maxZ),
                            new Vector3(maxX, maxY, maxZ), new Vector3(minX, maxY, maxZ),
                            new Vector2(minX, 1f - minY), new Vector2(maxX, 1f - minY),
                            new Vector2(maxX, 1f - maxY), new Vector2(minX, 1f - maxY),
                            icon.ZLayer, brightness * 0.86f);
                    }
                }
            }
        }

        for (int j = 0; j < PieceMask.Steps; j++)
        {
            for (int k = 0; k < PieceMask.Steps; k++)
            {
                for (int i = 0; i < PieceMask.Steps; i++)
                {
                    if (!PieceMask.Has(mask, i, j, k)
                        || (j < PieceMask.Steps - 1 && PieceMask.Has(mask, i, j + 1, k)))
                    {
                        continue;
                    }

                    float minX = i * Half;
                    float maxX = minX + Half;
                    float maxY = (j + 1) * Half;
                    float minZ = k * Half;
                    float maxZ = minZ + Half;

                    Quad(
                        sprites, x, y, size,
                        new Vector3(minX, maxY, minZ), new Vector3(maxX, maxY, minZ),
                        new Vector3(maxX, maxY, maxZ), new Vector3(minX, maxY, maxZ),
                        new Vector2(minX, 1f - minZ), new Vector2(maxX, 1f - minZ),
                        new Vector2(maxX, 1f - maxZ), new Vector2(minX, 1f - maxZ),
                        icon.TopLayer, brightness);
                }
            }
        }
    }

    private static void Quad(
        SpriteRenderer sprites,
        float x,
        float y,
        float size,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        Vector2 uvA,
        Vector2 uvB,
        Vector2 uvC,
        Vector2 uvD,
        int layer,
        float brightness) =>
        sprites.DrawIconQuad(
            Project(a, x, y, size), Project(b, x, y, size),
            Project(c, x, y, size), Project(d, x, y, size),
            uvA, uvB, uvC, uvD, layer, brightness);

    private static Vector2 Project(Vector3 point, float x, float y, float size) => new(
        x + (size * (0.5f + ((point.X - point.Z) * 0.40f))),
        y + (size * (0.52f + ((point.X + point.Z) * 0.18f) - (point.Y * 0.46f))));
}
