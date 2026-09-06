using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;

namespace Tesseris.Game.Micro;

/// <summary>Výsledek dvouúrovňového průchodu paprsku.</summary>
/// <param name="Block">Souřadnice zasaženého bloku.</param>
/// <param name="Micro">
/// Souřadnice mikrovoxelu uvnitř bloku, 0 až 15. U neotesaného bloku je to (-1, -1, -1).
/// </param>
/// <param name="Normal">Normála zasažené stěny.</param>
/// <param name="Face">Která stěna byla zasažena.</param>
/// <param name="Distance">Vzdálenost od počátku paprsku.</param>
/// <param name="Position">Bod dopadu ve světových souřadnicích.</param>
/// <param name="Material">Identifikátor materiálu, do kterého se paprsek trefil.</param>
public readonly record struct MicroHit(
    Vector3i Block,
    Vector3i Micro,
    Vector3i Normal,
    BlockFace Face,
    float Distance,
    Vector3 Position,
    ushort Material)
{
    /// <summary>Trefil paprsek otesaný blok, tedy má smysl mikro souřadnice?</summary>
    public bool IsMicro => Micro.X >= 0;
}

/// <summary>
/// Paprsek, který umí najít i jednotlivé mikrovoxely uvnitř otesaných bloků.
///
/// Pracuje ve dvou úrovních. Vnější průchod jde po blocích a využívá obyčejný
/// <see cref="VoxelRaycast"/>. Jakmile narazí na blok, který je otesaný, spustí se druhý
/// průchod uvnitř jeho mřížky 16³. Když uvnitř nic není — což u otesaného bloku snadno
/// nastane, protože paprsek proletí dírou — vnější průchod prostě pokračuje dál.
///
/// Tohle rozhodování se dělá uvnitř podmínky, kterou vnějšímu průchodu předává volající.
/// Díky tomu nemusí existovat druhá kopie algoritmu DDA a vnější úroveň zůstává v MathLib
/// bez znalosti herních typů.
/// </summary>
public static class MicroRaycast
{
    /// <summary>Vystřelí paprsek a najde první pevné místo, ať už je to blok nebo mikrovoxel.</summary>
    /// <param name="hitLiquid">
    /// Má se paprsek zastavit o kapalinu?
    ///
    /// <para>Normálně ne. Voda se nedá vytěžit ani do ní stavět, takže by zaměřovač jen
    /// překážel: hráč mířil na dno pod hladinou a trefil hladinu, a pod vodou se zaměřoval
    /// dokonce blok, ve kterém měl hlavu. Paprsek proto kapalinu bere jako vzduch a projde
    /// skrz ni na to, co je za ní.</para>
    ///
    /// <para>Zapíná se to jen pro kbelík, protože ten na vodu naopak mířit musí.</para>
    /// </param>
    public static bool Cast(
        VoxelWorld world, Vector3 origin, Vector3 direction, float maxDistance, out MicroHit hit,
        bool hitLiquid = false)
    {
        ArgumentNullException.ThrowIfNull(world);

        hit = default;

        float lengthSquared = direction.LengthSquared;
        if (lengthSquared < 1e-12f)
        {
            return false;
        }

        Vector3 unit = direction / MathF.Sqrt(lengthSquared);

        MicroHit? insideHit = null;

        bool blockFound = VoxelRaycast.Cast(
            origin,
            unit,
            maxDistance,
            (x, y, z) =>
            {
                MicroBlock? micro = world.GetMicro(x, y, z);

                if (micro is null)
                {
                    ushort block = world.GetBlock(x, y, z);

                    if (world.Registry.IsAir(block))
                    {
                        return false;
                    }

                    // Kapalina se chová jako vzduch, dokud o ni volající vysloveně nestojí.
                    if (!hitLiquid && world.Registry.IsLiquid(block))
                    {
                        return false;
                    }

                    // ROSTLINA NENÍ KOSTKA, ale dvě zkřížené plochy. Bez téhle větve by
                    // hráč zničil kytku i tím, že zamířil vedle ní na něco za ní.
                    BlockShape shape = world.Registry.ShapeOf(block);

                    if (shape == BlockShape.Cross)
                    {
                        if (TryCastCross(world, origin, unit, x, y, z, maxDistance, block, out MicroHit cross))
                        {
                            insideHit = cross;
                            return true;
                        }

                        return false;
                    }

                    if (shape == BlockShape.GroundClutter)
                    {
                        if (TryCastGroundClutter(
                            world.Registry, origin, unit,
                            x, y, z, maxDistance, block, out MicroHit clutter))
                        {
                            insideHit = clutter;
                            return true;
                        }

                        return false;
                    }

                    if (shape == BlockShape.HytaleModel)
                    {
                        if (TryCastHytaleModel(
                            world.Registry, origin, unit,
                            x, y, z, maxDistance, block, out MicroHit modelHit))
                        {
                            insideHit = modelHit;
                            return true;
                        }

                        return false;
                    }

                    byte mask = world.GetPieces(x, y, z);

                    // NE KAŽDÝ BLOK JE PLNÁ KOSTKA. Strom se sází na dvakrát jemnější mřížce,
                    // takže jeho blok může být jen z několika dílků. Bez téhle větve by hráč
                    // zamířil na vzduch půl bloku vedle kmene a kmen by zmizel — paprsek by
                    // trefil obal, ne dřevo.
                    if (mask == PieceMask.Full && shape != BlockShape.Post)
                    {
                        return true;
                    }

                    if (TryCastShape(shape, mask, origin, unit, x, y, z, maxDistance, block, out MicroHit shapeHit))
                    {
                        insideHit = shapeHit;
                        return true;
                    }

                    // Druhá vrstva téhož bloku: listí kolem kmene. Bez ní by se skrz ni mířilo
                    // naskrz, i když je vidět.
                    ExtraPieces extra = world.GetExtra(x, y, z);

                    if (!extra.IsEmpty
                        && TryCastShape(
                            BlockShape.Cube, extra.Mask, origin, unit,
                            x, y, z, maxDistance, extra.Block, out MicroHit extraHit))
                    {
                        insideHit = extraHit;
                        return true;
                    }

                    // Paprsek proletěl mezerou kolem tvaru — pro vnější průchod je tu prázdno.
                    return false;
                }

                if (TryCastInside(micro, origin, unit, x, y, z, maxDistance, out MicroHit inner))
                {
                    insideHit = inner;
                    return true;
                }

                // Paprsek proletěl dírou — pro vnější průchod to znamená prázdno.
                return false;
            },
            out VoxelHit blockHit);

        if (!blockFound)
        {
            return false;
        }

        if (insideHit is { } micro)
        {
            hit = micro;
            return true;
        }

        ushort material = world.GetBlock(blockHit.Block.X, blockHit.Block.Y, blockHit.Block.Z);

        hit = new MicroHit(
            blockHit.Block,
            new Vector3i(-1, -1, -1),
            blockHit.Normal,
            FaceFromNormal(blockHit.Normal),
            blockHit.Distance,
            blockHit.Position,
            material);

        return true;
    }

