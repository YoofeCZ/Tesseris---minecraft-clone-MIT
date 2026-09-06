using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;

namespace Tesseris.Game.Blocks;

public enum StairCornerShape
{
    Straight = 0,
    InnerLeft,
    InnerRight,
    OuterLeft,
    OuterRight,
}

public enum ChestPairSide
{
    Single = 0,
    Left = 1,
    Right = 2,
}

/// <summary>
/// Osm dílků uvnitř bloku, po jednom bitu.
///
/// <para><b>Proč vůbec.</b> Kmen má být poloviční šířky a listí se ho musí dotýkat. Na mřížce
/// celých bloků to nejde: sloupek stojí uprostřed svého bloku, takže mezi ním a listím
/// v sousedním bloku zůstane čtvrt bloku vzduchu — a přesně ta mezera byla vidět ve hře.
/// Strom se proto sází na <b>dvakrát jemnější mřížce</b> a blok si pamatuje, které jeho dílky
/// jsou vyplněné.</para>
///
/// <para><b>Jsou to skutečná data, ne kresba.</b> Předchozí pokus dílky odvozoval z hashe
/// souřadnice — vypadalo to skoro stejně, ale hráč pořád vykopával celý blok, protože žádné
/// dílky ve světě neexistovaly. Tady existují: dají se sázet, vykopat po jednom a uložit.</para>
///
/// <para>Zmenšovat na poloviční bloky celý svět se zkoušelo a stálo to polovinu snímkové
/// frekvence, aniž by se cokoli zlepšilo. Jemná mřížka proto platí jen tam, kde je k něčemu.</para>
/// </summary>
public static class PieceMask
{
    /// <summary>Kolik dílků připadá na hranu bloku.</summary>
    public const int Steps = 2;

    /// <summary>Hrana jednoho dílku ve světových jednotkách.</summary>
    public const float Size = 1f / Steps;

    /// <summary>Kolik dílků má blok celkem.</summary>
    public const int Count = Steps * Steps * Steps;

    /// <summary>Plný blok. Zároveň výchozí hodnota pro bloky, které dílky vůbec neřeší.</summary>
    public const byte Full = 0xFF;

    /// <summary>Prázdný blok. Takový ve světě nezůstane — odstraní se celý.</summary>
    public const byte Empty = 0;

    public const byte SlabBottom = 0x0F;
    public const byte SlabTop = 0xF0;
    public const byte PanelAlongX = 0x33;
    public const byte PanelAlongZ = 0x55;

    private const byte StairStateMin = 0x80;
    private const byte StairStateMax = 0xA7;

    // Dveře používají svůj bajt jako stav, ne jako osm skutečných půlbloků. Horní půlka
    // označuje formát, spodní čtyři bity nesou směr (0..3), pravý pant a otevření.
    // Staré světy s PanelAlongX/PanelAlongZ zůstávají čitelné přes pomocné metody níže.
    private const byte DoorStateMarker = 0xD0;
    private const byte DoorAnimatingMarker = 0xE0;
    private const byte DoorStateMask = 0xF0;
    private const byte DoorFacingMask = 0x03;
    private const byte DoorHingeRightBit = 0x04;
    private const byte DoorOpenBit = 0x08;

    private const byte TrapdoorAnimatingMarker = 0xB0;
    private const byte TrapdoorAnimatingMask = 0xF8;

    // Žebřík používá horní dva bity jako marker a spodní dva jako stranu stěny.
    private const byte LadderStateMarker = 0xC0;
    private const byte LadderStateMask = 0xFC;

    // Pochodeň: 0 = stojí na podlaze, 1..4 = visí na stěně ve směru normály.
    // Tvar má vlastní marker, proto se stav bezpečně ukládá ve stejném bajtu jako
    // orientace dveří, schodů a žebříku. Stará hodnota Full zůstá podlahová.
    private const byte TorchStateMarker = 0x60;
    private const byte TorchStateMask = 0xF8;
    private const byte TorchFacingMask = 0x07;

