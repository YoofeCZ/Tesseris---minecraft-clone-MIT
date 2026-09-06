using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Rekonstrukce směru pohledu v shaderu oblohy.
///
/// <para>Obloha nemá geometrii — kreslí se jako jeden trojúhelník přes obrazovku a směr
/// paprsku si dopočítá sama. Tady je celý rozdíl proti terénu: terén se kreslí maticí
/// přímou, obloha si musí směr odvodit. Když je to odvození nepřesné, terén stojí
/// a slunce poskakuje — a přesně takhle se to projevilo.</para>
///
/// <para><b>Měří se poloha slunce na obrazovce, ne úhel mezi paprsky.</b> Úhel dvou skoro
/// rovnoběžných vektorů přes <c>acos</c> je numericky mizerný: derivace <c>acos</c> u jedničky
/// jde do nekonečna, takže chyba dotu 6e-8 vyjde jako desetina stupně. První verze tohohle
/// testu na to naletěla a měřila hlavně sama sebe. Poloha na obrazovce se počítá bez
/// jediného odčítání blízkých čísel a je to zároveň přesně to, co je vidět.</para>
///
/// <para>Osy se počítají ve <b>float</b>, protože v tom počítá i shader; porovnání pak jde
/// v <c>double</c>, aby měřítko bylo přesnější než měřené.</para>
/// </summary>
public sealed class SkyRayTests
{
    private const float PixelsPerScreen = 1280f;
    private const float Yaw0 = 4.87f;
    private const float Step = 0.03f;
    private const float Pitch = 22.9f;

    private static Camera CameraAt(float yawDegrees) =>
        new(new Vector3(-1.74f, 321f, -0.17f))
        {
            YawDegrees = yawDegrees,
            PitchDegrees = Pitch,
            AspectRatio = 1280f / 720f,
        };

    /// <summary>Jak paprsek počítá <c>sky.vert</c> dnes: z os kamery.</summary>
    private static (Vector3 Right, Vector3 Up, Vector3 Forward) AxisBasis(Camera camera)
    {
        float tanHalf = MathF.Tan(MathHelper.DegreesToRadians(camera.FieldOfViewDegrees) * 0.5f);

        return (camera.Right * tanHalf * camera.AspectRatio, camera.Up * tanHalf, camera.Forward);
    }

    /// <summary>
    /// Jak ho počítal dřív: bod na vzdálené rovině převedený zpátky inverzní maticí.
    /// Zůstává tu jako měřítko, o kolik to bylo horší.
    /// </summary>
    private static (Vector3 Right, Vector3 Up, Vector3 Forward) InverseBasis(Camera camera)
    {
        Matrix4 clip = camera.ViewMatrix * camera.ProjectionMatrix;
        Matrix4 inverse = clip.Inverted();

        Vector3 Ray(float x, float y)
        {
            Vector4 far = new Vector4(x, y, 1f, 1f) * inverse;
            return (far.Xyz / far.W) - camera.Position;
        }

        // Osa nahoru se bere jako zaporny smer: shader oblohy stavi paprsek shora dolu.
        Vector3 centre = Ray(0f, 0f);

        return (Ray(1f, 0f) - centre, centre - Ray(0f, 1f), centre);
    }

    /// <summary>
    /// Kde na obrazovce vyjde zadaný světový směr, ve zlomcích šířky obrazu.
    /// </summary>
    /// <remarks>
    /// Řeší se <c>sun = a·right + b·up + c·forward</c> a vrací se <c>a / c</c>. Počítá se
    /// v <c>double</c> nad osami, které vyrobil float — chyba os se tím projeví, chyba
    /// měření ne.
    /// </remarks>
    private static double ScreenX((Vector3 Right, Vector3 Up, Vector3 Forward) basis, Vector3d sun)
    {
        Vector3d r = new(basis.Right.X, basis.Right.Y, basis.Right.Z);
        Vector3d u = new(basis.Up.X, basis.Up.Y, basis.Up.Z);
        Vector3d f = new(basis.Forward.X, basis.Forward.Y, basis.Forward.Z);

        // Cramerovo pravidlo: determinanty 3x3 z dobre podminenych cisel kolem jednicky.
        double det = Vector3d.Dot(r, Vector3d.Cross(u, f));
        double a = Vector3d.Dot(sun, Vector3d.Cross(u, f)) / det;
        double c = Vector3d.Dot(r, Vector3d.Cross(u, sun)) / det;

        return a / c;
    }

