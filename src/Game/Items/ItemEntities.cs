using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Game.Player;
using Tesseris.Game.World;

namespace Tesseris.Game.Items;

/// <summary>
/// Předměty ležící ve světě: to, co po sobě těžba nechá na zemi.
/// </summary>
/// <remarks>
/// <para><b>Vytěžený blok nepadá rovnou do inventáře.</b> Je to vědomé rozhodnutí, ne
/// jen ozdoba: hráč vidí, co vytěžil, může to nechat ležet a hlavně je z toho zpětná
/// vazba, že se něco stalo. Rovnou do inventáře je tichý přesun, o kterém se člověk
/// doví jen tím, že se mu někde změnilo číslo.</para>
///
/// <para><b>Slévání se dělá při dopadu, ne každý snímek.</b> Když se rozpadne strom,
/// leží na zemi hromada klád a porovnávat každou s každou v každém snímku je kvadratické.
/// Nová věc se proto pokusí slít do té, která už leží, jednou — a pak už jen padá.</para>
/// </remarks>
public sealed class ItemEntities
{
    /// <summary>Jak blízko musí hráč být, aby předmět sebral.</summary>
    /// <remarks>
    /// Bylo to 1,6 bloku a působilo to, jako by se věci sbíraly samy přes půl místnosti.
    /// Metr je zhruba na dosah ruky, což odpovídá tomu, co hráč čeká.
    /// </remarks>
    private const float PickupRange = 1.0f;

    /// <summary>Odkud se předmět začne přitahovat k hráči.</summary>
    /// <remarks>
    /// <para>Přitahování není jen efekt: bez něj se hráč musí trefit přesně a v důlku pod
    /// nohama leží předmět, ke kterému nejde dojít.</para>
    ///
    /// <para>Dosah je ale <b>malý schválně</b>. Při 3,2 bloku se předměty slétaly z takové
    /// dálky, že to vypadalo jako vysavač; 1,7 stačí na to, aby se posbíralo, co leží
    /// u nohou, a přitom je vidět, že se hráč musí přiblížit.</para>
    /// </remarks>
    private const float MagnetRange = 1.7f;

    /// <summary>Jak dlouho po vypuštění se předmět nesmí sebrat, ve vteřinách.</summary>
    /// <remarks>
    /// Bez prodlevy by se položený a hned rozbitý blok sebral dřív, než by ho hráč uviděl,
    /// a vyhozený předmět by skočil rovnou zpátky do inventáře.
    /// </remarks>
    private const float PickupDelay = 0.4f;

    /// <summary>Jak blízko se dvě hromádky slijí do jedné.</summary>
    private const float MergeRange = 0.9f;

    private const float Gravity = -22f;
    private const float Size = 0.25f;

    private readonly List<ItemEntity> _entities = [];
    private readonly ItemRegistry _items;

    public ItemEntities(ItemRegistry items) =>
        _items = items ?? throw new ArgumentNullException(nameof(items));

    public IReadOnlyList<ItemEntity> All => _entities;

    public int Count => _entities.Count;

    public void Clear() => _entities.Clear();

    /// <summary>
    /// Vyhodí předmět do světa.
    /// </summary>
    /// <param name="impulse">
    /// Počáteční rychlost. Těžba dává drobný náhodný rozstřel, aby hromádka z jednoho
    /// bloku nezůstala v jediném bodě; vyhození z inventáře dává směr pohledu.
    /// </param>
    public void Spawn(ItemStack stack, Vector3 position, Vector3 impulse)
    {
        if (stack.IsEmpty)
        {
            return;
        }

        _entities.Add(new ItemEntity
        {
            Stack = stack,
            Position = position,
            Velocity = impulse,
            Age = 0f,
        });
    }

    /// <summary>Vysype obsah bloku na jeho místo s drobným rozstřelem.</summary>
    public void SpawnFromBlock(ItemStack stack, Vector3i block, uint hash)
    {
        var centre = new Vector3(block.X + 0.5f, block.Y + 0.35f, block.Z + 0.5f);

        // Rozstřel z hashe, ne z generátoru náhody: tentýž blok se rozpadne pokaždé
        // stejně, takže se to dá reprodukovat, když se něco chová divně.
        float vx = (((hash >> 3) & 0xFFu) / 255f - 0.5f) * 1.6f;
        float vz = (((hash >> 11) & 0xFFu) / 255f - 0.5f) * 1.6f;

        Spawn(stack, centre, new Vector3(vx, 2.2f, vz));
    }

    /// <summary>Vysype jeden slot úložného bloku výš a s vlastním rozptylem.</summary>
    public void SpawnFromContainer(ItemStack stack, Vector3i block, uint hash)
    {
        float x = (((hash >> 2) & 0xFFu) / 255f - 0.5f);
        float z = (((hash >> 10) & 0xFFu) / 255f - 0.5f);
        var position = new Vector3(
            block.X + 0.5f + (x * 0.42f),
            block.Y + 0.72f,
            block.Z + 0.5f + (z * 0.42f));
        Spawn(stack, position, new Vector3(x * 2.5f, 2.8f, z * 2.5f));
    }

