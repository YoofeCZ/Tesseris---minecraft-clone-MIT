namespace Tesseris.Game.UI;

/// <summary>
/// Ruční posun vzdušné mlhy nad automatickou vzdáleností odvozenou z hotového dohledu.
/// </summary>
/// <remarks>
/// Automatický základ se při streamování i změně dohledu pohybuje. Menu proto neukládá
/// absolutní čísla, ale odchylky od základu: hráčovo nastavení zůstane účinné a mlha se
/// současně dál přizpůsobuje skutečně hotové geometrii.
/// </remarks>
internal sealed class FogTuning
{
    public const float MinimumSpan = 64f;
    public const float AbsoluteMaximumDistance = 16384f;

    private float _automaticStart = 288f;
    private float _automaticEnd = 384f;
    private float _maximum = 384f;
    private float _startOffset;
    private float _endOffset;

    public float Start { get; private set; } = 288f;

    public float End { get; private set; } = 384f;

    public void UpdateAutomatic(float start, float end, float maximum)
    {
        _maximum = float.IsFinite(maximum)
            ? Math.Clamp(maximum, MinimumSpan, AbsoluteMaximumDistance)
            : MinimumSpan;
        _automaticStart = float.IsFinite(start)
            ? Math.Clamp(start, 0f, _maximum - MinimumSpan)
            : 0f;
        _automaticEnd = float.IsFinite(end)
            ? Math.Clamp(end, _automaticStart + MinimumSpan, _maximum)
            : _automaticStart + MinimumSpan;
        Resolve();
    }

    public void SetStart(float value)
    {
        float requested = float.IsFinite(value)
            ? Math.Clamp(value, 0f, _maximum - MinimumSpan)
            : Start;
        _startOffset = requested - _automaticStart;
        Resolve();
    }

    public void SetEnd(float value)
    {
        float requested = float.IsFinite(value)
            ? Math.Clamp(value, MinimumSpan, _maximum)
            : End;
        _endOffset = requested - _automaticEnd;
        Resolve();
    }

    private void Resolve()
    {
        Start = Math.Clamp(
            _automaticStart + _startOffset,
            0f,
            _maximum - MinimumSpan);
        End = Math.Clamp(
            _automaticEnd + _endOffset,
            Start + MinimumSpan,
            _maximum);
    }
}
