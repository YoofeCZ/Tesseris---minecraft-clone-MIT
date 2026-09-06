using OpenTK.Mathematics;

namespace Tesseris.Engine.Rendering;

/// <summary>
/// Free-fly kamera. Drží pózu (pozice + yaw/pitch) a umí z ní spočítat view a projection matici.
///
/// Souřadnice: pravotočivé, +Y nahoru. Yaw se měří kolem osy Y, pitch kolem lokální osy X.
/// Yaw = -90° znamená pohled ve směru -Z (výchozí orientace OpenGL).
///
/// Třída záměrně nečte vstup sama — okno vstup vytáhne a zavolá <see cref="ApplyLook"/>
/// a <see cref="ApplyMovement"/>. Díky tomu jde celá pohybová matematika testovat bez GL kontextu
/// i bez okna.
/// </summary>
public sealed class Camera
{
    /// <summary>O kolik stupňů pod svislicí se pitch zastaví. Přesně ±90° by degenerovalo view matici.</summary>
    private const float PitchLimitDegrees = 89.9f;

    private float _yawDegrees;
    private float _pitchDegrees;

    public Camera(Vector3 position)
    {
        Position = position;

        // Přes property, ne přes pole: -90° se obtočí na 270° a getter je od začátku
        // konzistentní s tím, co vrátí po prvním ApplyLook.
        YawDegrees = -90f;
    }

    public Vector3 Position { get; set; }

    /// <summary>Otočení kolem osy Y ve stupních. Drží se v rozsahu [0, 360) kvůli přesnosti float při dlouhém běhu.</summary>
    public float YawDegrees
    {
        get => _yawDegrees;
        set => _yawDegrees = WrapDegrees(value);
    }

    /// <summary>Náklon ve stupních, tvrdě omezený na ±<see cref="PitchLimitDegrees"/>.</summary>
    public float PitchDegrees
    {
        get => _pitchDegrees;
        set => _pitchDegrees = Math.Clamp(value, -PitchLimitDegrees, PitchLimitDegrees);
    }

    public float FieldOfViewDegrees { get; set; } = 70f;

    /// <summary>
    /// Blízká rovina. Zvednuta z 0,05 na 0,12: přesnost hloubky závisí hlavně na poměru
    /// vzdálené a blízké roviny, takže posunout blízkou je levnější než ubrat z dohledu.
    /// </summary>
    public float NearPlane { get; set; } = 0.12f;

    /// <summary>
    /// Vzdálená rovina.
    ///
    /// <para><b>Musí pokrýt i LOD.</b> Byla 1000 bloků, zatímco vzdálený terén sahá do
    /// 12 288 — všechno za tisícovkou se ořízlo. Projevovalo se to tak, že hora, na kterou
    /// se člověk díval rovně, nebyla vidět, ale <b>po otočení kamery se objevila</b>:
    /// vzdálená rovina totiž ořezává podle vzdálenosti <b>po ose pohledu</b>, ne radiálně,
    /// takže otočením o 45 stupňů spadne vzdálenost hory po ose na sedmdesát procent
    /// a hora se vejde dovnitř. Zadavatel to popsal přesně a dovedlo to k příčině.</para>
    /// </summary>
    /// <remarks>
    /// Sníženo z 16384 na 8192: vzdálený terén sahá do 6144 bloků, takže dvojnásobek
    /// byl zbytečná rezerva — a přesnost hloubky závisí na poměru vzdálené a blízké
    /// roviny, takže se tím zadarmo získá jeden bit.
    /// </remarks>
    public float FarPlane { get; set; } = 8192f;

    public float AspectRatio { get; set; } = 16f / 9f;

    /// <summary>Jednotkový vektor směru pohledu.</summary>
    public Vector3 Forward
    {
        get
        {
            float yaw = MathHelper.DegreesToRadians(_yawDegrees);
            float pitch = MathHelper.DegreesToRadians(_pitchDegrees);
            float cosPitch = MathF.Cos(pitch);

            return Vector3.Normalize(new Vector3(
                cosPitch * MathF.Cos(yaw),
                MathF.Sin(pitch),
                cosPitch * MathF.Sin(yaw)));
        }
    }

    /// <summary>
    /// Jednotkový směr pohledu promítnutý na zem. Jeho délka nezávisí na pitchi.
    /// </summary>
    /// <remarks>
    /// Pouhé vynulování složky Y nestačí: vodorovná část <see cref="Forward"/> má délku
    /// <c>cos(pitch)</c>, takže bez nové normalizace hráč při pohledu dolů zpomaluje a u
    /// svislého pohledu se téměř zastaví.
    /// </remarks>
    public Vector3 HorizontalForward
    {
        get
        {
            Vector3 forward = Forward with { Y = 0f };
            float lengthSquared = forward.LengthSquared;

            return lengthSquared > 1e-8f
                ? forward / MathF.Sqrt(lengthSquared)
                : Vector3.Zero;
        }
    }

