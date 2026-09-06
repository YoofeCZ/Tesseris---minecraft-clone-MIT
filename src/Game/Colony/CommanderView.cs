using OpenTK.Mathematics;

namespace Tesseris.Game.Colony;

/// <summary>Co se v daném režimu smí.</summary>
/// <remarks>
/// <b>Tohle není optimalizace ani zjednodušení, je to hook hry.</b>
/// pohled shora je <i>záměrně omezený</i>, není to „totéž, jen shora". Velitel nemá ruce.
/// </remarks>
public readonly record struct ModeAbilities(
    bool CanDigByHand,
    bool CanPlaceBlocks,
    bool CanFight,
    bool CanCarry,
    bool CanMarkArea,
    bool CanPlaceMachines,
    bool CanAssignWork,
    bool CanSeeStockpiles);

/// <summary>
/// Přepínání mezi první osobou a pohledem velitele.
/// </summary>
/// <remarks>
/// <para><b>Přechod není střih.</b> Zadání M0 to říká výslovně: plynulý přechod je součástí
/// identity hry. Střihem by ze dvou režimů byly dvě hry.</para>
///
/// <para><b>Řez patrem.</b> Velitel se dívá shora na jedno patro a může jím projíždět nahoru
/// a dolů. Bez toho by v podzemní kolonii viděl jen strop.</para>
///
/// <para>Třída je čistý stav bez OpenTK okna a bez rendereru, takže se dá testovat —
/// omezení režimů je pravidlo hry, ne detail vykreslování, a musí být ověřitelné.</para>
/// </remarks>
public sealed class CommanderView
{
    /// <summary>Jak dlouho trvá přechod mezi režimy, ve vteřinách.</summary>
    public const float TransitionSeconds = 0.45f;

    /// <summary>Jak vysoko nad řezem se vznáší velitelská kamera.</summary>
    public const float CameraHeight = 28f;

    /// <summary>
    /// Sklon velitelské kamery nad obzorem, ve stupních.
    /// </summary>
    /// <remarks>
    /// <para><b>Kolmo dolů se to dívat nesmí.</b> Ve voxelovém světě není z kolmého pohledu
    /// hloubka — svah, útes a rovina vypadají stejně. Navíc právě pohled přesně shora je ten,
    /// ve kterém svět vypadá nejvíc jako Minecraft, a je to degenerovaný
    /// případ pro LookAt: směr rovnoběžný se svislicí dá nulový vektorový součin.</para>
    ///
    /// <para>55 stupňů je dost šikmo, aby byla vidět výška terénu, a pořád dost zhora, aby
    /// se dalo plánovat. Projekce zůstává PERSPEKTIVNÍ — ortho by vypadalo jako Going
    /// Medieval a zabilo návaznost na první osobu, která je primární režim.</para>
    /// </remarks>
    public const float PitchDegrees = 55f;

    /// <summary>
    /// Čtyři pevné světové strany, po devadesáti stupních.
    /// </summary>
    /// <remarks>
    /// <b>Volné otáčení tady vědomě není.</b> Velitel označuje kliknutím do světa a při volně
    /// otočené kameře přestane být zřejmé, kam vlastně klikne. Čtyři pevné směry drží vztah
    /// „obrazovka → svět" naučitelný: mapa se otočí o pravý úhel a je jasné, co se stalo.
    /// </remarks>
    public static readonly Vector3[] Headings =
    [
        new(0f, 0f, -1f),
        new(1f, 0f, 0f),
        new(0f, 0f, 1f),
        new(-1f, 0f, 0f),
    ];

    private float _blend;

    /// <summary>Je zapnutý režim velitele? Přepíná se okamžitě, obraz dojíždí.</summary>
    public bool Commanding { get; private set; }

    /// <summary>Kterou světovou stranou je kamera otočená, 0 až 3.</summary>
    public int Facing { get; private set; }

    /// <summary>Otočí pohled o devadesát stupňů doleva (klávesa Q).</summary>
    public void RotateLeft() => Facing = (Facing + Headings.Length - 1) % Headings.Length;

    /// <summary>Otočí pohled o devadesát stupňů doprava (klávesa E).</summary>
    public void RotateRight() => Facing = (Facing + 1) % Headings.Length;

    /// <summary>
    /// Kde mezi režimy obraz právě je, 0 = první osoba, 1 = velitel.
    /// </summary>
    public float Blend => _blend;

    /// <summary>Právě se přepíná?</summary>
    public bool InTransition => _blend > 0f && _blend < 1f;

    /// <summary>Které patro velitel vidí.</summary>
    public int SliceY { get; private set; }

