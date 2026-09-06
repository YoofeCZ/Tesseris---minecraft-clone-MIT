using OpenTK.Mathematics;
using Tesseris.Game.World.Navigation;

namespace Tesseris.Game.Colony;

/// <summary>
/// Zkrácení cesty: kde je mezi dvěma body přímá průchodnost, jde se napřímo.
/// </summary>
/// <remarks>
/// <para><b>Proč to musí být.</b> A* běží po mřížce a povoluje jen čtyři vodorovné směry
/// (<c>PathFinder.HorizontalDirections</c> — úhlopříčky jsou schválně zakázané, jinak by
/// kolonista prošel rohem mezi dvěma zdmi). Výsledek je proto vždycky lomená čára z pravých
/// úhlů, i když vede přes prázdnou pláň. Kdo po ní půjde doslova, chodí do L, i když má
/// jít šikmo — a přesně tak to vypadalo.</para>
///
/// <para><b>Zkracuje se AŽ po hledání, ne v něm.</b> Povolit A* úhlopříčky by znamenalo
/// řešit rohy uvnitř generátoru sousedů a zdražit každé rozvinutí buňky; jedno hledání stojí
/// naměřených 0,858 ms a do tiku se jich vejde osm. Tohle projde hotovou cestu jednou a je
/// v ní jen viditelnost, žádné rozvinování.</para>
///
/// <para><b>Chodba se drží.</b> Body, mezi kterými přímá viditelnost není, zůstávají —
/// zkrácení nikdy nevyrobí cestu, kterou by tělo neprošlo. Kontroluje se tímtéž predikátem
/// <see cref="ColonistBody.IsFree"/>, jakým se pak opravdu chodí.</para>
///
/// <para><b>Alokace nula</b> (pravidlo 6.5): pracuje se na místě nad polem, které už
/// kolonista má, a vrací se jen nová délka.</para>
/// </remarks>
public static class PathShortening
{
    /// <summary>
    /// Jak hustě se cesta vzorkuje při testu přímé viditelnosti.
    /// </summary>
    /// <remarks>
    /// Čtvrt bloku. Řidší vzorkování prošlo rohem zdi — mezi dvěma vzorky se vejde celá
    /// stěna. Hustší nic nepřidá, protože tělo je 0,6 široké a jeho rohy se testují zvlášť.
    /// </remarks>
    private const float SampleStep = 0.25f;

    /// <summary>
    /// Zkrátí cestu na místě a vrátí novou délku.
    /// </summary>
    /// <param name="graph">Navigace. Odpovídá na to, kde se dá stát.</param>
    /// <param name="path">Cesta od startu k cíli. Přepíše se zkrácenou.</param>
    /// <param name="length">Kolik prvků pole nese.</param>
    /// <returns>Nová délka cesty. Vždycky nejvýš <paramref name="length"/>.</returns>
    public static int Shorten(NavGraph graph, Span<Vector3i> path, int length)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (length <= 2)
        {
            return length;
        }

        // Klasické string pulling: z aktuálního bodu se skáče na NEJVZDÁLENĚJŠÍ viditelný,
        // ne na první neviditelný minus jedna. Rozdíl je v zalomeném terénu: hledat odzadu
        // najde i zkratku, která přeskočí několik pravých úhlů naráz.
        int write = 1;
        int from = 0;

        while (from < length - 1)
        {
            int furthest = from + 1;

            for (int to = length - 1; to > from + 1; to--)
            {
                if (HasDirectPath(graph, path[from], path[to]))
                {
                    furthest = to;
                    break;
                }
            }

            path[write++] = path[furthest];
            from = furthest;
        }

        return write;
    }

    /// <summary>
    /// Projde tělo mezi dvěma buňkami napřímo?
    /// </summary>
    /// <remarks>
    /// <para><b>Výška se interpoluje spolu s polohou.</b> Kdyby se test dělal jen vodorovně,
    /// prošla by zkratka schodištěm i propastí. Takhle je vzorek na úsečce mezi oběma středy
    /// a ptá se přesně na to, kudy pak kolonista opravdu půjde.</para>
    ///
    /// <para><b>Delší svislý rozdíl se nezkracuje vůbec.</b> Přes víc než jedno patro vede
    /// cesta po schodech nebo skokem a přímá čára by procházela skálou; nechat tam původní
    /// body je levnější než to řešit.</para>
    /// </remarks>
    public static bool HasDirectPath(NavGraph graph, Vector3i from, Vector3i to)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (Math.Abs(from.Y - to.Y) > World.Walkability.StepUp)
        {
            return false;
        }

        Vector3 start = ColonistBody.CentreOf(from);
        Vector3 end = ColonistBody.CentreOf(to);
        Vector3 delta = end - start;

        float distance = MathF.Sqrt((delta.X * delta.X) + (delta.Z * delta.Z));
        int steps = (int)MathF.Ceiling(distance / SampleStep);

        if (steps <= 0)
        {
            return true;
        }

        for (int i = 1; i <= steps; i++)
        {
            // Pevné pořadí operací, ať je výsledek deterministický (pravidlo 6.6).
            float t = i / (float)steps;
            var sample = new Vector3(
                start.X + (delta.X * t),
                start.Y + (delta.Y * t),
                start.Z + (delta.Z * t));

            // Výška vzorku leží mezi patry; tělo se ptá na buňku, ve které má nohy, takže
            // se zaokrouhluje dolů — přesně jak to dělá ColonistBody.CellOf.
            sample.Y = MathF.Floor(sample.Y + 0.001f);

            if (!ColonistBody.IsFree(graph, sample))
            {
                return false;
            }
        }

        return true;
    }
}
