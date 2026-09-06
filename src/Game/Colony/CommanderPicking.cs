using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;

namespace Tesseris.Game.Colony;

/// <summary>
/// Na co velitel ukazuje myší.
/// </summary>
/// <remarks>
/// <para><b>Paprsek se protíná se SVĚTEM, ne s vodorovnou rovinou řezu.</b> Dřív se mířilo
/// na průsečík s rovinou v pevném Y, takže na svahu skončil kurzor v každém sloupci jinde než
/// na povrchu: naměřeno, že z 32 sloupců jednoho řezu bylo 6 vzduchu, 2 povrchové a 24
/// zavalených, a ze 130 označených buněk se povedlo vykopat 15, kdežto 115 zůstalo odložených.
/// Hráč přitom klikal na to, co viděl.</para>
///
/// <para><b>Proč první zásah paprsku a ne nejvyšší pevný blok ve sloupci.</b> Maximum Y
/// ve sloupci je past: pod převisem, v jeskyni a uvnitř hráčovy vlastní budovy by označilo
/// STŘECHU, ne to, na co se hráč dívá. Průchod mřížkou tenhle případ řeší sám, protože
/// respektuje, odkud se kamera dívá.</para>
///
/// <para><b>Rovina řezu se nezahazuje, degraduje se na STROP.</b> Bloky nad zvoleným patrem
/// jsou pro paprsek průhledné, takže se velitel může „prokousat" pohledem do podzemí prostě
/// tím, že sjede řezem dolů — to bude potřeba, až přijde důl. Zároveň se tím vyřeší případ,
/// kdy je zásah nad řezem: takový zásah vůbec nevznikne.</para>
///
/// <para><b>Když paprsek netrefí nic</b> (kurzor míří do oblohy nebo za okraj načteného
/// světa), spadne se na starý průsečík s rovinou řezu. Tažení výběru tak funguje i přes
/// prázdno — jinak by tah přes okraj kopce zamrzl na posledním trefeném bodu.</para>
/// </remarks>
public static class CommanderPicking
{
    /// <summary>Jak daleko velitelský paprsek dohlédne, ve světových jednotkách.</summary>
    /// <remarks>
    /// Kamera se vznáší <see cref="CommanderView.CameraHeight"/> nad řezem a míří šikmo, takže
    /// k okraji záběru je to řádově stovka jednotek. 512 je s rezervou a průchod mřížkou má
    /// vlastní strop na počet kroků.
    /// </remarks>
    public const float MaximumDistance = 512f;

    /// <summary>
    /// Na kterou buňku velitel ukazuje.
    /// </summary>
    /// <param name="origin">Poloha kamery.</param>
    /// <param name="direction">Směr paprsku pod kurzorem; nemusí být jednotkový.</param>
    /// <param name="sliceY">Zvolené patro. Slouží jako strop pro paprsek a jako záloha.</param>
    /// <param name="cell">Zasažená buňka.</param>
    /// <param name="hitSolid">true, když se opravdu trefil pevný blok.</param>
    /// <returns>false, když se nedá určit vůbec nic.</returns>
    public static bool TryPick(
        VoxelWorld world,
        BlockRegistry blocks,
        Vector3 origin,
        Vector3 direction,
        int sliceY,
        out Vector3i cell,
        out bool hitSolid)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(blocks);

        cell = default;
        hitSolid = false;

        // Předávat rozsah přes lokální funkci, ne přes zachycující lambdu ve tick cestě:
        // označuje se na kliknutí myši, ne každý tik, a průchod mřížkou delegát vyžaduje.
        bool IsSolidUpToSlice(int x, int y, int z)
        {
            if (y > sliceY)
            {
                return false;
            }

            ushort block = world.GetBlock(x, y, z);
            return block != BlockRegistry.Air && blocks.IsSolid(block);
        }

        if (VoxelRaycast.Cast(origin, direction, MaximumDistance, IsSolidUpToSlice, out VoxelHit hit))
        {
            cell = hit.Block;
            hitSolid = true;
            return true;
        }

        return TryPickOnSlicePlane(origin, direction, sliceY, out cell);
    }

    /// <summary>
    /// Záloha: průsečík s vodorovnou rovinou řezu. Tohle bývalo jediné chování.
    /// </summary>
    /// <remarks>
    /// Používá se jen tehdy, když paprsek netrefil žádný blok. Pro označování to je špatný
    /// odhad (proto se nahradilo), ale pro udržení rozdělaného tahu je to lepší než nic.
    /// </remarks>
    public static bool TryPickOnSlicePlane(Vector3 origin, Vector3 direction, int sliceY, out Vector3i cell)
    {
        cell = default;

        float lengthSquared = direction.LengthSquared;
        if (lengthSquared < 1e-12f || !float.IsFinite(lengthSquared))
        {
            return false;
        }

        Vector3 dir = direction / MathF.Sqrt(lengthSquared);

        // Skoro vodorovný paprsek by rovinu trefil až v nekonečnu.
        if (MathF.Abs(dir.Y) < 0.05f)
        {
            return false;
        }

        // Horní stěna patra: buňka sliceY sahá od sliceY do sliceY+1.
        float planeY = sliceY + 1f;
        float distance = (planeY - origin.Y) / dir.Y;
        if (distance <= 0f || distance > MaximumDistance)
        {
            return false;
        }

        Vector3 point = origin + (dir * distance);
        cell = new Vector3i((int)MathF.Floor(point.X), sliceY, (int)MathF.Floor(point.Z));
        return true;
    }
}
