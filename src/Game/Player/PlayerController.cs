using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;
using Tesseris.Game.World;

namespace Tesseris.Game.Player;

/// <summary>
/// Pohyb a kolize hráče.
///
/// Hráč je kvádr 0,6 × 1,8 × 0,6 stojící nohama na <see cref="Position"/>. Kolize se řeší
/// po osách: každá osa se posune zvlášť a při nárazu se poloha srazí přesně na dotyk se
/// stěnou. Díky tomu hráč po zdi klouže místo aby se zastavil.
///
/// Pohyb se dělí na podkroky nejvýš po půl bloku. Bez toho by hráč při vyšší rychlosti nebo
/// po zadrhnutí framu prolétl tenkou stěnou skrz, protože test se dívá jen na cílovou polohu.
///
/// Třída nesahá na grafické API ani na vstup, takže se celá dá testovat proti světu bez okna.
/// </summary>
public sealed class PlayerController
{
    public const float Width = 0.6f;
    public const float Height = 1.8f;

    /// <summary>Výška fyzické kolize při crouchi. Hráč se vejde do průchodu vysokého 1,5 bloku.</summary>
    public const float CrouchHeight = 1.5f;

    /// <summary>Výška očí nad chodidly.</summary>
    public const float EyeHeight = 1.62f;

    /// <summary>Výška očí při crouchi. Odpovídá zřetelnému, ale ne přehnanému skrčení.</summary>
    public const float CrouchEyeHeight = 1.27f;

    /// <summary>Jak vysoký schod hráč vyjde bez skoku.</summary>
    public const float StepHeight = 0.6f;

    public const float WalkSpeed = 4.6f;
    public const float SprintMultiplier = 2.2f;
    public const float FlySpeed = 22f;

    /// <summary>Crouch zachová chůzi, ale zpomalí ji na 35 %.</summary>
    public const float CrouchSpeedMultiplier = 0.35f;

    /// <summary>Rychlost plynulého přechodu kamery do crouche a zpět.</summary>
    private const float CrouchTransitionSharpness = 14f;

    /// <summary>Gravitační zrychlení v blocích za vteřinu na druhou.</summary>
    public const float Gravity = 30f;
    private const float JumpVelocity = 9.2f;
    private const float TerminalVelocity = 60f;

    /// <summary>Kolik z tíže se ve vodě uplatní. Zbytek nahradí vztlak.</summary>
    private const float WaterGravityFactor = 0.22f;

    /// <summary>Zrychlení vzhůru při držení skoku pod vodou.</summary>
    private const float SwimUpAcceleration = 22f;

    /// <summary>Nejvyšší svislá rychlost ve vodě. Voda se nedá „proletět".</summary>
    private const float SwimTerminalVelocity = 5.5f;

    /// <summary>
    /// Odpor vody za sekundu. Rychlost se každou sekundu vynásobí tímhle číslem, takže
    /// pád do vody se rychle utlumí místo aby hráč prostřelil dno.
    /// </summary>
    private const float WaterDrag = 0.02f;

    /// <summary>Ve vodě se plave pomaleji než chodí.</summary>
    private const float SwimSpeedFactor = 0.62f;

    /// <summary>
    /// Rychlost vzhůru při vylézání z vody na břeh. Vyjde z ní zhruba blok a čtvrt,
    /// tedy dost na břeh i na jednoblokový schod.
    /// </summary>
    private const float LedgeClimbVelocity = 8.6f;

    /// <summary>Nejdelší povolený podkrok. Půl bloku bezpečně zabrání protunelování.</summary>
    private const float MaxSubStep = 0.5f;

    /// <summary>
    /// Mezera, o kterou se hráč po nárazu odsadí od stěny. Bez ní by zůstal přesně na
    /// hranici a test kolize by ho v dalším framu považoval za zaseknutého.
    /// </summary>
    private const float SkinWidth = 1e-3f;

    /// <summary>Jak hluboko pod chodidly se při crouchi hledá opora.</summary>
    private const float LedgeProbe = 0.08f;