    // Bedna si nese směr, otevření, animaci a svou polovinu dvojbedny. Staré hodnoty
    // 0x70..0x73 se níže stále čtou jako jižně otočená jednoduchá bedna.
    private const byte ChestStateMarker = 0x40;
    private const byte ChestStateMarkerMask = 0xC0;
    private const byte ChestFacingMask = 0x03;
    private const byte ChestOpenBit = 0x04;
    private const byte ChestAnimatingBit = 0x08;
    private const byte ChestPairMask = 0x30;
    private const int ChestPairShift = 4;
    private const byte LegacyChestClosedState = 0x70;
    private const byte LegacyChestOpenState = 0x71;
    private const byte LegacyChestAnimatingClosedState = 0x72;
    private const byte LegacyChestAnimatingOpenState = 0x73;

    public const byte ChestClosedState = ChestStateMarker;
    public const byte ChestOpenState = ChestStateMarker | ChestOpenBit;
    public const byte ChestAnimatingClosedState = ChestStateMarker | ChestAnimatingBit;
    public const byte ChestAnimatingOpenState = ChestStateMarker | ChestOpenBit | ChestAnimatingBit;

    public const int DoorSouth = 0;
    public const int DoorEast = 1;
    public const int DoorNorth = 2;
    public const int DoorWest = 3;

    public static byte StairState(int facing, bool upsideDown, StairCornerShape corner)
    {
        if ((uint)facing > DoorWest) throw new ArgumentOutOfRangeException(nameof(facing));
        if ((uint)corner > (uint)StairCornerShape.OuterRight)
        {
            throw new ArgumentOutOfRangeException(nameof(corner));
        }

        int code = ((int)corner * 8) + (upsideDown ? 4 : 0) + facing;
        return (byte)(StairStateMin + code);
    }

    public static bool IsStairState(byte state) => state is >= StairStateMin and <= StairStateMax;

    public static int StairFacing(byte state)
    {
        if (IsStairState(state)) return (state - StairStateMin) & 0x03;
        return state switch
        {
            0xAF or 0xFA => DoorEast,
            0x5F or 0xF5 => DoorWest,
            0xCF or 0xFC => DoorSouth,
            _ => DoorNorth,
        };
    }

    public static bool StairUpsideDown(byte state) => IsStairState(state)
        ? (((state - StairStateMin) & 0x04) != 0)
        : state is 0xFA or 0xF5 or 0xFC or 0xF3;

    public static StairCornerShape StairCorner(byte state) => IsStairState(state)
        ? (StairCornerShape)((state - StairStateMin) / 8)
        : StairCornerShape.Straight;

    public static Vector3i StairForwardStep(int facing) => facing switch
    {
        DoorEast => Vector3i.UnitX,
        DoorWest => -Vector3i.UnitX,
        DoorNorth => -Vector3i.UnitZ,
        _ => Vector3i.UnitZ,
    };

    public static int StairLeftFacing(int facing) => facing switch
    {
        DoorEast => DoorNorth,
        DoorWest => DoorSouth,
        DoorNorth => DoorWest,
        _ => DoorEast,
    };

    public static byte StairOccupancy(byte state)
    {
        if (!IsStairState(state)) return state;

        int facing = StairFacing(state);
        int high = StairHalf(facing);
        int left = StairHalf(StairLeftFacing(facing));
        int right = StairHalf((StairLeftFacing(facing) + 2) & 0x03);
        int footprint = StairCorner(state) switch
        {
            StairCornerShape.InnerLeft => high | left,
            StairCornerShape.InnerRight => high | right,
            StairCornerShape.OuterLeft => high & left,
            StairCornerShape.OuterRight => high & right,
            _ => high,
        };

        return StairUpsideDown(state)
            ? (byte)(0xF0 | footprint)
            : (byte)(0x0F | (footprint << 4));
    }

    private static int StairHalf(int facing) => facing switch
    {
        DoorEast => 0x0A,
        DoorWest => 0x05,
        DoorSouth => 0x0C,
        _ => 0x03,
    };

    // Trapdoor si v jediném bajtu vedle polohy pamatuje i osu pantu. Bez toho se po
    // otevření vybírala orientace z aktuální kamery a kresba se zdánlivě otočila o 90°.
    public const byte TrapdoorBottomAlongX = SlabBottom;
    public const byte TrapdoorBottomAlongZ = 0x3C;
    public const byte TrapdoorTopAlongX = SlabTop;
    public const byte TrapdoorTopAlongZ = 0xC3;
    public const byte TrapdoorOpenBottomAlongX = PanelAlongX;
    public const byte TrapdoorOpenBottomAlongZ = PanelAlongZ;
    public const byte TrapdoorOpenTopAlongX = 0xCC;
    public const byte TrapdoorOpenTopAlongZ = 0xAA;

