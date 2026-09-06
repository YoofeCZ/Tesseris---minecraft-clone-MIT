using OpenTK.Mathematics;
using Tesseris.Game.World.Navigation;

namespace Tesseris.Game.Colony;

/// <summary>
/// Fyzické tělo kolonisty: spojitá poloha, kolize s terénem, gravitace a skok.
/// </summary>
/// <remarks>
/// <para><b>Proč to vzniklo.</b> Kolonista byl index do mřížky. Pohyb byl skok o celou buňku
/// jednou za třináct tiků a plynulost dělala až interpolace při vykreslování — proto to
/// vypadalo jako figurka na šachovnici, i když se mezi kroky nezastavoval. Kolize neexistovala
/// a přes překážku se dalo dostat jen po schodu. Mřížka zůstává tam, kde dává smysl: v hledání
/// cesty. Pohyb po ní ne.</para>
///
/// <para><b>Kolize se řeší proti mřížce pochůznosti, ne proti voxelům.</b> Hráč se ptá
/// <c>PlayerController.IsFree</c>, což prochází bloky, dílky, mikrovoxely a modely — a
/// <c>Walkability</c> si u toho sama poznamenává, že <c>PieceMask.Colliders</c> je
/// <c>yield return</c> iterátor, tedy alokace na každé volání. Při dvou stech kolonistech
/// šedesátkrát za vteřinu je to neprůchodné (pravidlo 6.5). Mřížka odpoví bitovým testem
/// a hlavně dá <b>stejnou</b> odpověď jako pathfinding: kdyby se pohyb ptal jinam než
/// plánování, kolonista by uvázl na cestě, kterou mu graf slíbil.</para>
///
/// <para><b>Po osách zvlášť, stejně jako hráč.</b> Díky tomu se o zeď klouze místo zastavení.
/// Podkroky nejsou potřeba: za tik se ujde 4,6/60 = 0,077 bloku, tedy daleko pod půl bloku,
/// kde hrozí protunelování.</para>
///
/// <para><b>Podlaha je celé patro buňky.</b> Přesnou výšku povrchu (půlblok, schod, otesaný
/// blok) umí <c>Walkability.TrySupportSurface</c>, ale za cenu těch alokací. Mřížka je
/// postavená tak, že buňka je pochůzná právě tehdy, když v ní nohy můžou být, takže spodní
/// hrana buňky je bezpečné dno. Je to stejná aproximace, jakou dělal pohyb po buňkách před
/// tímhle — žádná regrese, jen se jí teď říká jménem.</para>
///
/// <para><b>Determinismus</b> (pravidlo 6.6): počítá se ve <c>float</c>, ale pořadí operací je
/// pevné a žádná hodnota nezávisí na délce snímku — krok je vždycky <see cref="StepSeconds"/>.
/// Odmocnina i násobení jsou v IEEE 754 korektně zaokrouhlené, takže tentýž vstup dá tentýž
/// výstup.</para>
/// </remarks>
public static class ColonistBody
{
    /// <summary>Šířka těla. Stejná jako hráč — kudy projde on, projde i kolonista.</summary>
    public const float Width = Player.PlayerController.Width;

    /// <summary>Polovina šířky. Půdorys je čtverec se středem v poloze nohou.</summary>
    public const float HalfWidth = Width / 2f;

    /// <summary>Rychlost chůze za prací. Přesně rychlost hráče.</summary>
    public const float WalkSpeed = Player.PlayerController.WalkSpeed;

    /// <summary>Rychlost procházky. Kdo nikam nespěchá, se courá.</summary>
    /// <remarks>
    /// Dvě třetiny chůze. Před spojitou polohou to bylo 20 tiků na buňku proti 13, tedy
    /// 3,0 proti 4,62 bloku za vteřinu — tenhle poměr se zachovává, ať se procházka pozná.
    /// </remarks>
    public const float WanderSpeed = 3.0f;

