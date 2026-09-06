using OpenTK.Mathematics;

namespace Tesseris.Engine.MathLib;

/// <summary>
/// Šest ořezových rovin pohledového jehlanu, vytažených z view-projection matice
/// (Gribb–Hartmann). Normály míří dovnitř, takže bod je uvnitř, když má u všech rovin
/// nezápornou vzdálenost.
///
/// Je to třída, ne struktura, aby se pole rovin alokovalo jednou a
/// <see cref="Update"/> se dal volat každý frame bez alokace.
/// </summary>
public sealed class Frustum
{
    // Roviny ve tvaru (nx, ny, nz, d), tedy nx*x + ny*y + nz*z + d = 0.
    private readonly Vector4[] _planes = new Vector4[6];

    /// <summary>
    /// Přepočítá roviny podle matice.
    /// </summary>
    /// <param name="viewProjection">
    /// Součin view a projection v pořadí, v jakém ho používá zbytek kódu, tedy
    /// <c>view * projection</c>. OpenTK počítá s řádkovými vektory, proto se roviny skládají
    /// ze <b>sloupců</b> matice, ne z řádků.
    /// </param>
    public void Update(Matrix4 viewProjection)
    {
        Vector4 columnX = new(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
        Vector4 columnY = new(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
        Vector4 columnZ = new(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
        Vector4 columnW = new(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

        _planes[0] = columnW + columnX; // vlevo
        _planes[1] = columnW - columnX; // vpravo
        _planes[2] = columnW + columnY; // dole
        _planes[3] = columnW - columnY; // nahoře
        _planes[4] = columnW + columnZ; // blízká
        _planes[5] = columnW - columnZ; // vzdálená

        for (int i = 0; i < _planes.Length; i++)
        {
            Vector4 plane = _planes[i];
            float length = new Vector3(plane.X, plane.Y, plane.Z).Length;
            if (length > 0f)
            {
                _planes[i] = plane / length;
            }
        }
    }

    /// <summary>
    /// Je kvádr aspoň částečně vidět?
    ///
    /// Test je konzervativní: u každé roviny se vezme ten roh kvádru, který leží nejdál
    /// ve směru normály. Když je i ten za rovinou, je celý kvádr venku. Opačně to neplatí —
    /// kvádr těsně za rohem jehlanu může projít, ale to jen znamená, že se zbytečně vykreslí.
    /// </summary>
    public bool Intersects(in Aabb box)
    {
        foreach (Vector4 plane in _planes)
        {
            Vector3 farthest = new(
                plane.X >= 0f ? box.Max.X : box.Min.X,
                plane.Y >= 0f ? box.Max.Y : box.Min.Y,
                plane.Z >= 0f ? box.Max.Z : box.Min.Z);

            if ((plane.X * farthest.X) + (plane.Y * farthest.Y) + (plane.Z * farthest.Z) + plane.W < 0f)
            {
                return false;
            }
        }

        return true;
    }
}