    /// <summary>Jednotkový vektor doprava, vždy vodorovný (nenaklání se s pitchem).</summary>
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    /// <summary>Jednotkový vektor nahoru, kolmý na <see cref="Forward"/> i <see cref="Right"/>.</summary>
    public Vector3 Up => Vector3.Normalize(Vector3.Cross(Right, Forward));

    /// <summary>
    /// Kam se kamera dívá, když to má být jinam, než kam míří hráč. <c>null</c> = podél
    /// <see cref="Forward"/>.
    /// </summary>
    /// <remarks>
    /// Kvůli pohledu ze třetí osoby zepředu: kamera stojí před hráčem a dívá se na něj, ale
    /// zaměřování musí dál mířit tam, kam se dívá hráč. Kdyby se otočil <see cref="YawDegrees"/>,
    /// otočilo by se i míření a hráč by kopal za sebe.
    /// </remarks>
    public Vector3? ViewDirection { get; set; }

    /// <summary>Směr, kterým se opravdu kreslí. Obloha i mraky musí jít podle něj, ne podle míření.</summary>
    public Vector3 ViewForward => ViewDirection ?? Forward;

    /// <summary>
    /// Svislice, ze které se odvozují osy pohledu.
    /// </summary>
    /// <remarks>
    /// <para><b>U kolmého pohledu se musí vyměnit.</b> Osy se počítají vektorovým součinem
    /// směru pohledu se svislicí; když se kamera dívá přesně dolů, jsou oba vektory
    /// rovnoběžné, součin vyjde nulový a normalizace z něj udělá NaN. Odtud pak celá
    /// view matice, takže se nekreslí vůbec nic.</para>
    ///
    /// <para><b>Tohle shodilo hru při přepnutí do režimu velitele</b>, který se dívá kolmo
    /// dolů. Nikdy se to neprojevilo, protože selftest do velitele nevstupuje — spadlo to až
    /// v ruce. Práh 0,999 je zhruba 2,6 stupně od kolmice, tedy hluboko v pásmu, kde už je
    /// součin špatně podmíněný, ale ještě dávno předtím, než by výměna byla vidět.</para>
    /// </remarks>
    private Vector3 ReferenceUp =>
        MathF.Abs(Vector3.Normalize(ViewForward).Y) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;

    public Vector3 ViewRight => Vector3.Normalize(Vector3.Cross(ViewForward, ReferenceUp));

    public Vector3 ViewUp => Vector3.Normalize(Vector3.Cross(ViewRight, ViewForward));

    public Matrix4 ViewMatrix => Matrix4.LookAt(Position, Position + ViewForward, ReferenceUp);

    public Matrix4 ProjectionMatrix => Matrix4.CreatePerspectiveFieldOfView(
        MathHelper.DegreesToRadians(FieldOfViewDegrees),
        AspectRatio,
        NearPlane,
        FarPlane);

    /// <summary>
    /// Otočí pohled o zadaný přírůstek ve stupních. Pitch se ořízne, yaw obtočí.
    /// </summary>
    public void ApplyLook(float deltaYawDegrees, float deltaPitchDegrees)
    {
        YawDegrees = _yawDegrees + deltaYawDegrees;
        PitchDegrees = _pitchDegrees + deltaPitchDegrees;
    }

    /// <summary>
    /// Posune kameru podle vstupu v jejím lokálním prostoru.
    /// </summary>
    /// <param name="localMove">
    /// X = doprava, Y = nahoru (ve světě, ne po ose pohledu), Z = dopředu.
    /// Očekává se rozsah [-1, 1] na složku; delší vektor se normalizuje, aby chůze do rohu
    /// nebyla rychlejší než rovně.
    /// </param>
    /// <param name="unitsPerSecond">Rychlost pohybu.</param>
    /// <param name="deltaSeconds">Délka framu v sekundách.</param>
    public void ApplyMovement(Vector3 localMove, float unitsPerSecond, float deltaSeconds)
    {
        float lengthSquared = localMove.LengthSquared;
        if (lengthSquared <= 0f)
        {
            return;
        }

        if (lengthSquared > 1f)
        {
            localMove /= MathF.Sqrt(lengthSquared);
        }

        // Svislý pohyb jde po světové ose Y, aby se let nahoru nezatáčel podle pitchu.
        Vector3 delta = (Right * localMove.X)
                      + (Vector3.UnitY * localMove.Y)
                      + (Forward * localMove.Z);

        Position += delta * (unitsPerSecond * deltaSeconds);
    }

    private static float WrapDegrees(float degrees)
    {
        degrees %= 360f;
        return degrees < 0f ? degrees + 360f : degrees;
    }
}
