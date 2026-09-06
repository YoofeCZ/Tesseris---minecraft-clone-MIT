namespace Tesseris.Engine.Rendering;

/// <summary>
/// Počítadlo draw callů za frame.
///
/// Není tu žádný "policejní" mechanismus, který by hlídal, že se nikdo nekreslí mimo počítadlo.
/// Zvažovaná varianta (statická obalová třída + test, který prohledává zdrojáky regexem) byla
/// ověřena a je děravá: alias nebo <c>using static</c> ji obejdou a test přesto projde.
/// Místo falešné jistoty platí prosté pravidlo — kdo kreslí, zvýší počítadlo; ve F0 jsou taková
/// místa dvě a obě jsou v tomhle projektu.
/// </summary>
public sealed class RenderStats
{
    /// <summary>Počet draw callů v právě dokončeném framu.</summary>
    public int DrawCalls { get; private set; }

    private int _current;

    /// <summary>Volá se na začátku framu. Zpřístupní počet z minulého framu a začne nový.</summary>
    public void BeginFrame()
    {
        DrawCalls = _current;
        _current = 0;
    }

    /// <summary>Zaznamená jeden draw call. Volá se hned vedle příslušného vkCmdDraw volání.</summary>
    public void CountDrawCall() => _current++;
}
