using OpenTK.Mathematics;

namespace Tesseris.Game.Colony;

/// <summary>
/// Kdo stojí komu v cestě. Prostorový index nad polohami kolonistů.
/// </summary>
/// <remarks>
/// <para><b>Proč to není lineární průchod.</b> Předchozí pokus o kolizi (commit 8b9d43b)
/// porovnával každého s každým s poznámkou „kolonistů jsou dnes jednotky". Cíl ze sekce 8 je
/// dvě stě, což je 40 000 porovnání za tik jen na vyhýbání. Tohle je rovnoměrná mřížka
/// s hashem: sousedé se hledají v okolních buňkách, ne v celé kolonii.</para>
///
/// <para><b>Nula alokací v tiku</b> (pravidlo 6.5). Dvě předalokovaná pole a spojový seznam
/// uvnitř nich: <c>_heads</c> drží první prvek každé přihrádky, <c>_next</c> další ve stejné.
/// Přestavba je jedno naplnění <c>_heads</c> hodnotou −1 a průchod kolonisty. Žádný slovník,
/// žádný <c>List</c>.</para>
///
/// <para><b>Buňka mřížky je dva bloky.</b> Poloměr vyhýbání je 0,6 (šířka těla), takže
/// dvoublokové přihrádky znamenají, že sousedé jsou vždycky v téže nebo přilehlé — a stačí
/// projít 3×3×3 okolí.</para>
///
/// <para><b>Pořadí je dané indexem kolonisty, ne pořadím vložení.</b> Vkládá se odzadu, takže
/// spojový seznam vychází vzestupně a průchod je deterministický (pravidlo 6.6).</para>
/// </remarks>
public sealed class ColonistGrid
{
    /// <summary>Hrana přihrádky v blocích.</summary>
    public const int CellSize = 2;

    /// <summary>Kolik přihrádek má tabulka. Mocnina dvou, ať se maskuje místo dělení.</summary>
    private const int Buckets = 1024;

    private const int Empty = -1;

    private readonly int[] _heads = new int[Buckets];
    private int[] _next;
    private Vector3[] _position;
    private int _count;

    public ColonistGrid(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _next = new int[capacity];
        _position = new Vector3[capacity];
        Array.Fill(_heads, Empty);
    }

    /// <summary>Kolik poloh index drží.</summary>
    public int Count => _count;

    /// <summary>Zahodí obsah a připraví se na nové naplnění.</summary>
    public void Clear()
    {
        Array.Fill(_heads, Empty);
        _count = 0;
    }

    /// <summary>Zapíše polohu kolonisty. Index musí být jeho vlastní identifikátor.</summary>
    public void Add(int id, Vector3 position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);

        if (id >= _next.Length)
        {
            int capacity = _next.Length;
            while (capacity <= id)
            {
                capacity *= 2;
            }

            Array.Resize(ref _next, capacity);
            Array.Resize(ref _position, capacity);
        }

        int bucket = BucketOf(position);
        _next[id] = _heads[bucket];
        _heads[bucket] = id;
        _position[id] = position;
        _count++;
    }

    /// <summary>
    /// Spočítá, kam se má kolonista uhnout, aby nestál v jiném.
    /// </summary>
    /// <remarks>
    /// <para><b>Vyhýbání, ne zákaz vstupu.</b> Tohle je celé poučení z commitu 3db486f: zákaz
    /// vstupu do obsazené buňky rozbil uzavřenou herní smyčku, protože u stroje je úzké místo
    /// vždycky a kolonista se nedostal na buňku, ze které se odevzdává. Vrací se proto POSUN,
    /// který se k pohybu přičte — nikdy se nezruší pohyb k cíli. Když se uhnout nedá, projdou
    /// se; rozpixelovaná chvíle je nekonečněkrát lepší než uváznutí.</para>
    ///
    /// <para><b>Síla klesá se vzdáleností</b>, takže dva lidé stojící přesně v sobě se od sebe
    /// odstrčí nejsilněji a kolemjdoucí se skoro nedotknou. Nula na hranici dosahu znamená,
    /// že se nic neděje skokem.</para>
    ///
    /// <para><b>Kdo stojí přesně v sobě, se rozdělí podle indexu.</b> Nulový rozdíl nemá směr
    /// a náhoda by porušila determinismus (pravidlo 6.6); index je stálý a různý.</para>
    /// </remarks>
    /// <param name="id">Kdo se vyhýbá. Sám sebe nepočítá.</param>
    /// <param name="position">Jeho poloha.</param>
    /// <param name="radius">Od jaké vzdálenosti se odstrkává.</param>
    /// <returns>Vodorovný posun. Nulový, když nikdo nepřekáží.</returns>
    public Vector2 Separation(int id, Vector3 position, float radius)
    {
        float pushX = 0f;
        float pushZ = 0f;
        float radiusSquared = radius * radius;

        int baseX = FloorDiv((int)MathF.Floor(position.X), CellSize);
        int baseY = FloorDiv((int)MathF.Floor(position.Y), CellSize);
        int baseZ = FloorDiv((int)MathF.Floor(position.Z), CellSize);

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int bucket = Hash(baseX + dx, baseY + dy, baseZ + dz);

                    for (int other = _heads[bucket]; other != Empty; other = _next[other])
                    {
                        if (other == id)
                        {
                            continue;
                        }

                        Vector3 delta = position - _position[other];

                        // SVISLE SE NEVYHÝBÁ. Kdo stojí o patro výš, nepřekáží — a kdyby se
                        // počítal, odstrkávali by se lidé na schodech od sebe do prázdna.
                        if (MathF.Abs(delta.Y) >= 1f)
                        {
                            continue;
                        }

                        float distanceSquared = (delta.X * delta.X) + (delta.Z * delta.Z);
                        if (distanceSquared >= radiusSquared)
                        {
                            continue;
                        }

                        if (distanceSquared < 1e-6f)
                        {
                            // Přesně v sobě: rozejdou se podle indexu, ať to je reprodukovatelné.
                            float angle = id * 2.399963f;
                            pushX += MathF.Cos(angle) * radius;
                            pushZ += MathF.Sin(angle) * radius;
                            continue;
                        }

                        float distance = MathF.Sqrt(distanceSquared);
                        float strength = (radius - distance) / radius;

                        pushX += delta.X / distance * strength;
                        pushZ += delta.Z / distance * strength;
                    }
                }
            }
        }

        return new Vector2(pushX, pushZ);
    }

    private static int BucketOf(Vector3 position) => Hash(
        FloorDiv((int)MathF.Floor(position.X), CellSize),
        FloorDiv((int)MathF.Floor(position.Y), CellSize),
        FloorDiv((int)MathF.Floor(position.Z), CellSize));

    /// <summary>Dělení dolů. Obyčejné dělení u záporných souřadnic zaokrouhluje k nule.</summary>
    private static int FloorDiv(int value, int size) =>
        value >= 0 ? value / size : ((value + 1) / size) - 1;

    private static int Hash(int x, int y, int z)
    {
        // Tři velká lichá čísla, jak je zvykem u prostorových hashů. Maska místo modula,
        // protože počet přihrádek je mocnina dvou.
        unchecked
        {
            int hash = (x * 73856093) ^ (y * 19349663) ^ (z * 83492791);
            return hash & (Buckets - 1);
        }
    }
}
