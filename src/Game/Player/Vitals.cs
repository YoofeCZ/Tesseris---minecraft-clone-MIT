using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Tesseris.Game.World;

namespace Tesseris.Game.Player;

/// <summary>
/// Životy hráče, dech pod vodou a zranění z pádu.
/// </summary>
/// <remarks>
/// <para><b>Je to vlastní systém, ne pár polí v okně.</b> Tři věci se tu potkávají —
/// dopadová rychlost, hladina vody a brnění — a každá z nich má vlastní pravidla, kdy se
/// smí uplatnit. Rozeseté po herní smyčce by se to nedalo otestovat ani přečíst.</para>
///
/// <para><b>Zranění se počítá z rychlosti, ne z uražené výšky.</b> Výška lže: hráč, který
/// spadne deset bloků do vody nebo doplachtí po svahu, žádný náraz nezažil. Rychlost při
/// dopadu je to, co ho zabije, a je to jediné číslo, které v tu chvíli něco znamená.</para>
/// </remarks>
public sealed class Vitals
{
    /// <summary>Kolik má hráč životů, když je zdravý.</summary>
    public const float MaxHealth = 20f;

    /// <summary>Jak dlouho hráč vydrží pod vodou, ve vteřinách.</summary>
    public const float MaxBreath = 15f;

    /// <summary>Výška pádu, která je ještě zcela bezpečná.</summary>
    public const float SafeFallBlocks = 3f;

    /// <summary>
    /// Povolená chyba při přepočtu rychlosti na výšku. Diskrétní gravitační krok může při
    /// dopadu z přesně tří bloků přidat asi 0,04 bloku rychlosti navíc.
    /// </summary>
    private const float LandingHeightEpsilon = 0.05f;

    /// <summary>Jak často a jak silně ubližuje utopení.</summary>
    private const float DrownInterval = 1f;
    private const float DrownDamage = 2f;

    /// <summary>Jak rychle se dech doplňuje nad hladinou. Násobek spotřeby.</summary>
    private const float BreathRecovery = 4f;

    /// <summary>Jak dlouho po zranění je hráč nezranitelný, ve vteřinách.</summary>
    /// <remarks>
    /// Bez toho by utopení a pád ubíraly každý snímek a hráč by zemřel dřív, než by stihl
    /// zareagovat. Půl vteřiny je dost na to, aby se dalo uhnout.
    /// </remarks>
    private const float InvulnerableSeconds = 0.5f;

    private float _drownClock;
    private float _invulnerable;

    /// <summary>Kolik životů hráči zbývá.</summary>
    public float Health { get; private set; } = MaxHealth;

    /// <summary>Kolik vteřin dechu zbývá.</summary>
    public float Breath { get; private set; } = MaxBreath;

    /// <summary>Je hráč pod hladinou po hlavu?</summary>
    public bool Submerged { get; private set; }

    /// <summary>Nejvyšší rychlost pádu od posledního doteku země. Pro ukazatel a pro test.</summary>
    public float FallSpeed { get; private set; }

    /// <summary>Je hráč mrtvý?</summary>
    public bool Dead => Health <= 0f;

    /// <summary>Kolik životů ubralo poslední zranění. Nula znamená, že se nic nestalo.</summary>
    public float LastDamage { get; private set; }

    /// <summary>Vrátí hráče do plného stavu. Volá se po smrti a při přepnutí do kreativu.</summary>
    public void Reset()
    {
        Health = MaxHealth;
        Breath = MaxBreath;
        FallSpeed = 0f;
        _drownClock = 0f;
        _invulnerable = 0f;
        LastDamage = 0f;
    }

    /// <summary>
    /// Posune životy a dech.
    /// </summary>
    /// <param name="creative">V kreativu se neubližuje: stavění je tam celý smysl.</param>
    public void Update(
        VoxelWorld world, PlayerController player, Inventory? armour, bool creative, float seconds)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(player);

        LastDamage = 0f;

        if (_invulnerable > 0f)
        {
            _invulnerable = MathF.Max(0f, _invulnerable - seconds);
        }

        Submerged = HeadUnderwater(world, player);

        if (creative)
        {
            // V kreativu se nic nepočítá, ale stav se drží plný — jinak by hráč po přepnutí
            // zpátky do přežití zemřel na dech, který mu došel, když na tom nezáleželo.
            Reset();
            return;
        }

