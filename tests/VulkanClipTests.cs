using OpenTK.Mathematics;
using Tesseris.Engine.Rendering.Vulkan;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Korekce projekce z OpenGL do Vulkanu.
///
/// Testuje se to takhle podrobně proto, že chyba v téhle matici se neprojeví výjimkou ani
/// varováním — jen černou obrazovkou nebo scénou vzhůru nohama, a hledá se pak dlouho.
///
/// <para>
/// <b>Jak se emuluje shader.</b> OpenTK počítá s řádkovými vektory (<c>v * M</c>), GLSL se
/// sloupcovými (<c>M * v</c>). Platí ale <c>v * M == Mᵀ * v</c>, takže <c>v * M</c> v C#
/// dává přesně to, co spočítá shader nad nahranou maticí. Násobení řádkovým vektorem je
/// tedy věrná náhrada shaderu, ne jen podobný výpočet.
/// </para>
///
/// <para>
/// Matice se skládají přímo, ne přes <c>Camera</c>: předmětem testu je korekce, a závislost
/// na tom, kterým směrem zrovna míří výchozí kamera, by test jen zkřehčila.
/// </para>
/// </summary>
public sealed class VulkanClipTests
{
    private const float Near = 0.1f;
    private const float Far = 100f;

    /// <summary>Kamera v počátku hledící podél −Z, tedy tak, jak to čeká <c>LookAt</c>.</summary>
    private static Matrix4 ViewProjection()
    {
        Matrix4 view = Matrix4.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(70f), 16f / 9f, Near, Far);

        return view * projection;
    }

    private static Vector3 ToNdc(Vector3 world, Matrix4 viewProjection)
    {
        Vector4 clip = new Vector4(world, 1f) * viewProjection;
        return new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
    }

    [Fact]
    public void OpenGlKonvenceMaHloubkuOdMinusJedne()
    {
        Matrix4 viewProjection = ViewProjection();

        Assert.Equal(-1f, ToNdc(new Vector3(0f, 0f, -Near), viewProjection).Z, 3);
        Assert.Equal(1f, ToNdc(new Vector3(0f, 0f, -Far), viewProjection).Z, 3);
    }

    [Fact]
    public void VulkanskaKonverzeMapujeHloubkuNaNulaAzJedna()
    {
        Matrix4 vulkan = VulkanClip.ToVulkan(ViewProjection());

        Assert.Equal(0f, ToNdc(new Vector3(0f, 0f, -Near), vulkan).Z, 3);
        Assert.Equal(1f, ToNdc(new Vector3(0f, 0f, -Far), vulkan).Z, 3);
    }

    [Fact]
    public void VulkanskaKonverzeObratiOsuY()
    {
        Matrix4 openGl = ViewProjection();
        Matrix4 vulkan = VulkanClip.ToVulkan(openGl);

        var above = new Vector3(0f, 1f, -10f);

        float openGlY = ToNdc(above, openGl).Y;
        float vulkanY = ToNdc(above, vulkan).Y;

        // V OpenGL je nahore kladne Y, ve Vulkanu zaporne.
        Assert.True(openGlY > 0f, $"OpenGL: bod nad stredem ma mit kladne Y, ale ma {openGlY}.");
        Assert.True(vulkanY < 0f, $"Vulkan: bod nad stredem ma mit zaporne Y, ale ma {vulkanY}.");
        Assert.Equal(openGlY, -vulkanY, 5);
    }

    [Fact]
    public void VulkanskaKonverzeNemeniOsuX()
    {
        Matrix4 openGl = ViewProjection();
        Matrix4 vulkan = VulkanClip.ToVulkan(openGl);

        var right = new Vector3(3f, 0f, -10f);

        Assert.Equal(ToNdc(right, openGl).X, ToNdc(right, vulkan).X, 5);
    }

    /// <summary>
    /// Hloubka musí zůstat monotónní: bližší bod musí mít menší Z. Kdyby se korekcí obrátila,
    /// hloubkový test by nechal kreslit vzdálené plochy přes blízké.
    /// </summary>
    [Fact]
    public void HloubkaRosteSeVzdalenosti()
    {
        Matrix4 vulkan = VulkanClip.ToVulkan(ViewProjection());

        float blizko = ToNdc(new Vector3(0f, 0f, -5f), vulkan).Z;
        float daleko = ToNdc(new Vector3(0f, 0f, -50f), vulkan).Z;

        Assert.True(blizko < daleko, $"Bližší bod má mít menší Z, ale {blizko} >= {daleko}.");
        Assert.InRange(blizko, 0f, 1f);
        Assert.InRange(daleko, 0f, 1f);
    }

    /// <summary>
    /// Celý viditelný objem se musí vejít do platného rozsahu. Kdyby korekce mapovala
    /// hloubku špatně, část scény by se oříznula a chyba by se projevila až v pohybu.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(2f, 1f)]
    [InlineData(-2f, -1f)]
    public void BodyPredKamerouLeziVPlatnemRozsahu(float x, float y)
    {
        Matrix4 vulkan = VulkanClip.ToVulkan(ViewProjection());

        foreach (float depth in (ReadOnlySpan<float>)[0.2f, 1f, 10f, 90f])
        {
            Vector3 ndc = ToNdc(new Vector3(x, y, -depth), vulkan);
            Assert.InRange(ndc.Z, 0f, 1f);
        }
    }

    /// <summary>
    /// Frustum musí dál dostávat matici v konvenci OpenGL. Kdyby někdo v budoucnu poslal
    /// do <c>Frustum.Update</c> vulkanskou matici, culling by zahazoval viditelné chunky —
    /// tenhle test hlídá, že se ty dvě matice opravdu liší a nedají se zaměnit.
    /// </summary>
    [Fact]
    public void VulkanskaMaticeSeLisiOdOpenGlove()
    {
        Matrix4 openGl = ViewProjection();

        Assert.NotEqual(openGl, VulkanClip.ToVulkan(openGl));
    }
}
