using OpenTK.Mathematics;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Převod projekce z konvence OpenGL na konvenci Vulkanu.
///
/// <para>Liší se dvě věci, obě tiše a obě zle:</para>
/// <list type="number">
///   <item>
///     <b>Osa Y.</b> V OpenGL míří v NDC nahoru, ve Vulkanu dolů. Bez opravy je scéna
///     vzhůru nohama.
///   </item>
///   <item>
///     <b>Rozsah hloubky.</b> OpenGL má −1 až 1, Vulkan 0 až 1. Bez opravy skončí bližší
///     polovina scény mimo rozsah a ořízne se, a to, co zbude, má polovinu přesnosti.
///   </item>
/// </list>
///
/// <para>
/// Oprava se dělá <b>až tady</b> a ne v <see cref="Camera"/> schválně:
/// <c>Frustum.Update</c> i testy počítají s maticí v konvenci OpenGL. Kdyby kamera vracela
/// rovnou vulkanskou matici, culling by zahazoval jiné chunky, než které jsou vidět.
/// Kamera tedy zůstává, jak byla, a upravená matice existuje jen po cestu do shaderu.
/// </para>
///
/// <para>
/// <b>Proč se násobí zprava.</b> OpenTK počítá s řádkovými vektory (<c>v * M</c>), zatímco
/// GLSL se sloupcovými (<c>M * v</c>). Nahraná matice se v shaderu chová jako transponovaná,
/// takže požadavek „v shaderu chci C·M" znamená „nahraj M·Cᵀ". Konstanta níž je proto
/// rovnou <b>transponovaná</b> korekce.
/// </para>
/// </summary>
public static class VulkanClip
{
    /// <summary>
    /// Transponovaná korekční matice. Řádky odpovídají tomu, jak OpenTK matice ukládá.
    ///
    /// Nepřevrácená podoba by byla:
    /// <code>
    /// 1   0   0    0
    /// 0  -1   0    0
    /// 0   0  0.5  0.5
    /// 0   0   0    1
    /// </code>
    /// </summary>
    public static readonly Matrix4 Correction = new(
        new Vector4(1f, 0f, 0f, 0f),
        new Vector4(0f, -1f, 0f, 0f),
        new Vector4(0f, 0f, 0.5f, 0f),
        new Vector4(0f, 0f, 0.5f, 1f));

    /// <summary>
    /// Vezme matici v konvenci OpenGL a vrátí tu, která se nahraje do shaderu.
    /// </summary>
    public static Matrix4 ToVulkan(Matrix4 openGl) => openGl * Correction;
}