    /// <summary>
    /// Průchod trsem rostliny: dvě svislé plochy po úhlopříčkách bloku.
    ///
    /// <para>Rozměry se berou z <see cref="PlantShape"/>, tedy z týchž čísel, podle kterých
    /// se trs kreslí. Textura má sice ještě díry, ale ty se neřeší — číst alfu při každém
    /// výstřelu paprsku by stálo víc, než kolik je ta přesnost hodná.</para>
    /// </summary>
    private static bool TryCastCross(
        VoxelWorld world, Vector3 origin, Vector3 unit,
        int blockX, int blockY, int blockZ, float maxDistance, ushort material, out MicroHit hit)
    {
        hit = default;

        bool submerged = world.ContainsWater(blockX, blockY, blockZ)
            || world.ContainsWater(blockX, blockY + 1, blockZ);

        (PlantShape.Plane first, PlantShape.Plane second) =
            PlantShape.Planes(blockX, blockY, blockZ, submerged);

        float best = float.MaxValue;
        Vector3i bestNormal = Vector3i.Zero;

        foreach (PlantShape.Plane plane in new[] { first, second })
        {
            if (TryHitPlane(origin, unit, plane, blockX, blockY, blockZ, out float distance, out Vector3i normal)
                && distance < best
                && distance <= maxDistance)
            {
                best = distance;
                bestNormal = normal;
            }
        }

        if (best == float.MaxValue)
        {
            return false;
        }

        hit = new MicroHit(
            new Vector3i(blockX, blockY, blockZ),
            new Vector3i(-1, -1, -1),
            bestNormal,
            FaceFromNormal(bestNormal),
            best,
            origin + (unit * best),
            material);

        return true;
    }