    /// <summary>Tloušťka dveří a trapdooru: jedna šestnáctina bloku.</summary>
    public const float PanelThickness = 1f / 16f;

    /// <summary>
    /// Bit dílku. Pořadí kopíruje <see cref="Chunk"/>: X se mění nejrychleji, pak Z, pak Y.
    /// </summary>
    public static int Bit(int i, int j, int k) => i | (k << 1) | (j << 2);

    /// <summary>Je dílek vyplněný?</summary>
    public static bool Has(byte mask, int i, int j, int k) => (mask & (1 << Bit(i, j, k))) != 0;

    /// <summary>Maska s vyplněným dílkem.</summary>
    public static byte With(byte mask, int i, int j, int k) => (byte)(mask | (1 << Bit(i, j, k)));

    /// <summary>Maska bez daného dílku.</summary>
    public static byte Without(byte mask, int i, int j, int k) => (byte)(mask & ~(1 << Bit(i, j, k)));

    /// <summary>Kvádr dílku v souřadnicích uvnitř bloku, tedy 0 až 1.</summary>
    public static Aabb Piece(int i, int j, int k)
    {
        var min = new Vector3(i * Size, j * Size, k * Size);
        return new Aabb(min, min + new Vector3(Size));
    }

    /// <summary>
    /// Polovina šířky sloupku. Čtvrtina bloku na každou stranu od středu — sloupek je tedy
    /// o poloviční šířce a stojí přesně uprostřed.
    /// </summary>
    public const float PostRadius = 0.25f;

    /// <summary>Kvádr sloupku v souřadnicích uvnitř bloku.</summary>
    public static Aabb Post { get; } = new(
        new Vector3(0.5f - PostRadius, 0f, 0.5f - PostRadius),
        new Vector3(0.5f + PostRadius, 1f, 0.5f + PostRadius));

    /// <summary>
    /// Listí kolem sloupku: čtyři kvádry, které vyplní blok všude tam, kde sloupek není.
    ///
    /// <para><b>Dílky se tu použít nedají.</b> Dílek je půlka bloku, tedy 0 až 0,5 nebo
    /// 0,5 až 1 — jenže sloupek zabírá 0,25 až 0,75, takže se s KAŽDÝM ze čtyř dílků
    /// v půdorysu protne. Ve hře se listí viditelně bořilo do kmene. Rám sloupek obchází
    /// přesně a nikde se s ním nepřekrývá.</para>
    /// </summary>
    public static IEnumerable<Aabb> PostFrame()
    {
        const float Low = 0.5f - PostRadius;
        const float High = 0.5f + PostRadius;

        // Dva pásy přes celou šířku bloku a dva doplňky mezi nimi — dohromady přesně to,
        // co sloupku zbývá, bez překryvů.
        yield return new Aabb(new Vector3(0f, 0f, 0f), new Vector3(Low, 1f, 1f));
        yield return new Aabb(new Vector3(High, 0f, 0f), new Vector3(1f, 1f, 1f));
        yield return new Aabb(new Vector3(Low, 0f, 0f), new Vector3(High, 1f, Low));
        yield return new Aabb(new Vector3(Low, 0f, High), new Vector3(High, 1f, 1f));
    }