    /// <summary>Tíže. Stejná jako u hráče, jinak by kolonista padal jinak než svět.</summary>
    public const float Gravity = Player.PlayerController.Gravity;

    /// <summary>
    /// Počáteční rychlost skoku.
    /// </summary>
    /// <remarks>
    /// Vyjde z ní 9,2² / (2 × 30) = <b>1,41 bloku</b>, tedy pohodlně přes jednoblokovou
    /// překážku ze zadání a nic víc. Je to táž hodnota, jakou má hráč, takže kam doskočí
    /// hráč, doskočí i kolonista — a hlavně to sedí na <c>Walkability.StepUp</c> = 1, podle
    /// kterého plánuje A*. Kdyby kolonista doskočil níž, sliboval by mu graf kroky, které
    /// tělo neprovede.
    /// </remarks>
    public const float JumpVelocity = 9.2f;

    /// <summary>Nejvyšší rychlost pádu. Bez ní by dlouhý pád protunelovalo.</summary>
    public const float TerminalVelocity = 40f;

    /// <summary>Délka jednoho simulačního kroku. Pevných 60 Hz (pravidlo 6.6).</summary>
    public const float StepSeconds = 1f / Engine.Core.FixedClock.DefaultTicksPerSecond;

    /// <summary>Jak blízko musí kolonista být, aby se bod cesty počítal za dosažený.</summary>
    /// <remarks>
    /// Menší poloměr znamená, že kolonista dojde přesněji doprostřed buňky; větší, že se dřív
    /// otočí k dalšímu bodu a chůze je plynulejší. Čtvrt bloku je kompromis: pořád skončí ve
    /// správné buňce (což potřebuje kopání i odevzdávání), ale nedělá kolem cíle piruety.
    /// </remarks>
    public const float ArrivalRadius = 0.25f;

    /// <summary>Střed buňky ve výšce její podlahy. Sem míří pohyb po cestě.</summary>
    public static Vector3 CentreOf(Vector3i cell) => new(cell.X + 0.5f, cell.Y, cell.Z + 0.5f);

    /// <summary>Buňka, ve které kolonista stojí. Nohy leží uvnitř téhle buňky.</summary>
    /// <remarks>
    /// <b>Zaokrouhluje se dolů, ne k nejbližší.</b> Poloha nohou je spodní hrana buňky, takže
    /// při stání na y = 2 vyjde přesně 2. Kdyby se zaokrouhlovalo k nejbližšímu, spadl by
    /// stojící kolonista o patro níž hned prvním dotazem.
    /// </remarks>
    public static Vector3i CellOf(Vector3 feet) => new(
        (int)MathF.Floor(feet.X),
        (int)MathF.Floor(feet.Y + 0.001f),
        (int)MathF.Floor(feet.Z));

    /// <summary>
    /// Vejde se tělo na tuhle polohu?
    /// </summary>
    /// <remarks>
    /// <para>Testují se čtyři rohy půdorysu, ne jen střed. Bez rohů by kolonista prošel rohem
    /// zdi — dva bloky do kříže vypadají zvenku jako průchod, ale nejsou. Je to táž past,
    /// kterou u chůze po buňkách řešila zvláštní kontrola sousedních os.</para>
    ///
    /// <para><b>Sestup je volný, výstup ne.</b> Roh nad dírou se bere za průchodný, protože
    /// tam kolonista spadne a to je platný stav světa. Roh, kde je pochůzno až o patro výš,
    /// průchodný není — to je právě ta překážka, přes kterou se má skákat.</para>
    /// </remarks>
    public static bool IsFree(NavGraph graph, Vector3 feet)
    {
        ArgumentNullException.ThrowIfNull(graph);

        int y = (int)MathF.Floor(feet.Y + 0.001f);

        return CornerFree(graph, feet.X - HalfWidth, y, feet.Z - HalfWidth)
            && CornerFree(graph, feet.X + HalfWidth, y, feet.Z - HalfWidth)
            && CornerFree(graph, feet.X - HalfWidth, y, feet.Z + HalfWidth)
            && CornerFree(graph, feet.X + HalfWidth, y, feet.Z + HalfWidth);
    }