    private static bool TryCastGroundClutter(
        BlockRegistry registry, Vector3 origin, Vector3 unit,
        int blockX, int blockY, int blockZ, float maxDistance, ushort material, out MicroHit hit)
    {
        hit = default;
        GroundClutterModel? model = registry.GroundModelOf(material);
        if (model is null)
        {
            return false;
        }

        float angle = GroundClutterShape.AngleRadians(blockX, blockY, blockZ);
        var blockOrigin = new Vector3(blockX, blockY, blockZ);
        Vector3 localOrigin = blockOrigin
            + GroundClutterShape.InversePoint(origin - blockOrigin, angle);
        Vector3 localUnit = GroundClutterShape.InverseDirection(unit, angle);
        float best = float.MaxValue;
        Vector3i bestNormal = Vector3i.Zero;

        foreach (GroundClutterModel.Box box in model.Boxes)
        {
            Vector3 min = blockOrigin + box.Min;
            Vector3 max = blockOrigin + box.Max;

            if (TryHitBox(localOrigin, localUnit, min, max, out float distance, out Vector3i normal)
                && distance < best
                && distance <= maxDistance)
            {
                best = distance;
                bestNormal = DominantNormal(GroundClutterShape.TransformDirection(
                    new Vector3(normal.X, normal.Y, normal.Z), angle));
            }
        }

        if (best == float.MaxValue)
        {
            return false;
        }

        hit = new MicroHit(
            new Vector3i(blockX, blockY, blockZ),
            new Vector3i(-1, -1, -1),
            bestNormal,
            FaceFromNormal(bestNormal),
            best,
            origin + (unit * best),
            material);
        return true;

        static Vector3i DominantNormal(Vector3 normal)
        {
            var absolute = new Vector3(
                MathF.Abs(normal.X), MathF.Abs(normal.Y), MathF.Abs(normal.Z));

            if (absolute.Y >= absolute.X && absolute.Y >= absolute.Z)
            {
                return new Vector3i(0, normal.Y < 0f ? -1 : 1, 0);
            }

            return absolute.X >= absolute.Z
                ? new Vector3i(normal.X < 0f ? -1 : 1, 0, 0)
                : new Vector3i(0, 0, normal.Z < 0f ? -1 : 1);
        }
    }

    private static bool TryCastHytaleModel(
        BlockRegistry registry, Vector3 origin, Vector3 unit,
        int blockX, int blockY, int blockZ, float maxDistance, ushort material, out MicroHit hit)
    {
        hit = default;
        HytaleBlockModel? model = registry.HytaleModelOf(material);
        if (model is null)
        {
            return false;
        }

        Vector3 anchor = new(blockX + 0.5f, blockY, blockZ + 0.5f);
        float best = float.MaxValue;
        Vector3i bestNormal = Vector3i.Zero;

        foreach (HytaleBlockModel.Node node in model.Nodes)
        {
            if (node.Box is not { } box)
            {
                continue;
            }

            Quaternion inverse = Quaternion.Invert(box.Orientation);
            Vector3 localOrigin = Vector3.Transform(origin - (anchor + box.Centre), inverse);
            Vector3 localUnit = Vector3.Transform(unit, inverse);

            if (!TryHitBox(localOrigin, localUnit, -box.HalfSize, box.HalfSize,
                    out float distance, out Vector3i localNormal)
                || distance >= best || distance > maxDistance)
            {
                continue;
            }

            best = distance;
            Vector3 worldNormal = Vector3.Transform(
                new Vector3(localNormal.X, localNormal.Y, localNormal.Z), box.Orientation);
            bestNormal = DominantNormal(worldNormal);
        }

        if (best == float.MaxValue)
        {
            return false;
        }

        hit = new MicroHit(
            new Vector3i(blockX, blockY, blockZ), new Vector3i(-1, -1, -1),
            bestNormal, FaceFromNormal(bestNormal), best, origin + (unit * best), material);
        return true;

        static Vector3i DominantNormal(Vector3 normal)
        {
            Vector3 absolute = new(MathF.Abs(normal.X), MathF.Abs(normal.Y), MathF.Abs(normal.Z));
            if (absolute.Y >= absolute.X && absolute.Y >= absolute.Z)
                return new Vector3i(0, normal.Y < 0f ? -1 : 1, 0);
            return absolute.X >= absolute.Z
                ? new Vector3i(normal.X < 0f ? -1 : 1, 0, 0)
                : new Vector3i(0, 0, normal.Z < 0f ? -1 : 1);
        }
    }