    /// <summary>
    /// Kvádry, do kterých se dá na tomhle bloku narazit — s ohledem na jeho tvar.
    /// </summary>
    public static IEnumerable<Aabb> Colliders(BlockShape shape, byte mask)
    {
        if (shape == BlockShape.Stairs)
        {
            foreach (Aabb box in Colliders(StairOccupancy(mask))) yield return box;
            yield break;
        }

        if (shape == BlockShape.Door)
        {
            yield return DoorCollider(mask, height: 1f);
            yield break;
        }

        if (shape == BlockShape.Trapdoor)
        {
            if (TrapdoorIsOpen(mask))
            {
                yield return TrapdoorIsAlongZ(mask)
                    ? new Aabb(Vector3.Zero, new Vector3(PanelThickness, 1f, 1f))
                    : new Aabb(Vector3.Zero, new Vector3(1f, 1f, PanelThickness));
                yield break;
            }

            yield return TrapdoorIsTop(mask)
                ? new Aabb(new Vector3(0f, 1f - PanelThickness, 0f), Vector3.One)
                : new Aabb(Vector3.Zero, new Vector3(1f, PanelThickness, 1f));
            yield break;
        }

        if (shape == BlockShape.Ladder)
        {
            yield return LadderCollider(mask);
            yield break;
        }

        if (shape == BlockShape.Chest)
        {
            yield return new Aabb(
                new Vector3(1f / 16f, 0f, 1f / 16f),
                new Vector3(15f / 16f, 14f / 16f, 15f / 16f));
            yield break;
        }

        if (shape == BlockShape.Torch)
        {
            (Vector3 bottom, Vector3 top) = TorchAxis(mask);
            var radius = new Vector3(TorchRadius);
            yield return new Aabb(Vector3.ComponentMin(bottom, top) - radius,
                Vector3.ComponentMax(bottom, top) + radius);
            yield break;
        }

        if (shape == BlockShape.Post && mask == Full)
        {
            yield return Post;
            yield break;
        }

        foreach (Aabb box in Colliders(mask))
        {
            yield return box;
        }
    }

    public static byte LadderState(int facing)
    {
        if ((uint)facing > DoorWest) throw new ArgumentOutOfRangeException(nameof(facing));
        return (byte)(LadderStateMarker | facing);
    }

    public static int LadderFacing(byte state) => (state & LadderStateMask) == LadderStateMarker
        ? state & DoorFacingMask
        : DoorSouth;

    public static byte LadderStateFromNormal(Vector3i normal)
    {
        int facing = normal.X switch
        {
            > 0 => DoorEast,
            < 0 => DoorWest,
            _ => normal.Z < 0 ? DoorNorth : DoorSouth,
        };
        return LadderState(facing);
    }

    public static byte TorchFloorState => TorchStateMarker;

    public static byte TorchStateFromNormal(Vector3i normal)
    {
        if (normal.Y > 0) return TorchFloorState;
        int side = normal.X switch
        {
            > 0 => 1,
            < 0 => 2,
            _ => normal.Z > 0 ? 3 : 4,
        };
        return (byte)(TorchStateMarker | side);
    }

    public static Vector3i TorchSupportDirection(byte state)
    {
        int side = (state & TorchStateMask) == TorchStateMarker
            ? state & TorchFacingMask
            : 0;
        return side switch
        {
            1 => -Vector3i.UnitX,
            2 => Vector3i.UnitX,
            3 => -Vector3i.UnitZ,
            4 => Vector3i.UnitZ,
            _ => -Vector3i.UnitY,
        };
    }

    public static Vector3 TorchOutward(byte state)
    {
        Vector3i support = TorchSupportDirection(state);
        return new Vector3(-support.X, -support.Y, -support.Z);
    }

    public static bool TorchIsWallMounted(byte state) => TorchSupportDirection(state).Y == 0;

    public const float TorchRadius = 0.095f;

    /// <summary>Osa prostorove tycky pochodne v lokalnich souradnicich voxelu.</summary>
    public static (Vector3 Bottom, Vector3 Top) TorchAxis(byte state)
    {
        bool wall = TorchIsWallMounted(state);
        Vector3 outward = TorchOutward(state);

        // U nastenne varianty zacina osa temer na rovine podpory. Predchozich 0,11 bloku
        // pred stenou nechavalo viditelnou mezeru a pochoden pusobila, jako by levitovala.
        Vector3 bottom = wall
            ? new Vector3(0.5f, 0.42f, 0.5f) - (outward * 0.48f)
            : new Vector3(0.5f, 0.04f, 0.5f);
        Vector3 top = wall
            ? bottom + new Vector3(outward.X * 0.28f, 0.68f, outward.Z * 0.28f)
            : bottom + new Vector3(0f, 0.74f, 0f);
        return (bottom, top);
    }

