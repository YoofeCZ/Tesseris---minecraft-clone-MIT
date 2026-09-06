using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy kamery. Všechny běží bez GL kontextu — kamera schválně nečte vstup sama,
/// takže je pohybová matematika čistě funkční.
/// </summary>
public sealed class CameraTests
{
    private const float Tolerance = 1e-4f;

    [Fact]
    public void Vychozi_orientace_miri_po_zaporne_ose_Z()
    {
        var camera = new Camera(Vector3.Zero);

        Assert.Equal(0f, camera.Forward.X, Tolerance);
        Assert.Equal(0f, camera.Forward.Y, Tolerance);
        Assert.Equal(-1f, camera.Forward.Z, Tolerance);
    }

    [Fact]
    public void Yaw_se_obtoci_do_rozsahu_0_az_360()
    {
        var camera = new Camera(Vector3.Zero);

        // Konstruktor nastavuje -90, což se má hned obtočit na 270.
        Assert.Equal(270f, camera.YawDegrees, Tolerance);

        camera.YawDegrees = 450f;
        Assert.Equal(90f, camera.YawDegrees, Tolerance);

        camera.YawDegrees = -30f;
        Assert.Equal(330f, camera.YawDegrees, Tolerance);
    }

    [Theory]
    [InlineData(200f, 89.9f)]
    [InlineData(-200f, -89.9f)]
    [InlineData(45f, 45f)]
    public void Pitch_se_orizne_tesne_pod_svislici(float requested, float expected)
    {
        var camera = new Camera(Vector3.Zero) { PitchDegrees = requested };

        Assert.Equal(expected, camera.PitchDegrees, Tolerance);
    }

    [Theory]
    [InlineData(-89.9f)]
    [InlineData(-45f)]
    [InlineData(0f)]
    [InlineData(45f)]
    [InlineData(89.9f)]
    public void Vektor_doprava_zustava_vodorovny_pri_kazdem_pitchi(float pitch)
    {
        var camera = new Camera(Vector3.Zero) { PitchDegrees = pitch };

        // Kdyby Right nebyl vodorovný, pohled by se při pohybu do strany nakláněl.
        Assert.Equal(0f, camera.Right.Y, Tolerance);
        Assert.Equal(1f, camera.Right.Length, Tolerance);
    }

    [Theory]
    [InlineData(-89.9f)]
    [InlineData(-70f)]
    [InlineData(0f)]
    [InlineData(70f)]
    [InlineData(89.9f)]
    public void Vodorovny_smer_chuze_ma_stejnou_delku_pri_kazdem_pitchi(float pitch)
    {
        var camera = new Camera(Vector3.Zero) { PitchDegrees = pitch };

        Assert.Equal(0f, camera.HorizontalForward.Y, Tolerance);
        Assert.Equal(1f, camera.HorizontalForward.Length, Tolerance);
        Assert.Equal(-1f, camera.HorizontalForward.Z, Tolerance);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(30f, 40f)]
    [InlineData(200f, -70f)]
    public void Bazove_vektory_jsou_ortonormalni(float yaw, float pitch)
    {
        var camera = new Camera(Vector3.Zero) { YawDegrees = yaw, PitchDegrees = pitch };

        Assert.Equal(1f, camera.Forward.Length, Tolerance);
        Assert.Equal(1f, camera.Right.Length, Tolerance);
        Assert.Equal(1f, camera.Up.Length, Tolerance);

        Assert.Equal(0f, Vector3.Dot(camera.Forward, camera.Right), Tolerance);
        Assert.Equal(0f, Vector3.Dot(camera.Forward, camera.Up), Tolerance);
        Assert.Equal(0f, Vector3.Dot(camera.Right, camera.Up), Tolerance);
    }

    [Fact]
    public void Pohyb_do_rohu_neni_rychlejsi_nez_rovne()
    {
        var straight = new Camera(Vector3.Zero);
        var diagonal = new Camera(Vector3.Zero);

        straight.ApplyMovement(new Vector3(0f, 0f, 1f), 10f, 1f);
        diagonal.ApplyMovement(new Vector3(1f, 0f, 1f), 10f, 1f);

        Assert.Equal(straight.Position.Length, diagonal.Position.Length, Tolerance);
    }

    [Fact]
    public void Svisly_pohyb_jde_po_svetove_ose_i_pri_naklonenem_pohledu()
    {
        var camera = new Camera(Vector3.Zero) { PitchDegrees = 60f };

        camera.ApplyMovement(new Vector3(0f, 1f, 0f), 5f, 1f);

        // Kdyby se svislý pohyb odvozoval od Up kamery, uletěl by i do stran.
        Assert.Equal(0f, camera.Position.X, Tolerance);
        Assert.Equal(5f, camera.Position.Y, Tolerance);
        Assert.Equal(0f, camera.Position.Z, Tolerance);
    }

    [Fact]
    public void Nulovy_vstup_kamerou_nepohne()
    {
        var camera = new Camera(new Vector3(3f, 4f, 5f));

        camera.ApplyMovement(Vector3.Zero, 100f, 1f);

        Assert.Equal(new Vector3(3f, 4f, 5f), camera.Position);
    }

    [Fact]
    public void ApplyLook_scita_prirustky_a_drzi_meze()
    {
        var camera = new Camera(Vector3.Zero);

        camera.ApplyLook(30f, 20f);
        camera.ApplyLook(30f, 20f);

        Assert.Equal(330f, camera.YawDegrees, Tolerance);
        Assert.Equal(40f, camera.PitchDegrees, Tolerance);

        camera.ApplyLook(0f, 500f);
        Assert.Equal(89.9f, camera.PitchDegrees, Tolerance);
    }
}