    /// <summary>
    /// Průnik paprsku se svislou plochou. Řeší se v půdorysu jako paprsek proti úsečce
    /// a výsledná výška se pak ověří proti rozsahu plochy.
    /// </summary>
    private static bool TryHitPlane(
        Vector3 origin, Vector3 unit, PlantShape.Plane plane,
        int blockX, int blockY, int blockZ, out float distance, out Vector3i normal)
    {
        distance = 0f;
        normal = Vector3i.Zero;

        var from = new Vector2(blockX + plane.From.X, blockZ + plane.From.Y);
        var to = new Vector2(blockX + plane.To.X, blockZ + plane.To.Y);

        Vector2 edge = to - from;
        var ray = new Vector2(unit.X, unit.Z);

        float denominator = (ray.X * edge.Y) - (ray.Y * edge.X);

        // Paprsek rovnoběžný s plochou ji buď mine, nebo po ní klouže — obojí bereme jako minutí.
        if (MathF.Abs(denominator) < 1e-8f)
        {
            return false;
        }

        Vector2 delta = from - new Vector2(origin.X, origin.Z);

        float along = ((delta.X * edge.Y) - (delta.Y * edge.X)) / denominator;
        float across = ((delta.X * ray.Y) - (delta.Y * ray.X)) / denominator;

        if (along < 0f || across < 0f || across > 1f)
        {
            return false;
        }

        float height = origin.Y + (unit.Y * along);

        if (height < blockY + plane.Bottom || height > blockY + plane.Top)
        {
            return false;
        }

        distance = along;

        // Normála se bere jako opačný směr paprsku v hlavní vodorovné ose. Přesná normála
        // šikmé plochy by stejně nešla použít: na ni se pokládají bloky do mřížky.
        normal = MathF.Abs(unit.X) >= MathF.Abs(unit.Z)
            ? new Vector3i(unit.X > 0f ? -1 : 1, 0, 0)
            : new Vector3i(0, 0, unit.Z > 0f ? -1 : 1);

        return true;
    }

    /// <summary>
    /// Průchod tvarem, který nevyplňuje celý blok — sloupkem kmene nebo dílky listí.
    ///
    /// <para>Kvádry se berou z <see cref="PieceMask"/>, tedy z týchž dat, podle kterých se
    /// tvar kreslí a podle kterých se do něj naráží. Kdyby si paprsek počítal svoje, rozešlo
    /// by se to, na co hráč míří, s tím, co vidí.</para>
    /// </summary>
    private static bool TryCastShape(
        BlockShape shape, byte mask, Vector3 origin, Vector3 unit,
        int blockX, int blockY, int blockZ, float maxDistance, ushort material, out MicroHit hit)
    {
        hit = default;

        float best = float.MaxValue;
        Vector3i bestNormal = Vector3i.Zero;

        foreach (Aabb collider in PieceMask.Colliders(shape, mask))
        {
            var min = new Vector3(blockX + collider.Min.X, blockY + collider.Min.Y, blockZ + collider.Min.Z);
            var max = new Vector3(blockX + collider.Max.X, blockY + collider.Max.Y, blockZ + collider.Max.Z);

            if (TryHitBox(origin, unit, min, max, out float distance, out Vector3i normal)
                && distance < best
                && distance <= maxDistance)
            {
                best = distance;
                bestNormal = normal;
            }
        }

        if (best == float.MaxValue)
        {
            return false;
        }

        hit = new MicroHit(
            new Vector3i(blockX, blockY, blockZ),
            new Vector3i(-1, -1, -1),
            bestNormal,
            FaceFromNormal(bestNormal),
            best,
            origin + (unit * best),
            material);

        return true;
    }

