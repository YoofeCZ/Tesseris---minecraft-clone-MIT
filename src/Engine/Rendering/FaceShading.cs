namespace Tesseris.Engine.Rendering;

/// <summary>
/// Jas stěny podle toho, kam míří, a stínění rohů. Obojí převzaté z Luanti.
/// </summary>
/// <remarks>
/// <para><b>Pevná tabulka šesti čísel, ne výpočet ze směru slunce.</b> Předchozí verze
/// odvozovala jas z <c>0,5 + 0,5·dot(normála, směr ke slunci)</c>, aby měl každý svah
/// jiný odstín. Bylo to špatně ze zásadního důvodu: jas se zapéká do vrcholu při
/// meshování, ale směr slunce se během dne mění. Chunk, který se přemeshoval v poledne,
/// měl proto jiné stínování než jeho soused přemeshovaný za soumraku — a švy mezi nimi
/// byly vidět. Luanti to řeší tím, že jas stěny na slunci vůbec nezávisí; putující
/// slunce má na starosti stínová mapa, která se počítá každý snímek.</para>
///
/// <para>Reliéf zajišťuje samotný poměr vršku ke stěnám: vodorovná plocha je 2,24×
/// světlejší než spodní strana převisu a 1,49× světlejší než stěna podél X. To je na
/// čitelnost tvaru dost — přesně tak vypadá Luanti bez zapnutých stínů.</para>
///
/// <para>Vrchol tak nepotřebuje normálu jako další atribut: jas se zamíchá do stínění
/// rohů už při meshování a do shaderu dorazí jediné číslo.</para>
/// </remarks>
public static class FaceShading
{
    /// <summary>
    /// Nejtmavší hodnota, na kterou smí stínění rohů srazit jas.
    /// </summary>
    /// <remarks>
    /// Odpovídá Luanti <c>ambient_occlusion_gamma = 1,8</c> na nejzastíněnějším rohu,
    /// tedy <c>0,25^(1/1,8)</c>. Bylo tu 0,32 z vlastní úvahy; Luanti kout tolik
    /// nezavírá, protože počítá s tím, že do něj světlo doleze odrazem.
    /// </remarks>
    public static readonly float AmbientFloor = LuantiLight.AmbientOcclusion(0);

    /// <summary>
    /// Jas stěny kolmé na zadanou osu. Osa 0 je X, 1 je Y, 2 je Z.
    /// </summary>
    public static float ForAxis(int axis, bool positive) => LuantiLight.FaceShade(axis, positive);

    /// <summary>
    /// Spojí stínění rohu s jasem stěny do jednoho čísla, které jde rovnou do vrcholu.
    /// </summary>
    /// <param name="cornerOcclusion">Stínění rohu 0 až 3, jak ho spočítal mesher.</param>
    public static float Combine(int cornerOcclusion, float faceBrightness) =>
        LuantiLight.AmbientOcclusion(cornerOcclusion) * faceBrightness;
}