    /// <summary>
    /// Posune všechny předměty, slije, co se dá, a sebere, co je u hráče.
    /// </summary>
    /// <returns>Kolik kusů se sebralo. Pro zvuk a pro test.</returns>
    public int Update(VoxelWorld world, PlayerController player, Inventory inventory, float deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(inventory);

        Vector3 target = player.Position + new Vector3(0f, 0.9f, 0f);
        int picked = 0;

        for (int i = _entities.Count - 1; i >= 0; i--)
        {
            ItemEntity entity = _entities[i];

            entity.Age += deltaSeconds;

            Step(world, ref entity, target, deltaSeconds);

            if (entity.Age >= PickupDelay && Vector3.Distance(entity.Position, target) <= PickupRange)
            {
                ItemStack left = inventory.Add(entity.Stack);

                picked += entity.Stack.Count - left.Count;

                if (left.IsEmpty)
                {
                    _entities.RemoveAt(i);
                    continue;
                }

                entity.Stack = left;
            }

            _entities[i] = entity;
        }

        return picked;
    }

    private void Step(VoxelWorld world, ref ItemEntity entity, Vector3 target, float deltaSeconds)
    {
        // PŘITAHOVÁNÍ K HRÁČI. Až od chvíle, kdy je předmět seberatelný — jinak by se
        // vyhozená věc rozeběhla zpátky dřív, než opustí ruku.
        float distance = Vector3.Distance(entity.Position, target);

        if (entity.Age >= PickupDelay && distance is > PickupRange and <= MagnetRange)
        {
            Vector3 pull = Vector3.Normalize(target - entity.Position);

            // Blíž = silněji. Rovnoměrné přitahování vypadá jako by předmět letěl na provázku.
            float strength = 14f * (1f - ((distance - PickupRange) / (MagnetRange - PickupRange)));

            entity.Velocity += pull * strength * deltaSeconds;
        }
        else
        {
            entity.Velocity = entity.Velocity with { Y = entity.Velocity.Y + (Gravity * deltaSeconds) };
        }

        Vector3 step = entity.Velocity * deltaSeconds;

        entity.Position = MoveAxis(world, entity.Position, new Vector3(step.X, 0f, 0f), ref entity, axis: 0);
        entity.Position = MoveAxis(world, entity.Position, new Vector3(0f, step.Y, 0f), ref entity, axis: 1);
        entity.Position = MoveAxis(world, entity.Position, new Vector3(0f, 0f, step.Z), ref entity, axis: 2);

        // Tření o zem. Bez něj předmět po dopadu odjede a zastaví se až o stěnu.
        if (entity.OnGround)
        {
            entity.Velocity = new Vector3(entity.Velocity.X * 0.7f, entity.Velocity.Y, entity.Velocity.Z * 0.7f);
        }
    }

    /// <summary>Posun po jedné ose s odrazem od geometrie.</summary>
    /// <remarks>
    /// Po osách zvlášť ze stejného důvodu jako u hráče: šikmý pohyb do rohu by jinak
    /// prošel skrz, protože by se testoval jediný výsledný obal.
    /// </remarks>
    private static Vector3 MoveAxis(
        VoxelWorld world, Vector3 position, Vector3 step, ref ItemEntity entity, int axis)
    {
        Vector3 moved = position + step;

        if (PlayerController.IsFree(world, BoundsAt(moved)))
        {
            if (axis == 1)
            {
                entity.OnGround = false;
            }

            return moved;
        }

        if (axis == 1)
        {
            // Dopad na zem nebo náraz do stropu. Obojí zastaví svislou rychlost; u země
            // se ještě zapamatuje, že předmět leží, aby se uplatnilo tření.
            entity.OnGround = step.Y < 0f;
            entity.Velocity = entity.Velocity with { Y = 0f };
        }
        else
        {
            // Odraz od stěny, ne zastavení. Vytěžený blok vypadlý do stěny by se o ni jinak
            // zasekl a zůstal viset v místě, kde na něj hráč nedosáhne.
            entity.Velocity = axis == 0
                ? entity.Velocity with { X = entity.Velocity.X * -0.3f }
                : entity.Velocity with { Z = entity.Velocity.Z * -0.3f };
        }

        return position;
    }

    private static Aabb BoundsAt(Vector3 position) => new(
        position - new Vector3(Size * 0.5f, 0f, Size * 0.5f),
        position + new Vector3(Size * 0.5f, Size, Size * 0.5f));

    /// <summary>Slije hromádky, které se sešly na jednom místě.</summary>
    /// <remarks>
    /// Volá se po těžbě, ne každý snímek: je to porovnání každého s každým a při stovce
    /// ležících předmětů by to bylo deset tisíc porovnání za snímek pro nic.
    /// </remarks>
    public void Merge()
    {
        for (int i = _entities.Count - 1; i > 0; i--)
        {
            ItemEntity later = _entities[i];

            for (int j = 0; j < i; j++)
            {
                ItemEntity earlier = _entities[j];

                if (!earlier.Stack.Matches(later.Stack)
                    || Vector3.Distance(earlier.Position, later.Position) > MergeRange)
                {
                    continue;
                }

                int max = _items.Definition(later.Stack.Item).MaxStack;
                int room = max - earlier.Stack.Count;

                if (room <= 0)
                {
                    continue;
                }

                int moved = Math.Min(room, later.Stack.Count);

                earlier.Stack = earlier.Stack.WithCount(earlier.Stack.Count + moved);
                later.Stack = later.Stack.WithCount(later.Stack.Count - moved);

                _entities[j] = earlier;

                if (later.Stack.IsEmpty)
                {
                    _entities.RemoveAt(i);
                    break;
                }

                _entities[i] = later;
            }
        }
    }
}

/// <summary>Jeden předmět ležící ve světě.</summary>
public struct ItemEntity
{
    public ItemStack Stack;
    public Vector3 Position;
    public Vector3 Velocity;
    public float Age;
    public bool OnGround;
}