        UpdateBreath(seconds);
        UpdateFall(player, armour, seconds);
    }

    /// <summary>Dech: pod vodou ubývá, nad hladinou se rychle vrací.</summary>
    private void UpdateBreath(float seconds)
    {
        if (!Submerged)
        {
            Breath = MathF.Min(MaxBreath, Breath + (seconds * BreathRecovery));
            _drownClock = 0f;
            return;
        }

        Breath = MathF.Max(0f, Breath - seconds);

        if (Breath > 0f)
        {
            return;
        }

        // Došel dech: ubližuje se po vteřinách, ne každý snímek.
        _drownClock += seconds;

        if (_drownClock < DrownInterval)
        {
            return;
        }

        _drownClock -= DrownInterval;

        // UTOPENÍ BRNĚNÍ NEZASTAVÍ. Chrání proti nárazu, ne proti tomu, že se nedýchá —
        // a plátová zbroj by pod vodou spíš přitěžovala.
        Hurt(DrownDamage, ignoreArmour: true, armour: null);
    }

    /// <summary>
    /// Pád: sleduje se nejvyšší rychlost dolů a účtuje se v okamžiku dopadu.
    /// </summary>
    private void UpdateFall(PlayerController player, Inventory? armour, float seconds)
    {
        _ = seconds;

        // Ve vodě se pád neúčtuje: hladina náraz ztlumí. Je to i jediná záchrana, kterou
        // hráč ve výšce má.
        if (Submerged)
        {
            FallSpeed = 0f;
            return;
        }

        if (!player.OnGround)
        {
            FallSpeed = MathF.Max(FallSpeed, -player.Velocity.Y);
            return;
        }

        float landed = MathF.Max(FallSpeed, player.ImpactSpeed);
        FallSpeed = 0f;

        float damage = FallDamage(landed);

        if (damage <= 0f)
        {
            return;
        }

        Hurt(damage, ignoreArmour: false, armour);
    }

    /// <summary>
    /// Převede nárazovou rychlost na zranění z pádu.
    /// </summary>
    /// <remarks>
    /// Z rovnice volného pádu <c>v² = 2gh</c> se nejdřív odhadne výška. Tři bloky jsou
    /// bezpečné. Každý další blok ubere jeden bod života, tedy půl srdce:
    /// <c>damage = blocks - 3</c>. Pád z 22 bloků tak vezme 19 z 20 životů a nechá
    /// přesně půl srdce. Výpočet z rychlosti zachová záchranu vodou i sklouznutí po svahu.
    /// </remarks>
    public static float FallDamage(float impactSpeed)
    {
        float speed = MathF.Max(0f, impactSpeed);
        float equivalentHeight = (speed * speed) / (2f * PlayerController.Gravity);

        if (equivalentHeight <= SafeFallBlocks + LandingHeightEpsilon)
        {
            return 0f;
        }

        return equivalentHeight - SafeFallBlocks;
    }

    /// <summary>
    /// Ubere životy, případně zkrácené o ochranu brnění.
    /// </summary>
    /// <remarks>
    /// Během chvilky po zranění se další neúčtuje. Bez toho by pád do vody v jeskyni
    /// ubíral každý snímek a hráč by zemřel dřív, než by stihl zareagovat.
    /// </remarks>
    public void Hurt(float amount, bool ignoreArmour, Inventory? armour)
    {
        if (amount <= 0f || Dead || _invulnerable > 0f)
        {
            return;
        }

        float taken = ignoreArmour || armour is null
            ? amount
            : amount * (1f - armour.Protection);

        Health = MathF.Max(0f, Health - taken);
        LastDamage = taken;
        _invulnerable = InvulnerableSeconds;
    }

    /// <summary>
    /// Je hráčova hlava pod hladinou?
    /// </summary>
    /// <remarks>
    /// Rozhoduje blok v úrovni očí, ne to, jestli hráč stojí ve vodě. Po pás ve vodě se
    /// dýchá; topí se, až když se voda zavře nad hlavou.
    /// </remarks>
    private static bool HeadUnderwater(VoxelWorld world, PlayerController player)
    {
        Vector3 eye = player.EyePosition;

        return world.ContainsWater(
            (int)MathF.Floor(eye.X),
            (int)MathF.Floor(eye.Y),
            (int)MathF.Floor(eye.Z));
    }
}