    /// <summary>Je pod tímhle rohem kde stát, nebo aspoň kam spadnout?</summary>
    /// <remarks>
    /// Dolů se dívá o dvě patra, což je <c>Walkability.StepDown</c>. Hlouběji už je to pád
    /// a ten se řeší gravitací; kdyby se sem počítal, kolonista by ochotně vešel nad propast.
    /// </remarks>
    private static bool CornerFree(NavGraph graph, float x, int y, float z)
    {
        int cellX = (int)MathF.Floor(x);
        int cellZ = (int)MathF.Floor(z);

        for (int drop = 0; drop <= World.Walkability.StepDown; drop++)
        {
            if (graph.IsStandable(new Vector3i(cellX, y - drop, cellZ)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Posune tělo vodorovně s kolizí. Osy zvlášť, aby se o zeď klouzalo.
    /// </summary>
    /// <remarks>
    /// <para><b>Kdo už vězí, se smí hýbat.</b> Tělo se dá do neplatné polohy dostat i bez
    /// chyby v kolizi: doskok na hranu nechá roh půdorysu zasahovat do zdi a hráč může
    /// postavit blok komukoli pod nohy. Bez téhle výjimky by pak neprošel ŽÁDNÝ pohyb —
    /// naměřeno jako „přeskočil zeď a zůstal stát na x = 11,03", tedy dvě setiny za ní.
    /// Uvíznutí je horší než chvíle, kdy se rameno prolne se zdí.</para>
    ///
    /// <para><b>Ale jen ven, ne dál dovnitř.</b> Uvízlý smí do polohy, která už je volná,
    /// nebo která je stejně špatná; z toho vyjde pohyb směrem k volnému prostoru. Kdyby se
    /// povolilo cokoli, byla by z toho průchodnost zdí pro každého, kdo se jednou dotkl rohu.</para>
    /// </remarks>
    /// <returns>Narazilo tělo do překážky? Podle toho se pozná, kdy má smysl skočit.</returns>
    public static bool TryMoveHorizontal(NavGraph graph, ref Vector3 feet, float dx, float dz)
    {
        ArgumentNullException.ThrowIfNull(graph);

        bool stuck = !IsFree(graph, feet);
        bool blocked = false;

        if (dx != 0f)
        {
            Vector3 candidate = feet;
            candidate.X += dx;

            if (IsFree(graph, candidate))
            {
                feet = candidate;
                stuck = false;
            }
            else if (stuck && HasFewerBlockedCorners(graph, candidate, feet))
            {
                feet = candidate;
            }
            else
            {
                blocked = true;
            }
        }

        if (dz != 0f)
        {
            Vector3 candidate = feet;
            candidate.Z += dz;

            if (IsFree(graph, candidate))
            {
                feet = candidate;
            }
            else if (stuck && HasFewerBlockedCorners(graph, candidate, feet))
            {
                feet = candidate;
            }
            else
            {
                blocked = true;
            }
        }

        return blocked;
    }

    /// <summary>Je kandidát aspoň o roh míň zaseknutý než současná poloha?</summary>
    /// <remarks>
    /// Míra „jak moc vězím" je počet rohů půdorysu, pod kterými není kde stát. Pohyb, který
    /// ho nezvýší, vede ven; pohyb, který ho zvýší, vede hlouběji do zdi.
    /// </remarks>
    private static bool HasFewerBlockedCorners(NavGraph graph, Vector3 candidate, Vector3 current) =>
        BlockedCorners(graph, candidate) <= BlockedCorners(graph, current);

    private static int BlockedCorners(NavGraph graph, Vector3 feet)
    {
        int y = (int)MathF.Floor(feet.Y + 0.001f);
        int blocked = 0;

        if (!CornerFree(graph, feet.X - HalfWidth, y, feet.Z - HalfWidth)) { blocked++; }
        if (!CornerFree(graph, feet.X + HalfWidth, y, feet.Z - HalfWidth)) { blocked++; }
        if (!CornerFree(graph, feet.X - HalfWidth, y, feet.Z + HalfWidth)) { blocked++; }
        if (!CornerFree(graph, feet.X + HalfWidth, y, feet.Z + HalfWidth)) { blocked++; }

        return blocked;
    }

    /// <summary>
    /// Uplatní tíži, pád a přistání.
    /// </summary>
    /// <remarks>
    /// <para><b>Podlaha se hledá až pod nohama, ne v buňce nohou.</b> Kolonista stojící na
    /// y = 2 má nohy přesně na 2,0; jeho buňka JE pochůzná a zároveň je to místo, kde stojí.
    /// Kdyby se dno hledalo od buňky nohou nahoru, vystřelil by o patro výš při každém kroku.</para>
    ///
    /// <para><b>Kdo stojí, nepočítá nic.</b> Dokud je pod nohama pochůzná buňka a kolonista
    /// nestoupá, funkce se hned vrátí — to je většina lidí ve většině tiků.</para>
    /// </remarks>
    public static void ApplyGravity(NavGraph graph, ref Vector3 feet, ref float velocityY, ref bool onGround)
    {
        ArgumentNullException.ThrowIfNull(graph);

        // NEZNÁMÝ CHUNK NENÍ PRÁZDNO. Navigace se udržuje jen kolem hráče, takže kolonista,
        // od kterého hráč odletí, přijde o mřížku pod nohama — ne o zem. Bez téhle podmínky
        // se z „nevím" stane pád: naměřeno po načtení savu jako 900 tiků z 900 ve vzduchu,
        // tedy propadnutí světem jen proto, že se hráč vzdálil.
        //
        // Je to táž past jako u <c>Walkability.TryFindGround</c>, kde stojí „nenačtený chunk
        // není totéž co prázdno" — a stálo to tam z přesně tohohle důvodu u zvířat.
        if (!graph.IsKnown(CellOf(feet)))
        {
            velocityY = 0f;
            onGround = true;
            return;
        }

        if (onGround && velocityY <= 0f)
        {
            // Pořád je pod nohama zem? Pak není co počítat.
            if (graph.IsStandable(CellOf(feet)))
            {
                velocityY = 0f;
                return;
            }

            onGround = false;
        }

        velocityY = MathF.Max(velocityY - (Gravity * StepSeconds), -TerminalVelocity);

        float next = feet.Y + (velocityY * StepSeconds);

        if (velocityY > 0f)
        {
            // STOUPÁNÍ. Strop se neřeší: mřížka pochůznosti nese jen to, kde se dá stát,
            // takže vyskočit pod převis znamená narazit temenem do ničeho. Je to smluvená
            // nepřesnost — skok slouží na jeden blok a tam převis nehraje roli.
            feet.Y = next;
            return;
        }

        // PÁD. Hledá se nejvyšší pochůzná buňka, jejíž podlaha leží MEZI novou a starou
        // výškou; na ni se dopadne. Bez tohohle by rychlý pád podlahu přeskočil.
        //
        // POZOR NA `y >= next`, ne `y >= floor(next)`. S celočíselnou mezí se dopadlo i na
        // podlahu, ke které pád v tomhle tiku ještě nedolétl: kolonista stojící na y = 3
        // klesl o osm setin, spodní mez vyšla floor(2,99) = 2, a protože na dvojce se stát
        // dá, propadl celý blok JEDNÍM tikem. Naměřeno jako „největší posun za tik
        // 1,0000 bloku" proti 0,0767, kolik dovolí rychlost chůze — tedy přesně ten skok
        // o buňku, kvůli kterému se tahle práce dělá.
        int from = (int)MathF.Floor(feet.Y + 0.001f);
        int lowest = (int)MathF.Floor(next);

        for (int y = from; y >= lowest; y--)
        {
            if (y > feet.Y + 0.001f || y < next)
            {
                continue;
            }

            var cell = new Vector3i((int)MathF.Floor(feet.X), y, (int)MathF.Floor(feet.Z));

            // DO NEZNÁMA SE NEPADÁ. `IsStandable` odpoví „ne" na vzduch i na nenačtený chunk
            // úplně stejně, takže se tudy propadalo dolů — a protože každý další chunk pod
            // ním byl taky neznámý, pád nikdy neskončil. Pojistka na konci metody zabírala
            // až POTOM, co kolonista do neznáma spadl, a postavila ho dovnitř, ne na hranici;
            // příští tik se propadl zase o kus. Odtud to, že počet propadlých v čase ROSTL.
            //
            // Zastavit se musí u PRVNÍ neznámé buňky. Chvíli stát ve vzduchu nad
            // nenačteným chunkem je nesrovnatelně lepší než propadnout světem.
            if (!graph.IsKnown(cell))
            {
                feet.Y = y + 1f;
                velocityY = 0f;
                onGround = true;
                return;
            }

            if (!graph.IsStandable(cell))
            {
                continue;
            }

            feet.Y = y;
            velocityY = 0f;
            onGround = true;
            return;
        }

        feet.Y = next;
        onGround = false;

        // DÍRA NA HRANICI ZNÁMÉ NAVIGACE NENÍ DÍRA VE SVĚTĚ. Mřížky se staví s rozpočtem
        // jednoho chunku na tik, takže sousední chunk pod nohama často ještě neexistuje —
        // a `IsStandable` na něj odpoví „ne", i když tam je pevná skála. Kolonista tudy
        // propadne a padá dál, protože každý další chunk pod ním je taky neznámý.
        //
        // Naměřeno ve hře při 200 lidech: kolonisté se rozptýlili přes 70 pater
        // (y 255 až 326) a počet propadlých rostl 46 → 79 → 101 → 116 z 200. Vypadalo to
        // jako rozbité vyhýbání (23 231 překryvů), protože se dole naskládali na sebe.
        //
        // Kdo by spadl do neznáma, zůstane stát na hranici. Být chvíli ve vzduchu je lepší
        // než propadnout světem, který se jen nestihl načíst.
        if (!graph.IsKnown(CellOf(feet)))
        {
            feet.Y = MathF.Floor(feet.Y) + 1f;
            velocityY = 0f;
            onGround = true;
        }
    }

    /// <summary>
    /// Má kolonista přes tuhle překážku skočit?
    /// </summary>
    /// <remarks>
    /// <b>Skáče se jen tehdy, když je za překážkou kam doskočit</b> — jinak by se kolonista
    /// zaseklý v koutě odrážel od zdi donekonečna a vypadalo by to hůř než zastavení. Dívá se
    /// o patro výš přesně v tom směru, kterým se chtěl pohnout.
    /// </remarks>
    public static bool ShouldJump(NavGraph graph, Vector3 feet, float wishX, float wishZ)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (wishX == 0f && wishZ == 0f)
        {
            return false;
        }

        // O půl bloku dopředu: dost daleko, aby to byla sousední buňka, dost blízko, aby to
        // nebyla překážka o dvě buňky dál.
        float length = MathF.Sqrt((wishX * wishX) + (wishZ * wishZ));
        float aheadX = feet.X + (wishX / length * (HalfWidth + 0.2f));
        float aheadZ = feet.Z + (wishZ / length * (HalfWidth + 0.2f));

        int y = (int)MathF.Floor(feet.Y + 0.001f);
        var above = new Vector3i((int)MathF.Floor(aheadX), y + 1, (int)MathF.Floor(aheadZ));

        return graph.IsStandable(above);
    }
}
