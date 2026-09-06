using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;
using Tesseris.Game.Colony;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Pohled kolmo dolů. Tohle shodilo hru při prvním zapnutí režimu velitele.
/// </summary>
/// <remarks>
/// <para>Osy pohledu se počítají vektorovým součinem směru se svislicí. Když se kamera dívá
/// přesně dolů, jsou oba vektory rovnoběžné, součin vyjde nulový a normalizace z něj udělá
/// NaN — a odtud celá view matice.</para>
///
/// <para><b>Nikdy se to neprojevilo, protože selftest do režimu velitele nevstupuje.</b>
/// Zelený selftest tuhle cestu vůbec neprošel. Testy proto míří rovnou na tu podmínku, ne
/// na běh hry.</para>
/// </remarks>
public sealed class TopDownCameraTests(ITestOutputHelper output)
{
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4 matrix)
    {
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                if (!float.IsFinite(matrix[row, column]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    [Fact]
    public void Camera_looking_straight_down_still_has_a_usable_basis()
    {
        var camera = new Camera(new Vector3(10f, 40f, 10f))
        {
            AspectRatio = 16f / 9f,
            ViewDirection = new Vector3(0f, -1f, 0f),
        };

        Assert.True(IsFinite(camera.ViewRight), $"ViewRight je {camera.ViewRight}.");
        Assert.True(IsFinite(camera.ViewUp), $"ViewUp je {camera.ViewUp}.");
        Assert.True(IsFinite(camera.ViewMatrix), "View matice není konečná.");
        Assert.True(IsFinite(camera.ViewMatrix * camera.ProjectionMatrix), "View-projection není konečná.");

        // Osy musí zůstat jednotkové a navzájem kolmé, jinak by byl obraz zkosený.
        Assert.Equal(1f, camera.ViewRight.Length, 3);
        Assert.Equal(1f, camera.ViewUp.Length, 3);
        Assert.Equal(0f, Vector3.Dot(camera.ViewRight, camera.ViewUp), 3);

        output.WriteLine($"right {camera.ViewRight}, up {camera.ViewUp}");
    }

    [Fact]
    public void Camera_looking_straight_up_is_fine_too()
    {
        var camera = new Camera(Vector3.Zero)
        {
            AspectRatio = 1f,
            ViewDirection = new Vector3(0f, 1f, 0f),
        };

        Assert.True(IsFinite(camera.ViewMatrix), "Pohled kolmo vzhůru dal NaN matici.");
        Assert.True(IsFinite(camera.ViewRight));
    }

    /// <summary>
    /// Vodorovný pohled se opravou nesmí změnit — jinak by se obraz při pohledu k obzoru
    /// překlopil.
    /// </summary>
    [Fact]
    public void A_level_view_keeps_the_ordinary_basis()
    {
        var camera = new Camera(Vector3.Zero)
        {
            AspectRatio = 1f,
            ViewDirection = new Vector3(0f, 0f, -1f),
        };

        // Vpravo od pohledu na −Z je +X (levotočivá konvence OpenTK Cross).
        Assert.Equal(0f, camera.ViewRight.Y, 3);
        Assert.Equal(1f, MathF.Abs(camera.ViewRight.X), 3);

        // Nahoře je pořád nahoře.
        Assert.True(camera.ViewUp.Y > 0.99f, $"ViewUp je {camera.ViewUp}.");
    }

    /// <summary>
    /// Celý přechod do velitele, krok po kroku. Kdekoli po cestě NaN znamená černou obrazovku.
    /// </summary>
    [Fact]
    public void The_whole_transition_into_commander_mode_stays_finite()
    {
        var view = new CommanderView();
        view.Toggle();

        var camera = new Camera(new Vector3(5f, 70f, -5f))
        {
            AspectRatio = 16f / 9f,
            YawDegrees = 31f,
            PitchDegrees = -12f,
        };

        var eye = new Vector3(5f, 70f, -5f);

        // Půl vteřiny po šedesátinách, tedy s rezervou přes celý přechod.
        for (int tick = 0; tick <= 60; tick++)
        {
            view.Update(1f / 60f);
            camera.Position = view.CameraPosition(eye);
            camera.ViewDirection = view.ViewDirection(camera.Forward);

            Assert.True(
                IsFinite(camera.ViewMatrix * camera.ProjectionMatrix),
                $"View-projection přestala být konečná v kroku {tick}, blend {view.Blend}.");
        }

        // Na konci musí přechod opravdu dojet a pohled musí být ŠIKMÝ, ne kolmý: kolmice
        // nemá ve voxelovém světě hloubku (viz CommanderView.PitchDegrees).
        Assert.Equal(1f, view.Blend, 3);
        Assert.Equal(
            -MathF.Sin(MathHelper.DegreesToRadians(CommanderView.PitchDegrees)),
            camera.ViewForward.Y,
            3);

        output.WriteLine($"blend {view.Blend}, smer {camera.ViewForward}, right {camera.ViewRight}");
    }

    /// <summary>
    /// Zpátky do první osoby taky. Přechod se dá otočit uprostřed.
    /// </summary>
    [Fact]
    public void Toggling_back_and_forth_mid_transition_stays_finite()
    {
        var view = new CommanderView();
        var camera = new Camera(Vector3.Zero) { AspectRatio = 1f };
        var eye = new Vector3(0f, 64f, 0f);

        for (int tick = 0; tick < 200; tick++)
        {
            // Přepínat rychleji, než přechod stihne dojet.
            if (tick % 7 == 0)
            {
                view.Toggle();
            }

            view.Update(1f / 60f);
            camera.Position = view.CameraPosition(eye);
            camera.ViewDirection = view.ViewDirection(camera.Forward);

            Assert.True(
                IsFinite(camera.ViewMatrix * camera.ProjectionMatrix),
                $"NaN v kroku {tick}, blend {view.Blend}.");
        }
    }
}