    public static Aabb LadderCollider(byte state) => LadderFacing(state) switch
    {
        DoorEast => new Aabb(Vector3.Zero, new Vector3(PanelThickness, 1f, 1f)),
        DoorWest => new Aabb(
            new Vector3(1f - PanelThickness, 0f, 0f), Vector3.One),
        DoorNorth => new Aabb(
            new Vector3(0f, 0f, 1f - PanelThickness), Vector3.One),
        _ => new Aabb(Vector3.Zero, new Vector3(1f, 1f, PanelThickness)),
    };

    public static byte ChestState(int facing, ChestPairSide pair, bool open, bool animating = false)
    {
        if ((uint)facing > DoorWest) throw new ArgumentOutOfRangeException(nameof(facing));
        if (pair is < ChestPairSide.Single or > ChestPairSide.Right)
        {
            throw new ArgumentOutOfRangeException(nameof(pair));
        }

        return (byte)(ChestStateMarker
            | facing
            | ((int)pair << ChestPairShift)
            | (open ? ChestOpenBit : 0)
            | (animating ? ChestAnimatingBit : 0));
    }

    private static bool IsLegacyChestState(byte state) =>
        state is LegacyChestClosedState or LegacyChestOpenState
            or LegacyChestAnimatingClosedState or LegacyChestAnimatingOpenState;

    public static int ChestFacing(byte state) => IsLegacyChestState(state)
        ? DoorSouth
        : (state & ChestStateMarkerMask) == ChestStateMarker
            ? state & ChestFacingMask
            : DoorSouth;

    public static ChestPairSide ChestPair(byte state)
    {
        if (IsLegacyChestState(state) || (state & ChestStateMarkerMask) != ChestStateMarker)
        {
            return ChestPairSide.Single;
        }

        int pair = (state & ChestPairMask) >> ChestPairShift;
        return pair is 1 or 2 ? (ChestPairSide)pair : ChestPairSide.Single;
    }

    public static bool ChestIsAnimating(byte state) => IsLegacyChestState(state)
        ? state is LegacyChestAnimatingClosedState or LegacyChestAnimatingOpenState
        : (state & ChestStateMarkerMask) == ChestStateMarker && (state & ChestAnimatingBit) != 0;

    public static bool ChestIsOpen(byte state) => IsLegacyChestState(state)
        ? state is LegacyChestOpenState or LegacyChestAnimatingOpenState
        : (state & ChestStateMarkerMask) == ChestStateMarker && (state & ChestOpenBit) != 0;

    public static byte ChestFinalState(byte state) => ChestState(
        ChestFacing(state), ChestPair(state), ChestIsOpen(state), animating: false);

    public static byte ChestAnimatingState(byte state, bool open) => ChestState(
        ChestFacing(state), ChestPair(state), open, animating: true);

    public static byte ChestAnimatingState(bool open) => ChestState(
        DoorSouth, ChestPairSide.Single, open, animating: true);