    /// <summary>
    /// Polovina šířky plochy pod středem chodidel, která musí zůstat nad podkladem.
    /// </summary>
    /// <remarks>
    /// Kontrola přes celý 0,6blokový hitbox dovolila vysunout střed hráče skoro 0,3 bloku
    /// za hranu. Tahle menší plocha nechá asi 0,12 bloku pro výhled dolů a stavění, ale
    /// pořád drží většinu hráče bezpečně nad podkladem.
    /// </remarks>
    private const float LedgeSupportHalfWidth = 0.12f;

    /// <summary>Kolik půlení stačí na dohledání dotyku. Dvanáct dá přesnost pod desetinu milimetru.</summary>
    private const int ContactRefinements = 12;

    private const float HalfWidth = Width / 2f;

    /// <summary>
    /// Stojí hráč na zemi po dosud provedených podkrocích tohohle framu?
    ///
    /// Je to zvlášť od <see cref="OnGround"/>, protože dopad i výstup na schod se můžou stát
    /// uprostřed pohybu a musí se propsat do výsledku — kdyby se <see cref="OnGround"/>
    /// nastavovalo až na konci z jediné hodnoty, výstup na schod by se ztratil.
    /// </summary>
    private bool _groundedThisMove;

    /// <summary>
    /// Narazil hráč při posledním pohybu do svislé stěny? Podle toho se ve vodě pozná,
    /// že tlačí do břehu a má se z ní vyškrábat ven.
    /// </summary>
    private bool _blockedHorizontally;

    public PlayerController(Vector3 position)
    {
        Position = position;
    }

    /// <summary>Poloha chodidel, uprostřed půdorysu.</summary>
    public Vector3 Position { get; set; }

    public Vector3 Velocity { get; set; }

    /// <summary>Stojí hráč na pevném podkladu?</summary>
    public bool OnGround { get; private set; }

    /// <summary>
    /// Je hráč fyzicky skrčený? Po puštění crouche zůstane true, dokud nad hlavou není místo.
    /// </summary>
    public bool Crouching { get; private set; }

    /// <summary>Aktuální výška kolizního kvádru.</summary>
    public float CollisionHeight => Crouching ? CrouchHeight : Height;

    /// <summary>Aktuální plynule měněná výška očí nad chodidly.</summary>
    public float CurrentEyeHeight { get; private set; } = EyeHeight;

    /// <summary>
    /// Rychlost posledního nárazu do země v tomto kroku. Nula, když k dopadu nedošlo.
    /// </summary>
    /// <remarks>
    /// Svislá rychlost se při kolizi vynuluje, takže systém životů by ji po pohybu jinak už
    /// neviděl a počítal pád z rychlosti předchozího snímku.
    /// </remarks>
    public float ImpactSpeed { get; private set; }

    /// <summary>
    /// O kolik fyzika v tomto updatu automaticky zvedla chodidla při výstupu na schod.
    /// Kamera tuto změnu krátce dorovná, zatímco kolize zůstane okamžitě na správném místě.
    /// </summary>
    public float StepRiseThisUpdate { get; private set; }

    /// <summary>Prochází hráč zdmi? Používá se pro prohlížení světa a při měření streamingu.</summary>
    public bool NoClip { get; set; }

    /// <summary>Je hráč ve vodě? Přepíná fyziku na plavání.</summary>
    public bool InWater { get; private set; }

    /// <summary>Dotýká se hráč žebříku a může po něm lézt?</summary>
    public bool OnLadder { get; private set; }

    /// <summary>
    /// Sahá hráč do vody?
    /// </summary>
    /// <remarks>
    /// Ptá se na blok u nohou a v pase. Stačí to a je to levné: voda je souvislá, takže
    /// dva vzorky odchytí i mělčinu po kolena. Ptát se na celý kvádr by znamenalo desítky
    /// vyhledání každý frame kvůli rozdílu, který není poznat.
    /// </remarks>
    private bool IsInWater(VoxelWorld world)
    {
        return IsWaterAt(world, Position + new Vector3(0f, 0.2f, 0f))
            || IsWaterAt(world, Position + new Vector3(0f, Height * 0.5f, 0f));
    }