    /// <summary>
    /// Průnik paprsku s kvádrem metodou plátů. Vrací vzdálenost ke vstupu a normálu stěny,
    /// kterou paprsek vstoupil.
    /// </summary>
    private static bool TryHitBox(
        Vector3 origin, Vector3 unit, Vector3 min, Vector3 max, out float distance, out Vector3i normal)
    {
        distance = 0f;
        normal = Vector3i.Zero;

        float near = 0f;
        float far = float.MaxValue;

        int axis = -1;
        int sign = 0;

        for (int i = 0; i < 3; i++)
        {
            float from = origin[i];
            float step = unit[i];
            float low = min[i];
            float high = max[i];

            // Paprsek vedený rovnoběžně s pláty buď leží mezi nimi celý, nebo je mine.
            if (MathF.Abs(step) < 1e-8f)
            {
                if (from < low || from > high)
                {
                    return false;
                }

                continue;
            }

            float inverse = 1f / step;
            float first = (low - from) * inverse;
            float second = (high - from) * inverse;
            int entrySign = -1;

            if (first > second)
            {
                (first, second) = (second, first);
                entrySign = 1;
            }

            if (first > near)
            {
                near = first;
                axis = i;
                sign = entrySign;
            }

            if (second < far)
            {
                far = second;
            }

            if (near > far)
            {
                return false;
            }
        }

        if (far < 0f)
        {
            return false;
        }

        distance = near;

        if (axis < 0)
        {
            // Paprsek začal uvnitř kvádru. Normála se pak vezme proti jeho hlavnímu směru,
            // aby nevyšla nulová a stěna šla určit.
            int dominant = MathF.Abs(unit.X) >= MathF.Abs(unit.Y) && MathF.Abs(unit.X) >= MathF.Abs(unit.Z) ? 0
                : MathF.Abs(unit.Y) >= MathF.Abs(unit.Z) ? 1
                : 2;

            axis = dominant;
            sign = unit[dominant] > 0f ? -1 : 1;
        }

        normal = axis switch
        {
            0 => new Vector3i(sign, 0, 0),
            1 => new Vector3i(0, sign, 0),
            _ => new Vector3i(0, 0, sign),
        };

        return true;
    }