    /// <summary>Schopnosti první osoby: ruce ano, přehled ne.</summary>
    public static ModeAbilities FirstPerson => new(
        CanDigByHand: true,
        CanPlaceBlocks: true,
        CanFight: true,
        CanCarry: true,
        CanMarkArea: false,
        CanPlaceMachines: false,
        CanAssignWork: false,
        CanSeeStockpiles: false);

    /// <summary>Schopnosti velitele: přehled ano, ruce ne.</summary>
    public static ModeAbilities Commander => new(
        CanDigByHand: false,
        CanPlaceBlocks: false,
        CanFight: false,
        CanCarry: false,
        CanMarkArea: true,
        CanPlaceMachines: true,
        CanAssignWork: true,
        CanSeeStockpiles: true);

    /// <summary>Co se smí právě teď.</summary>
    /// <remarks>
    /// <b>Během přechodu platí omezení obou režimů.</b> Kdo je půl vteřiny „mezi", nesmí
    /// ani kopat, ani zadávat — jinak by se dalo přepínáním obejít, že velitel nemá ruce.
    /// </remarks>
    public ModeAbilities Abilities => InTransition
        ? new ModeAbilities(false, false, false, false, false, false, false, false)
        : Commanding ? Commander : FirstPerson;

    /// <summary>Přepne režim. Obraz začne dojíždět.</summary>
    public void Toggle() => Commanding = !Commanding;

    /// <summary>Posune obraz k cílovému režimu.</summary>
    public void Update(float deltaSeconds)
    {
        float target = Commanding ? 1f : 0f;
        float step = deltaSeconds / TransitionSeconds;

        _blend = _blend < target
            ? MathF.Min(target, _blend + step)
            : MathF.Max(target, _blend - step);
    }

    /// <summary>Posune řez o patra nahoru nebo dolů.</summary>
    public void MoveSlice(int delta, int minimum, int maximum) =>
        SliceY = Math.Clamp(SliceY + delta, minimum, maximum);

    public void SetSlice(int y, int minimum, int maximum) =>
        SliceY = Math.Clamp(y, minimum, maximum);

    /// <summary>
    /// Kde má být kamera. Interpoluje mezi očima hráče a pohledem shora na řez.
    /// </summary>
    /// <remarks>
    /// <b>Kamera se odtáhne dozadu, ne jen zvedne.</b> Dívá se na TOTÉŽ místo jako dřív —
    /// bod (oči.X, řez, oči.Z) — jen zešikma, takže musí stát o kus proti směru pohledu.
    /// Vzdálenost je odvozená, ne odhadnutá: aby zůstala výška nad řezem rovná
    /// <see cref="CameraHeight"/>, musí být vzdálenost <c>CameraHeight / sin(sklon)</c>.
    /// </remarks>
    public Vector3 CameraPosition(Vector3 eyePosition)
    {
        var focus = new Vector3(eyePosition.X, SliceY, eyePosition.Z);
        Vector3 look = CommanderForward();

        // sin(sklon) je kladné, takže se tady nedělí nulou ani při krajních hodnotách.
        float distance = CameraHeight / MathF.Sin(MathHelper.DegreesToRadians(PitchDegrees));
        Vector3 overhead = focus - (look * distance);
        return Vector3.Lerp(eyePosition, overhead, Smooth(_blend));
    }

    /// <summary>
    /// Kam se má kamera dívat. V plném velitelském režimu šikmo dolů podle zvolené strany.
    /// </summary>
    public Vector3 ViewDirection(Vector3 firstPersonForward)
    {
        Vector3 target = CommanderForward();
        Vector3 blended = Vector3.Lerp(Vector3.Normalize(firstPersonForward), target, Smooth(_blend));

        // Lerp mezi dvěma opačnými směry může projít nulou (hráč se dívá přesně proti
        // velitelskému směru). Pojistka zůstává, i když šikmý pohled degenerovaný není —
        // tohle už jednou hru shodilo a nestojí to nic.
        return blended.LengthSquared < 1e-6f ? target : Vector3.Normalize(blended);
    }

    /// <summary>Jednotkový směr velitelské kamery: zvolená světová strana sklopená o pitch.</summary>
    public Vector3 CommanderForward()
    {
        float pitch = MathHelper.DegreesToRadians(PitchDegrees);
        Vector3 heading = Headings[Facing];

        return Vector3.Normalize(new Vector3(
            heading.X * MathF.Cos(pitch),
            -MathF.Sin(pitch),
            heading.Z * MathF.Cos(pitch)));
    }

    /// <summary>Vyhlazení přechodu. Lineární blend je na začátku i konci vidět jako trhnutí.</summary>
    private static float Smooth(float t) => t * t * (3f - (2f * t));
}