    /// <summary>Přesné osy, spočítané celé v <c>double</c>.</summary>
    private static (Vector3 Right, Vector3 Up, Vector3 Forward) ExactBasis(float yawDegrees)
    {
        double yaw = yawDegrees * Math.PI / 180.0;
        double pitch = Pitch * Math.PI / 180.0;
        double cosPitch = Math.Cos(pitch);

        var forward = new Vector3d(cosPitch * Math.Cos(yaw), Math.Sin(pitch), cosPitch * Math.Sin(yaw));
        Vector3d right = Vector3d.Normalize(Vector3d.Cross(forward, Vector3d.UnitY));
        Vector3d up = Vector3d.Normalize(Vector3d.Cross(right, forward));

        double tanHalf = Math.Tan(70.0 * Math.PI / 180.0 * 0.5);
        double aspect = 1280.0 / 720.0;

        Vector3 Down(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

        return (Down(right * tanHalf * aspect), Down(up * tanHalf), Down(forward));
    }

    /// <summary>
    /// O kolik pixelů se poloha slunce mezi dvěma snímky odchýlí od hladkého pohybu.
    /// </summary>
    private static double WorstWobble(Func<Camera, (Vector3, Vector3, Vector3)> basis)
    {
        // Slunce v poledne, ale konkretni smer je jedno — jde o to, ze je pevny.
        var sun = Vector3d.Normalize(new Vector3d(0.9048, 0.3888, 0.1737));

        var errors = new List<double>();

        for (int i = 0; i < 200; i++)
        {
            float yaw = Yaw0 + (i * Step);

            double actual = ScreenX(basis(CameraAt(yaw)), sun);
            double exact = ScreenX(ExactBasis(yaw), sun);

            errors.Add((actual - exact) * PixelsPerScreen);
        }

        double worst = 0.0;
        for (int i = 1; i < errors.Count; i++)
        {
            worst = Math.Max(worst, Math.Abs(errors[i] - errors[i - 1]));
        }

        return worst;
    }

    /// <summary>
    /// Při rovnoměrném otáčení se musí slunce posouvat rovnoměrně. Krok při 0,03 stupně
    /// je zhruba 0,38 px, takže kolísání nad desetinu pixelu je vidět.
    /// </summary>
    [Fact]
    public void Slunce_se_pri_otaceni_posouva_rovnomerne()
    {
        double worst = WorstWobble(c => AxisBasis(c));

        Assert.True(worst < 0.1, $"Poloha slunce mezi snímky uskočí o {worst:F3} px.");
    }

    /// <summary>
    /// Osy jsou přesnější než inverzní matice — ale ne o tolik, aby to byla ta příčina.
    /// </summary>
    /// <remarks>
    /// <para><b>Zapsáno schválně i s tím, co se nepotvrdilo.</b> Inverzní matice vypadala
    /// jako viník poskakujícího slunce a první verze tohohle testu to i „dokazovala" —
    /// jenže měřila úhel mezi skoro rovnoběžnými vektory přes <c>acos</c>, což je numericky
    /// mizerné, a naměřila hlavně sama sebe (0,41 px). Poctivé měření polohy na obrazovce
    /// dalo <b>0,07 px</b>, což na viditelné uskakování zdaleka nestačí.</para>
    ///
    /// <para>Skutečná příčina byla jinde: saturovaný okraj slunečního kotouče, viz komentář
    /// u <c>fwidth</c> ve <c>sky.frag</c>. Osy se přesto nechávají — jsou přesnější, menší
    /// a ušetří inverzi matice na každý snímek.</para>
    /// </remarks>
    [Fact]
    public void Osy_jsou_presnejsi_nez_inverzni_matice()
    {
        double fromInverse = WorstWobble(c => InverseBasis(c));
        double fromAxes = WorstWobble(c => AxisBasis(c));

        Assert.True(
            fromAxes < fromInverse,
            $"Osy měly být klidnější než inverze: {fromAxes:F4} px proti {fromInverse:F4} px.");

        // A hlavně: inverze sama o sobě nebyla dost špatná na to, aby šla vidět. Kdyby
        // tohle jednou spadlo, znamená to, že se poměr rovin nebo přesnost změnily.
        Assert.True(fromInverse < 0.15, $"Inverze uskakuje o {fromInverse:F4} px, dřív to bylo 0,07.");
    }
}