    private static bool IsWaterAt(VoxelWorld world, Vector3 point) => world.ContainsWater(
        (int)MathF.Floor(point.X), (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z));

    private bool IsOnLadder(VoxelWorld world)
    {
        Aabb self = Bounds;
        var probe = new Aabb(
            self.Min - new Vector3(0.08f, 0f, 0.08f),
            self.Max + new Vector3(0.08f, 0f, 0.08f));
        int minX = (int)MathF.Floor(probe.Min.X);
        int minY = (int)MathF.Floor(probe.Min.Y);
        int minZ = (int)MathF.Floor(probe.Min.Z);
        int maxX = (int)MathF.Ceiling(probe.Max.X) - 1;
        int maxY = (int)MathF.Ceiling(probe.Max.Y) - 1;
        int maxZ = (int)MathF.Ceiling(probe.Max.Z) - 1;

        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            ushort block = world.GetBlock(x, y, z);
            if (world.Registry.ShapeOf(block) != BlockShape.Ladder) continue;
            if (Hits(PieceMask.LadderCollider(world.GetPieces(x, y, z)), x, y, z, probe))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Poloha očí, ze které se kreslí a střílí paprsek.</summary>
    public Vector3 EyePosition => Position + new Vector3(0f, CurrentEyeHeight, 0f);

    /// <summary>Kvádr, který hráč zabírá.</summary>
    public Aabb Bounds => BoundsAt(Position, CollisionHeight);

    /// <summary>Kvádr hráče, kdyby stál na zadané poloze.</summary>
    public static Aabb BoundsAt(Vector3 position) => BoundsAt(position, Height);

    private static Aabb BoundsAt(Vector3 position, float height) => new(
        new Vector3(position.X - HalfWidth, position.Y, position.Z - HalfWidth),
        new Vector3(position.X + HalfWidth, position.Y + height, position.Z + HalfWidth));

    /// <summary>
    /// Zabíral by blok na téhle souřadnici místo, kde hráč stojí?
    ///
    /// Dotyk se nepočítá — blok pod nohama nebo těsně u boku je v pořádku, jinak by nešlo
    /// postavit se na vlastní podlahu. Řeší se jen skutečné pronikání.
    /// </summary>
    public bool Overlaps(Vector3i blockPosition)
    {
        Aabb self = Bounds;

        return blockPosition.X + 1 > self.Min.X && blockPosition.X < self.Max.X
            && blockPosition.Y + 1 > self.Min.Y && blockPosition.Y < self.Max.Y
            && blockPosition.Z + 1 > self.Min.Z && blockPosition.Z < self.Max.Z;
    }

    /// <summary>Je zadaný kvádr volný, tedy neprotíná žádný pevný blok?</summary>
    public static bool IsFree(VoxelWorld world, in Aabb box)
    {
        ArgumentNullException.ThrowIfNull(world);

        int minX = (int)MathF.Floor(box.Min.X);
        int minY = (int)MathF.Floor(box.Min.Y);
        int minZ = (int)MathF.Floor(box.Min.Z);

        // Horní mez se posouvá dovnitř: kvádr končící přesně na celém čísle se bloku
        // za hranicí jen dotýká a to za kolizi nepovažujeme.
        int maxX = (int)MathF.Ceiling(box.Max.X) - 1;
        int maxY = (int)MathF.Ceiling(box.Max.Y) - 1;
        int maxZ = (int)MathF.Ceiling(box.Max.Z) - 1;

        for (int y = minY; y <= maxY; y++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!world.IsSolid(x, y, z))
                    {
                        continue;
                    }

                    MicroBlock? micro = world.GetMicro(x, y, z);
                    if (micro is null)
                    {
                        // NE KAŽDÝ BLOK VYPLŇUJE SVŮJ OBJEM CELÝ. Strom se sází na dvakrát
                        // jemnější mřížce, takže jeho blok může být jen z několika dílků —
                        // a kdyby se bral jako plná kostka, hráč by se zastavil ve vzduchu
                        // půl bloku od dřeva, na které se dívá.
                        byte mask = world.GetPieces(x, y, z);
                        ushort material = world.GetBlock(x, y, z);
                        BlockShape shape = world.Registry.ShapeOf(material);

                        if (shape == BlockShape.HytaleModel
                            && world.Registry.HytaleModelOf(material) is { } hytale)
                        {
                            var collider = new Aabb(
                                hytale.Bounds.Min + new Vector3(0.5f, 0f, 0.5f),
                                hytale.Bounds.Max + new Vector3(0.5f, 0f, 0.5f));
                            if (Hits(collider, x, y, z, box))
                            {
                                return false;
                            }

                            continue;
                        }

                        if (mask == PieceMask.Full && shape != BlockShape.Post)
                        {
                            return false;
                        }

                        foreach (Aabb collider in PieceMask.Colliders(shape, mask))
                        {
                            if (Hits(collider, x, y, z, box))
                            {
                                return false;
                            }
                        }

                        // Druhá vrstva téhož bloku, tedy listí kolem kmene.
                        ExtraPieces extra = world.GetExtra(x, y, z);

                        if (!extra.IsEmpty)
                        {
                            IEnumerable<Aabb> around = shape == BlockShape.Post
                                ? PieceMask.PostFrame()
                                : PieceMask.Colliders(extra.Mask);

                            foreach (Aabb collider in around)
                            {
                                if (Hits(collider, x, y, z, box))
                                {
                                    return false;
                                }
                            }
                        }

                        continue;
                    }

                    if (HitsMicro(world, micro, x, y, z, box))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Naráží kvádr do otesaného bloku?
    ///
    /// Testuje se proti sloučeným kvádrům z deduplikační zásoby, ne proti jednotlivým
    /// mikrovoxelům — z rovné desky je jeden kvádr místo dvou set padesáti šesti.
    ///
    /// Dotyk se nepočítá. Kdyby ano, hráč stojící na otesaném povrchu by se považoval
    /// za zaseknutého a propadl by se, nebo by se vůbec nehnul.
    /// </summary>
    private static bool HitsMicro(VoxelWorld world, MicroBlock micro, int x, int y, int z, in Aabb box)
    {
        MicroShape shape = world.MicroShapes.Get(micro);

        foreach (Aabb collider in shape.Colliders)
        {
            if (Hits(collider, x, y, z, box))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Naráží kvádr hráče do kolideru zadaného v souřadnicích bloku (0 až 1)?
    ///
    /// <para>Dotyk se nepočítá. Kdyby ano, hráč stojící na povrchu by se považoval za
    /// zaseknutého a propadl by se, nebo by se vůbec nehnul.</para>
    /// </summary>
    private static bool Hits(in Aabb collider, int x, int y, int z, in Aabb box)
    {
        float minX = x + collider.Min.X;
        float minY = y + collider.Min.Y;
        float minZ = z + collider.Min.Z;
        float maxX = x + collider.Max.X;
        float maxY = y + collider.Max.Y;
        float maxZ = z + collider.Max.Z;

        return box.Min.X < maxX && box.Max.X > minX
            && box.Min.Y < maxY && box.Max.Y > minY
            && box.Min.Z < maxZ && box.Max.Z > minZ;
    }

    /// <summary>
    /// Posune hráče o jeden krok simulace.
    /// </summary>
    /// <param name="wishDirection">
    /// Požadovaný vodorovný směr ve světových souřadnicích. Delší než jednotkový se zkrátí,
    /// aby chůze do rohu nebyla rychlejší než rovně.
    /// </param>
    /// <param name="jump">Drží hráč skok?</param>
    /// <param name="sprint">Drží hráč zrychlení?</param>
    /// <param name="verticalWish">Svislý vstup, uplatní se jen v režimu procházení zdí.</param>
    public void Update(
        VoxelWorld world,
        Vector3 wishDirection,
        bool jump,
        bool sprint,
        float verticalWish,
        float deltaSeconds,
        bool crouch = false)
    {
        ArgumentNullException.ThrowIfNull(world);

        ImpactSpeed = 0f;
        StepRiseThisUpdate = 0f;

        // Stisk hráče skrčí okamžitě. Po puštění se kolize smí zvětšit teprve ve chvíli,
        // kdy se celý stojící kvádr vejde do světa; pod nízkým stropem tak hlava neprojde blokem.
        // V noclipu překážky nemají význam, proto tam stav vždy přímo kopíruje vstup.
        if (crouch || NoClip)
        {
            Crouching = crouch;
        }
        else if (Crouching && IsFree(world, BoundsAt(Position)))
        {
            Crouching = false;
        }

        if (deltaSeconds <= 0f)
        {
            return;
        }

        // Dlouhý frame se ořízne. Jinak by po zadrhnutí následoval skok přes půl chunku.
        deltaSeconds = MathF.Min(deltaSeconds, 0.1f);

        // Plynulý crouch kamery. Exponenciální přiblížení je nezávislé na FPS a na začátku
        // reaguje rychle, ale nedělá jednosnímkový skok o třetinu bloku.
        float targetEyeHeight = Crouching ? CrouchEyeHeight : EyeHeight;
        float eyeBlend = 1f - MathF.Exp(-CrouchTransitionSharpness * deltaSeconds);

        CurrentEyeHeight += (targetEyeHeight - CurrentEyeHeight) * eyeBlend;

        if (MathF.Abs(CurrentEyeHeight - targetEyeHeight) < 1e-4f)
        {
            CurrentEyeHeight = targetEyeHeight;
        }

        wishDirection.Y = 0f;
        float wishLengthSquared = wishDirection.LengthSquared;
        if (wishLengthSquared > 1f)
        {
            wishDirection /= MathF.Sqrt(wishLengthSquared);
        }

        if (NoClip)
        {
            float flySpeed = sprint ? FlySpeed * SprintMultiplier : FlySpeed;

            if (Crouching)
            {
                flySpeed *= CrouchSpeedMultiplier;
            }

            Position += (wishDirection + new Vector3(0f, verticalWish, 0f)) * flySpeed * deltaSeconds;
            Velocity = Vector3.Zero;
            OnGround = false;
            return;
        }

        InWater = IsInWater(world);
        OnLadder = !InWater && IsOnLadder(world);

        // Crouch a sprint se nekombinují. Skrčený hráč jde vždy pomalu, ale nezastaví se.
        float speed = Crouching
            ? WalkSpeed * CrouchSpeedMultiplier
            : (sprint ? WalkSpeed * SprintMultiplier : WalkSpeed);
        Vector3 velocity = Velocity;

        if (InWater)
        {
            speed *= SwimSpeedFactor;
        }
        else if (OnLadder)
        {
            speed *= 0.72f;
        }

        velocity.X = wishDirection.X * speed;
        velocity.Z = wishDirection.Z * speed;

        if (InWater)
        {
            // PLAVÁNÍ. Voda není pevná, takže se v ní bez tohohle klesá stejně rychle
            // jako ve vzduchu a hráč skončí na dně bez možnosti se vrátit.
            //
            // Vztlak nedrží hráče na hladině sám: bez ovládání pomalu klesá. Držením
            // skoku se stoupá, takže se dá vyplavat. Odpor je vysoký, aby se pád do
            // vody zastavil skoro hned místo aby hráč prostřelil dno.
            velocity.Y -= Gravity * WaterGravityFactor * deltaSeconds;

            if (jump)
            {
                velocity.Y += SwimUpAcceleration * deltaSeconds;
            }

            velocity.Y = Math.Clamp(velocity.Y, -SwimTerminalVelocity, SwimTerminalVelocity);
            velocity.Y *= MathF.Pow(WaterDrag, deltaSeconds);

            // VYLEZENÍ NA BŘEH.
            //
            // Bez tohohle se z vody nedalo dostat na souš: plavat vzhůru jde jen po
            // hladinu a svislá rychlost je ve vodě omezená na 5,5, což nestačí ani na
            // jeden blok. Hráč tak dojel k břehu a zůstal ve vodě.
            //
            // Když se tlačí do stěny a drží skok, dostane rychlost jako při normálním
            // skoku — a hlavně AŽ ZA omezením rychlosti ve vodě, jinak by ji omezení
            // hned zase srazilo. Vyleze tím zhruba na jeden a čtvrt bloku, tedy na břeh
            // i na jednoblokový schod.
            if (jump && _blockedHorizontally)
            {
                velocity.Y = LedgeClimbVelocity;
            }
            else if (jump && OnGround)
            {
                // Mělčina: nohy na dně, tak se dá skočit normálně.
                velocity.Y = JumpVelocity;
            }
        }
        else if (OnLadder)
        {
            // Mezerník leze nahoru, crouch dolů. Bez vstupu hráč po žebříku jen pomalu
            // sjíždí, takže se na něm dá zastavit a rozhlédnout.
            velocity.Y = jump ? 3.8f : crouch ? -3f : MathF.Max(velocity.Y - 0.6f, -1.2f);
            OnGround = false;
        }
        else
        {
            if (jump && OnGround)
            {
                velocity.Y = JumpVelocity;
                OnGround = false;
            }

            velocity.Y = MathF.Max(velocity.Y - (Gravity * deltaSeconds), -TerminalVelocity);
        }

        Velocity = velocity;

        // OnGround může na hraně nepravidelného bloku na jediný snímek vypadnout. Dokud je
        // ale pod středem chodidel opora a hráč nestoupá vzhůru, crouch musí dál držet hranu.
        bool protectLedge = Crouching && !InWater && velocity.Y <= 0f
            && (OnGround || HasSupport(world, Position));

        Move(world, velocity * deltaSeconds, protectLedge);
    }

    /// <summary>Přesune hráče na zadanou polohu a vynuluje rychlost.</summary>
    public void Teleport(Vector3 position)
    {
        Position = position;
        Velocity = Vector3.Zero;
        OnGround = false;
    }

    private void Move(VoxelWorld world, Vector3 delta, bool protectLedge)
    {
        float longest = MathF.Max(MathF.Abs(delta.X), MathF.Max(MathF.Abs(delta.Y), MathF.Abs(delta.Z)));
        int steps = Math.Max(1, (int)MathF.Ceiling(longest / MaxSubStep));
        Vector3 step = delta / steps;

        _groundedThisMove = false;
        _blockedHorizontally = false;

        for (int i = 0; i < steps; i++)
        {
            // Svisle se řeší první: po dopadu je hráč na zemi a smí v témž kroku vyjít schod.
            if (MoveAxis(world, Axis.Y, step.Y) && step.Y < 0f)
            {
                _groundedThisMove = true;
            }

            // Náraz do svislé stěny se pamatuje: podle něj se pozná, že hráč tlačí
            // do břehu, a smí se z vody vyškrábat ven.
            _blockedHorizontally |= MoveHorizontalAxis(world, Axis.X, step.X, protectLedge);
            _blockedHorizontally |= MoveHorizontalAxis(world, Axis.Z, step.Z, protectLedge);
        }

        OnGround = _groundedThisMove;
    }

    /// <summary>
    /// Posune hráče vodorovně; při crouchi vrátí krok, po kterém by pod nohama nezůstala opora.
    /// </summary>
    private bool MoveHorizontalAxis(VoxelWorld world, Axis axis, float amount, bool protectLedge)
    {
        Vector3 before = Position;
        bool blocked = MoveAxis(world, axis, amount);

        if (!protectLedge || blocked || HasSupport(world, Position))
        {
            return blocked;
        }

        Position = before;
        Velocity = SetAxis(Velocity, axis, 0f);
        return true;
    }

    /// <summary>Dotýká se tenká plocha těsně pod chodidly pevného povrchu?</summary>
    private static bool HasSupport(VoxelWorld world, Vector3 position)
    {
        var probe = new Aabb(
            new Vector3(
                position.X - LedgeSupportHalfWidth,
                position.Y - LedgeProbe,
                position.Z - LedgeSupportHalfWidth),
            new Vector3(
                position.X + LedgeSupportHalfWidth,
                position.Y,
                position.Z + LedgeSupportHalfWidth));

        return !IsFree(world, probe);
    }

    /// <summary>Posune hráče po jedné ose a vrátí, jestli přitom do něčeho narazil.</summary>
    private bool MoveAxis(VoxelWorld world, Axis axis, float amount)
    {
        if (amount == 0f)
        {
            return false;
        }

        Vector3 target = Offset(Position, axis, amount);

        if (IsFree(world, BoundsAt(target, CollisionHeight)))
        {
            Position = target;
            return false;
        }

        // Vodorovná překážka se ještě zkusí vyjít jako schod.
        if (axis != Axis.Y && TryStepUp(world, target))
        {
            return true;
        }

        Position = FindContact(world, Position, axis, amount);

        if (axis == Axis.Y && amount < 0f)
        {
            ImpactSpeed = MathF.Max(ImpactSpeed, -Velocity.Y);
        }

        Velocity = SetAxis(Velocity, axis, 0f);
        return true;
    }

    /// <summary>
    /// Najde nejzazší volnou polohu na dané ose půlením intervalu.
    ///
    /// Vychází se z toho, že výchozí poloha je volná a cílová ne. Dvanáct půlení stačí
    /// na přesnost pod desetinu milimetru a je to mnohem levnější než posouvat se
    /// po malých krůčcích — a hlavně to funguje i když hráč narazí do několika bloků naráz.
    /// </summary>
    private Vector3 FindContact(VoxelWorld world, Vector3 start, Axis axis, float amount)
    {
        float free = 0f;
        float blocked = amount;

        for (int i = 0; i < ContactRefinements; i++)
        {
            float middle = (free + blocked) * 0.5f;

            if (IsFree(world, BoundsAt(Offset(start, axis, middle), CollisionHeight)))
            {
                free = middle;
            }
            else
            {
                blocked = middle;
            }
        }

        // Odsazení od stěny, aby hráč v dalším framu nevycházel z dotyku.
        float safe = free - (MathF.Sign(amount) * SkinWidth);
        if (MathF.Sign(safe) != MathF.Sign(amount))
        {
            safe = 0f;
        }

        return Offset(start, axis, safe);
    }

    /// <summary>
    /// Zkusí vyjít překážku jako schod.
    ///
    /// Podmínkou je, že hráč stojí na zemi (ve vzduchu se schody nechodí), že se vejde
    /// o kus výš a že tam po posunutí opravdu na něčem stojí. Bez poslední kontroly by
    /// hráč u každé zdi povyskočil, i kdyby za ní byla propast.
    /// </summary>
    private bool TryStepUp(VoxelWorld world, Vector3 blockedTarget)
    {
        // Bere se i podklad zjištěný v probíhajícím pohybu: hráč, který právě dopadl,
        // smí ve stejném framu vyjít schod.
        if (!OnGround && !_groundedThisMove)
        {
            return false;
        }

        Vector3 raised = blockedTarget with { Y = blockedTarget.Y + StepHeight };
        if (!IsFree(world, BoundsAt(raised, CollisionHeight)))
        {
            return false;
        }

        // Sesednout zpět na povrch schodu. Když se propadne celá výška, nebylo na co vystoupit.
        Vector3 settled = FindContact(world, raised, Axis.Y, -StepHeight);
        if (settled.Y <= blockedTarget.Y + SkinWidth)
        {
            return false;
        }

        StepRiseThisUpdate += MathF.Max(0f, settled.Y - Position.Y);
        Position = settled;
        _groundedThisMove = true;
        return true;
    }

    private static Vector3 Offset(Vector3 value, Axis axis, float amount) => axis switch
    {
        Axis.X => value with { X = value.X + amount },
        Axis.Y => value with { Y = value.Y + amount },
        _ => value with { Z = value.Z + amount },
    };

    private static Vector3 SetAxis(Vector3 value, Axis axis, float amount) => axis switch
    {
        Axis.X => value with { X = amount },
        Axis.Y => value with { Y = amount },
        _ => value with { Z = amount },
    };

    private enum Axis
    {
        X,
        Y,
        Z,
    }
}
