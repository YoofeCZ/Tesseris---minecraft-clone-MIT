using System.Runtime.InteropServices;

namespace Tesseris.Engine.Audio;

/// <summary>
/// Tenká vrstva nad céčkovým rozhraním knihovny SoLoud (<c>soloud_c</c>).
///
/// Je to doslovný přepis hlaviček, nic víc — žádné obalování, žádná logika. Smysl má i to,
/// co tu <b>není</b>: knihovna se nebere z NuGetu, protože zadání chce vlastní vrstvu, a tady
/// je vidět přesně, které funkce se používají.
///
/// <para>Ukazatele se drží jako <see cref="IntPtr"/>, protože SoLoud pracuje s neprůhlednými
/// objekty a jejich vnitřek nás nezajímá.</para>
///
/// <para><b>Chybějící knihovna.</b> Voláním kterékoli metody vznikne <c>DllNotFoundException</c>,
/// pokud <c>soloud_x64.dll</c> není vedle binárky. Odchytává se v <see cref="AudioEngine"/>,
/// aby se hra bez zvuku dala pořád hrát.</para>
/// </summary>
internal static class SoLoudNative
{
    /// <summary>Název nativní knihovny bez přípony. Hledá se vedle spustitelného souboru.</summary>
    public const string Library = "soloud_x64";

    /// <summary>Útlum se vzdáleností podle převrácené hodnoty. Odpovídá chování zvuku v prostoru.</summary>
    public const uint InverseDistanceAttenuation = 1;

    /// <summary>Automatická volba výstupu a vzorkovací frekvence.</summary>
    public const uint AutoBackend = 0;
    public const uint AutoSampleRate = 0;
    public const uint AutoBufferSize = 0;
    public const uint AutoChannels = 2;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr Soloud_create();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Soloud_destroy(IntPtr soloud);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Soloud_initEx(
        IntPtr soloud, uint flags, uint backend, uint sampleRate, uint bufferSize, uint channels);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Soloud_deinit(IntPtr soloud);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr Soloud_getErrorString(IntPtr soloud, int errorCode);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Soloud_setGlobalVolume(IntPtr soloud, float volume);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Soloud_stopAll(IntPtr soloud);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Soloud_stop(IntPtr soloud, uint voiceHandle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Soloud_stopAudioSource(IntPtr soloud, IntPtr source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Soloud_setRelativePlaySpeed(IntPtr soloud, uint voiceHandle, float speed);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Soloud_setLooping(IntPtr soloud, uint voiceHandle, int looping);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Soloud_setPause(IntPtr soloud, uint voiceHandle, int pause);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Soloud_getActiveVoiceCount(IntPtr soloud);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Soloud_playEx(
        IntPtr soloud, IntPtr source, float volume, float pan, int paused, uint bus);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Soloud_play3dEx(
        IntPtr soloud, IntPtr source,
        float posX, float posY, float posZ,
        float velX, float velY, float velZ,
        float volume, int paused, uint bus);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Soloud_set3dListenerParametersEx(
        IntPtr soloud,
        float posX, float posY, float posZ,
        float atX, float atY, float atZ,
        float upX, float upY, float upZ,
        float velocityX, float velocityY, float velocityZ);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Soloud_update3dAudio(IntPtr soloud);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr Wav_create();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Wav_destroy(IntPtr wav);

    /// <summary>
    /// Načte hotové vzorky z paměti. Používá se místo souboru, protože všechny zvuky
    /// se generují v kódu — binární assety jsou zakázané.
    /// </summary>
    /// <param name="copy">Nenulové znamená, že si SoLoud data zkopíruje a nedrží náš buffer.</param>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Wav_loadRawWaveEx(
        IntPtr wav, float[] samples, uint length, float sampleRate, uint channels, int copy, int takeOwnership);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int Wav_load(IntPtr wav, [MarshalAs(UnmanagedType.LPStr)] string path);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Wav_setLooping(IntPtr wav, int looping);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Wav_setVolume(IntPtr wav, float volume);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Wav_set3dMinMaxDistance(IntPtr wav, float minDistance, float maxDistance);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Wav_set3dAttenuation(IntPtr wav, uint model, float rolloffFactor);
}