    /// <summary>
    /// Souvislý panel dveří. Herní data dveří zabírají dva voxely kvůli ukládání a zásahu
    /// paprskem, ale jejich obrys nesmí kreslit spoj uprostřed jako dva samostatné bloky.
    /// </summary>
    public static Aabb DoorCollider(byte mask, float height = 1f)
    {
        if (height <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        int facing = DoorFacing(mask);
        bool hingeRight = DoorHingeRight(mask);

        if (!DoorIsOpen(mask))
        {
            return facing switch
            {
                DoorEast => new Aabb(Vector3.Zero, new Vector3(PanelThickness, height, 1f)),
                DoorWest => new Aabb(
                    new Vector3(1f - PanelThickness, 0f, 0f),
                    new Vector3(1f, height, 1f)),
                DoorNorth => new Aabb(
                    new Vector3(0f, 0f, 1f - PanelThickness),
                    new Vector3(1f, height, 1f)),
                _ => new Aabb(Vector3.Zero, new Vector3(1f, height, PanelThickness)),
            };
        }

        return facing switch
        {
            DoorEast => hingeRight
                ? new Aabb(
                    new Vector3(0f, 0f, 1f - PanelThickness),
                    new Vector3(1f, height, 1f))
                : new Aabb(Vector3.Zero, new Vector3(1f, height, PanelThickness)),
            DoorWest => hingeRight
                ? new Aabb(Vector3.Zero, new Vector3(1f, height, PanelThickness))
                : new Aabb(
                    new Vector3(0f, 0f, 1f - PanelThickness),
                    new Vector3(1f, height, 1f)),
            DoorNorth => hingeRight
                ? new Aabb(
                    new Vector3(1f - PanelThickness, 0f, 0f),
                    new Vector3(1f, height, 1f))
                : new Aabb(Vector3.Zero, new Vector3(PanelThickness, height, 1f)),
            _ => hingeRight
                ? new Aabb(Vector3.Zero, new Vector3(PanelThickness, height, 1f))
                : new Aabb(
                    new Vector3(1f - PanelThickness, 0f, 0f),
                    new Vector3(1f, height, 1f)),
        };
    }

    public static byte DoorState(int facing, bool hingeRight, bool open) => (byte)(
        DoorStateMarker
        | (facing & DoorFacingMask)
        | (hingeRight ? DoorHingeRightBit : 0)
        | (open ? DoorOpenBit : 0));

    public static int DoorFacing(byte mask)
    {
        if (IsDoorEncoded(mask))
        {
            return mask & DoorFacingMask;
        }

        // Kompatibilita se dveřmi uloženými před přidáním směru a pantu.
        return mask == PanelAlongZ ? DoorEast : DoorSouth;
    }

    public static bool DoorHingeRight(byte mask) =>
        IsDoorEncoded(mask) && (mask & DoorHingeRightBit) != 0;

    public static bool DoorIsOpen(byte mask) =>
        IsDoorEncoded(mask) && (mask & DoorOpenBit) != 0;

    public static bool DoorIsAnimating(byte mask) =>
        (mask & DoorStateMask) == DoorAnimatingMarker;

    public static byte DoorAnimatingState(byte finalState) => (byte)(
        DoorAnimatingMarker | (DoorFinalState(finalState) & 0x0F));

    public static byte DoorFinalState(byte mask) => DoorState(
        DoorFacing(mask), DoorHingeRight(mask), DoorIsOpen(mask));

    private static bool IsDoorEncoded(byte mask)
    {
        byte marker = (byte)(mask & DoorStateMask);
        return marker is DoorStateMarker or DoorAnimatingMarker;
    }

    /// <summary>
    /// Má se vodorovná osa kresby obrátit, aby pant namalovaný vlevo v textuře zůstal
    /// na skutečné straně pantu po otočení i otevření dveří.
    /// </summary>
    public static bool DoorUvReversed(byte mask)
    {
        int facing = DoorFacing(mask);
        if (DoorIsOpen(mask))
        {
            return facing is DoorNorth or DoorWest;
        }

        return facing switch
        {
            DoorSouth => !DoorHingeRight(mask),
            DoorEast => DoorHingeRight(mask),
            DoorNorth => DoorHingeRight(mask),
            _ => !DoorHingeRight(mask),
        };
    }

    public static byte ToggleDoor(byte mask) => DoorState(
        DoorFacing(mask), DoorHingeRight(mask), !DoorIsOpen(mask));

    /// <summary>Nejbližší světový směr, kterým se hráč dívá.</summary>
    public static int DoorFacingFromDirection(float x, float z)
    {
        if (MathF.Abs(x) > MathF.Abs(z))
        {
            return x >= 0f ? DoorEast : DoorWest;
        }

        return z >= 0f ? DoorSouth : DoorNorth;
    }

    /// <summary>Vodorovný krok doprava z pohledu hráče stojícího před dveřmi.</summary>
    public static Vector3i DoorRightStep(int facing) => facing switch
    {
        DoorEast => Vector3i.UnitZ,
        DoorWest => -Vector3i.UnitZ,
        DoorNorth => Vector3i.UnitX,
        _ => -Vector3i.UnitX,
    };

    public static bool TrapdoorIsOpen(byte mask) => TrapdoorFinalState(mask) is
        TrapdoorOpenBottomAlongX or TrapdoorOpenBottomAlongZ
        or TrapdoorOpenTopAlongX or TrapdoorOpenTopAlongZ;

    public static bool TrapdoorIsTop(byte mask) => TrapdoorFinalState(mask) is
        TrapdoorTopAlongX or TrapdoorTopAlongZ
        or TrapdoorOpenTopAlongX or TrapdoorOpenTopAlongZ;

    public static bool TrapdoorIsAlongZ(byte mask) => TrapdoorFinalState(mask) is
        TrapdoorBottomAlongZ or TrapdoorTopAlongZ
        or TrapdoorOpenBottomAlongZ or TrapdoorOpenTopAlongZ;

    public static byte TrapdoorClosed(bool top, bool alongZ) => (top, alongZ) switch
    {
        (false, false) => TrapdoorBottomAlongX,
        (false, true) => TrapdoorBottomAlongZ,
        (true, false) => TrapdoorTopAlongX,
        _ => TrapdoorTopAlongZ,
    };

    public static byte ToggleTrapdoor(byte mask) => TrapdoorFinalState(mask) switch
    {
        TrapdoorBottomAlongX => TrapdoorOpenBottomAlongX,
        TrapdoorBottomAlongZ => TrapdoorOpenBottomAlongZ,
        TrapdoorTopAlongX => TrapdoorOpenTopAlongX,
        TrapdoorTopAlongZ => TrapdoorOpenTopAlongZ,
        TrapdoorOpenBottomAlongX => TrapdoorBottomAlongX,
        TrapdoorOpenBottomAlongZ => TrapdoorBottomAlongZ,
        TrapdoorOpenTopAlongX => TrapdoorTopAlongX,
        TrapdoorOpenTopAlongZ => TrapdoorTopAlongZ,
        _ => TrapdoorBottomAlongX,
    };

    public static bool TrapdoorIsAnimating(byte mask) =>
        (mask & TrapdoorAnimatingMask) == TrapdoorAnimatingMarker;

    public static byte TrapdoorAnimatingState(byte finalState) => (byte)(
        TrapdoorAnimatingMarker | TrapdoorIndex(TrapdoorFinalState(finalState)));

    public static byte TrapdoorFinalState(byte mask)
    {
        if (!TrapdoorIsAnimating(mask))
        {
            return mask;
        }

        return TrapdoorFromIndex(mask & 0x07);
    }

    private static int TrapdoorIndex(byte mask) => mask switch
    {
        TrapdoorBottomAlongX => 0,
        TrapdoorBottomAlongZ => 1,
        TrapdoorTopAlongX => 2,
        TrapdoorTopAlongZ => 3,
        TrapdoorOpenBottomAlongX => 4,
        TrapdoorOpenBottomAlongZ => 5,
        TrapdoorOpenTopAlongX => 6,
        TrapdoorOpenTopAlongZ => 7,
        _ => 0,
    };

    private static byte TrapdoorFromIndex(int index) => index switch
    {
        1 => TrapdoorBottomAlongZ,
        2 => TrapdoorTopAlongX,
        3 => TrapdoorTopAlongZ,
        4 => TrapdoorOpenBottomAlongX,
        5 => TrapdoorOpenBottomAlongZ,
        6 => TrapdoorOpenTopAlongX,
        7 => TrapdoorOpenTopAlongZ,
        _ => TrapdoorBottomAlongX,
    };

    /// <summary>Kvádry všech vyplněných dílků. Pro plný blok jediný kvádr přes celý blok.</summary>
    public static IEnumerable<Aabb> Colliders(byte mask)
    {
        if (mask == Full)
        {
            yield return new Aabb(Vector3.Zero, Vector3.One);
            yield break;
        }

        for (int j = 0; j < Steps; j++)
        {
            for (int k = 0; k < Steps; k++)
            {
                for (int i = 0; i < Steps; i++)
                {
                    if (Has(mask, i, j, k))
                    {
                        yield return Piece(i, j, k);
                    }
                }
            }
        }
    }
}

/// <summary>
/// Druhý materiál uvnitř bloku a dílky, které zabírá.
///
/// <para>Blok nese jeden materiál v paletě chunku. To stačilo, dokud byl každý blok plná
/// kostka; jenže kmen zabírá jediný dílek a zbytek téhož bloku má být listí. Bez druhého
/// materiálu se listí do bloku s kmenem nevešlo a kolem každého kmene zůstala díra
/// o velikosti celého bloku.</para>
///
/// <para>Dva materiály stačí: víc než kmen a listí se v jednom bloku nepotká.</para>
/// </summary>
public readonly record struct ExtraPieces(ushort Block, byte Mask)
{
    /// <summary>Nemá blok druhý materiál?</summary>
    public bool IsEmpty => Mask == PieceMask.Empty || Block == 0;
}