    /// <summary>
    /// Druhý průchod uvnitř mřížky otesaného bloku.
    ///
    /// Paprsek se převede do souřadnic mřížky (blok je jednotková krychle rozdělená na
    /// šestnáct dílů) a projde stejným algoritmem jako vnější úroveň. Vstupní bod se
    /// dopočítá průnikem se stěnami bloku — vnější průchod ho nepředává.
    /// </summary>
    private static bool TryCastInside(
        MicroBlock micro,
        Vector3 origin,
        Vector3 unit,
        int blockX,
        int blockY,
        int blockZ,
        float maxDistance,
        out MicroHit hit)
    {
        hit = default;

        var blockMin = new Vector3(blockX, blockY, blockZ);
        if (!TryEntry(origin, unit, blockMin, out float entry, out Vector3i entryNormal))
        {
            return false;
        }

        if (entry > maxDistance)
        {
            return false;
        }

        // Posun dovnitř o zlomek mikrovoxelu, aby zaokrouhlení nevyhodilo start mimo mřížku.
        Vector3 localOrigin = ((origin + (unit * entry)) - blockMin) * MicroBlock.Size;
        localOrigin += unit * 1e-3f;

        int x = Math.Clamp((int)MathF.Floor(localOrigin.X), 0, MicroBlock.Size - 1);
        int y = Math.Clamp((int)MathF.Floor(localOrigin.Y), 0, MicroBlock.Size - 1);
        int z = Math.Clamp((int)MathF.Floor(localOrigin.Z), 0, MicroBlock.Size - 1);

        int stepX = Math.Sign(unit.X);
        int stepY = Math.Sign(unit.Y);
        int stepZ = Math.Sign(unit.Z);

        float deltaX = stepX != 0 ? MathF.Abs(1f / unit.X) : float.PositiveInfinity;
        float deltaY = stepY != 0 ? MathF.Abs(1f / unit.Y) : float.PositiveInfinity;
        float deltaZ = stepZ != 0 ? MathF.Abs(1f / unit.Z) : float.PositiveInfinity;

        float maxX = Boundary(localOrigin.X, unit.X, x, stepX);
        float maxY = Boundary(localOrigin.Y, unit.Y, y, stepY);
        float maxZ = Boundary(localOrigin.Z, unit.Z, z, stepZ);

        // Když paprsek trefí hned první mikrovoxel, žádný krok se neudělá a normála by
        // zůstala nulová. Použije se proto stěna, kterou paprsek do bloku vstoupil.
        Vector3i normal = entryNormal;
        float travelled = 0f;

        // Mřížka má šestnáct dílů na hranu, takže víc než padesát kroků nemá jak nastat.
        for (int step = 0; step < MicroBlock.Size * 3; step++)
        {
            if (micro.IsSolid(x, y, z))
            {
                // Vzdálenost uvnitř mřížky je v mikrovoxelech, venku v blocích.
                float distance = entry + (travelled / MicroBlock.Size);
                if (distance > maxDistance)
                {
                    return false;
                }

                hit = new MicroHit(
                    new Vector3i(blockX, blockY, blockZ),
                    new Vector3i(x, y, z),
                    normal,
                    FaceFromNormal(normal),
                    distance,
                    origin + (unit * distance),
                    micro.GetMaterial(x, y, z));

                return true;
            }

            if (maxX <= maxY && maxX <= maxZ)
            {
                travelled = maxX;
                maxX += deltaX;
                x += stepX;
                normal = new Vector3i(-stepX, 0, 0);
            }
            else if (maxY <= maxZ)
            {
                travelled = maxY;
                maxY += deltaY;
                y += stepY;
                normal = new Vector3i(0, -stepY, 0);
            }
            else
            {
                travelled = maxZ;
                maxZ += deltaZ;
                z += stepZ;
                normal = new Vector3i(0, 0, -stepZ);
            }

            if ((uint)x >= MicroBlock.Size || (uint)y >= MicroBlock.Size || (uint)z >= MicroBlock.Size)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Vzdálenost, ve které paprsek vstoupí do jednotkové krychle bloku, a stěna, kterou
    /// do ní vstoupil. Když už uvnitř je, vrací nulu a nulovou normálu.
    /// </summary>
    private static bool TryEntry(Vector3 origin, Vector3 unit, Vector3 blockMin, out float entry, out Vector3i normal)
    {
        entry = 0f;
        normal = Vector3i.Zero;
        float exit = float.PositiveInfinity;

        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? unit.X : axis == 1 ? unit.Y : unit.Z;
            float min = axis == 0 ? blockMin.X : axis == 1 ? blockMin.Y : blockMin.Z;
            float max = min + 1f;

            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < min || o > max)
                {
                    return false;
                }

                continue;
            }

            float t1 = (min - o) / d;
            float t2 = (max - o) / d;

            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            if (t1 > entry)
            {
                entry = t1;

                // Rozhoduje ta osa, na které se vstoupilo nejpozději — právě ta určuje stěnu.
                int step = d > 0f ? 1 : -1;
                normal = axis switch
                {
                    0 => new Vector3i(-step, 0, 0),
                    1 => new Vector3i(0, -step, 0),
                    _ => new Vector3i(0, 0, -step),
                };
            }

            exit = MathF.Min(exit, t2);

            if (entry > exit)
            {
                return false;
            }
        }

        return exit >= 0f;
    }

    private static float Boundary(float origin, float direction, int cell, int step)
    {
        if (step > 0)
        {
            return (cell + 1 - origin) / direction;
        }

        if (step < 0)
        {
            return (cell - origin) / direction;
        }

        return float.PositiveInfinity;
    }

    /// <summary>Převede normálu na označení stěny. Nulová normála znamená start uvnitř tělesa.</summary>
    public static BlockFace FaceFromNormal(Vector3i normal)
    {
        if (normal.X > 0) { return BlockFace.PosX; }
        if (normal.X < 0) { return BlockFace.NegX; }
        if (normal.Y > 0) { return BlockFace.PosY; }
        if (normal.Y < 0) { return BlockFace.NegY; }
        return normal.Z > 0 ? BlockFace.PosZ : BlockFace.NegZ;
    }
}
