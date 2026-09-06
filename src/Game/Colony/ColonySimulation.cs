using OpenTK.Mathematics;
using Tesseris.Engine.Core;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;

namespace Tesseris.Game.Colony;

/// <summary>Co kolonista právě dělá. Pro M0 jen volný / zaneprázdněný, rozepsané po krocích.</summary>
public enum ColonistState : byte
{
    /// <summary>Nemá co dělat. Tohle je ten stav, který se počítá do „volných lidí".</summary>
    Idle,

    /// <summary>Má úkol, ale čeká, až na něj ve frontě dojde řada hledání cesty.</summary>
    WaitingForPath,

    /// <summary>Jde k úkolu.</summary>
    Moving,

    /// <summary>Kope.</summary>
    Digging,

    /// <summary>Nese materiál na sklad.</summary>
    Delivering,

    /// <summary>Stojí u ručního stroje a obsluhuje ho. Tohle je ten "obsazený člověk".</summary>
    Operating,

    /// <summary>
    /// Nemá práci a jen se prochází po domovské oblasti.
    /// </summary>
    /// <remarks>
    /// <b>Bez tohohle vypadá kolonie mrtvě.</b> Kolonista bez úkolu dřív doslova stál na
    /// místě a usnul ve frontě — hráč postavil radnici, přišel člověk a nehnul se. Žádné
    /// jídlo ani stroje ten dojem nespraví, dokud postavy nežijí.
    /// </remarks>
    Wandering,

    /// <summary>Jde se najíst do skladu, nebo u něj právě jí.</summary>
    /// <remarks>
    /// <b>Jeden úkol navíc, ne druhá hra</b>.
    /// Hlad je číslo a tenhle stav; žádné nálady, vztahy ani historky.
    /// </remarks>
    Eating,
}

/// <summary>
/// Kolonisté a jejich práce. První polovina herní smyčky podle úkolu T3.
/// </summary>
/// <remarks>
/// <para><b>Struct-of-arrays</b> (pravidlo 6.3). Kolonista není objekt, je to index do sady
/// polí. Cíl je 200 kolonistů v tiku pod 8 ms a předloha, jak to vypadat NESMÍ, je
/// <c>AnimalPopulation</c>: třída na tvora v <c>List</c> se stropem 48 kusů.</para>
///
/// <para><b>Tikají jen ti, kdo něco dělají</b> (pravidlo 6.4). Kdo je <see cref="ColonistState.Idle"/>
/// a není pro něj úkol, spí v <see cref="UpdateList"/> a probudí ho až nový úkol. Ve zralé
/// kolonii bude většina lidí čekat a nesmí stát ani takt.</para>
///
/// <para><b>Cesty jdou přes frontu s rozpočtem.</b> Kolonista si o cestu řekne a chvíli
/// stojí — to je v pořádku, zásek to není. Naměřeno, že jedno hledání stojí 0,858 ms,
/// takže se jich do tiku vejde osm.</para>
///
/// <para><b>Pád je stav světa, ne animace.</b> Když někdo vykope blok pod nohama stojícího
/// kolonisty, přestane být jeho buňka pochůzná. Kolonista pak spadne na nejbližší pochůznou
/// buňku pod sebou a zahodí cestu, protože ta už z jeho nové polohy nevede.</para>
/// </remarks>
public sealed class ColonySimulation
{
    /// <summary>Jak daleko od radnice se kolonista bez práce potuluje.</summary>
    /// <remarks>
    /// Domovská oblast, ne celý svět. Kdo nemá co dělat, má postávat u svého, ne odejít
    /// za obzor — a hráč musí poznat, kam kolonie patří.
    /// </remarks>
    public const int HomeRadius = 12;

    /// <summary>Nejdelší postání, ale až na KONCI procházky, ne mezi kroky.</summary>
    /// <remarks>
    /// Pauza po každé buňce byla chyba: vypadalo to jako „blok, zastávka, otočka, blok".
    /// Postát se má až po dojití, ne uprostřed chůze.
    /// </remarks>
    public const int WanderPauseTicks = 70;

    /// <summary>
    /// Kolik tiků se drží jeden cíl procházky, než se vybere jiný.
    /// </summary>
    /// <remarks>
    /// Domovská oblast má poloměr 12, takže přejít ji celou trvá při 3 blocích za vteřinu
    /// zhruba 480 tiků. Šest set je dost i na obcházení překážky a zároveň to není věčnost,
    /// když se cíl ukáže jako nedosažitelný.
    /// </remarks>
    public const int WanderGoalTicks = 600;

    /// <summary>Jak dlouho kolonista počká, než to ke skladu zkusí znovu. Pět vteřin.</summary>
    public const int EatRetryTicks = 300;

    /// <summary>
    /// Kolik tiků trvá jeden krok o buňku.
    /// </summary>
    /// <remarks>
    /// <para><b>Už se podle toho nechodí</b> — pohyb je spojitý a rychlost drží
    /// <see cref="ColonistBody.WalkSpeed"/>. Konstanta zůstává jako doba jednoho kroku
    /// odevzdávání, kde se nechodí, jen se čeká, a jako referenční hodnota v testu rychlosti:
    /// 60/13 = 4,6 bloku za vteřinu, tedy přesně <c>PlayerController.WalkSpeed</c>.</para>
    /// </remarks>
    public const int TicksPerStep = 13;

    /// <summary>
    /// Jak daleko od sebe se kolonisté odstrkávají.
    /// </summary>
    /// <remarks>
    /// Šířka těla. Míň by znamenalo, že se prolínají rameny; víc, že se rozestupují i tam,
    /// kde by se pohodlně minuli — a u stroje je úzké místo vždycky (viz commit 3db486f).
    /// </remarks>
    public const float AvoidRadius = ColonistBody.Width;

    /// <summary>
    /// Jak silně se vyhýbání propíše do pohybu, v blocích za vteřinu.
    /// </summary>
    /// <remarks>
    /// <b>Zlomek rychlosti chůze, ne její násobek.</b> Silnější odstrkávání přebije pohyb
    /// k cíli a dva lidé se pak od sebe odrážejí místo práce. Tohle je „ukroč stranou",
    /// ne „nepustím tě".
    /// </remarks>
    public const float AvoidSpeed = ColonistBody.WalkSpeed * 0.5f;

    /// <summary>
    /// Kolikrát silněji tlačí hráč než kolonista.
    /// </summary>
    /// <remarks>
    /// <para><b>Hráč se o kolonisty nezastaví, ale projít jimi jako duch taky nemá.</b>
    /// Zastavit ho by znamenalo sáhnout do kolizní smyčky <c>PlayerController</c>, která
    /// s tímhle úkolem nesouvisí — a hlavně by dav, který hráče zazdí v koutě, byl horší než
    /// dav, který se rozestoupí. Tohle je střední cesta: kdo hráči vleze do cesty, dostane
    /// pořádně z cesty, takže je odstrčení vidět, ale nikoho to nezablokuje.</para>
    ///
    /// <para>Trojnásobek proti odstrkávání mezi kolonisty. Míň nebylo v davu poznat, protože
    /// se příspěvek od hráče utopil mezi sousedy.</para>
    /// </remarks>
    public const float PlayerPushWeight = 3f;

    /// <summary>Kolik tiků trvá vykopání jednoho bloku.</summary>
    public const int TicksPerDig = 30;

    /// <summary>Nejdelší cesta, kterou kolonista unese. Delší se zkrátí a doplánuje se cestou.</summary>
    public const int MaxPathLength = 256;

    /// <summary>
    /// Za kolik tiků kolonista vyhládne z plného žaludku na práh <see cref="HungryAt"/>.
    /// </summary>
    /// <remarks>
    /// <b>V ticích, ne ve vteřinách</b> (pravidlo 6.6): hlad musí být deterministický, jinak by
    /// dva stejné savy dopadly jinak. Plný žaludek je <see cref="MaxHunger"/> a přibývá jedna
    /// jednotka za tik, takže 3 600 tiků je minuta herního času do prvního hladu a další dvě
    /// minuty do vyhladovění. Krátce schválně: hlad, který není za jedno posezení u hry vidět,
    /// nikoho k ničemu nenutí.
    /// </remarks>
    public const int TicksToHungry = 3_600;

    /// <summary>Nejvyšší hodnota hladu. Nad ní se nejde, jen se tam zůstane.</summary>
    public const int MaxHunger = 3 * TicksToHungry;

    /// <summary>Od téhle hodnoty si kolonista jde pro jídlo.</summary>
    public const int HungryAt = TicksToHungry;

    /// <summary>Od téhle hodnoty práce vázne, protože nebylo co jíst.</summary>
    /// <remarks>
    /// Dvojnásobek prahu hladu: mezi „mám hlad" a „nemůžu" je celá minuta, ve které má hráč
    /// šanci jídlo sehnat. Trest hned při prvním zakručení by byl neférový.
    /// </remarks>
    public const int StarvingAt = 2 * TicksToHungry;

    /// <summary>Kolikrát pomaleji jde práce vyhladovělému kolonistovi.</summary>
    /// <remarks>
    /// <b>Musí to být poznat na číslech, ne jen v kódu.</b> Dvojnásobek je natolik znatelný,
    /// že se rozdíl ukáže na počtu vykopaných bloků během jednoho měření.
    /// </remarks>
    public const int StarvingSlowdown = 2;

    /// <summary>Jak dlouho trvá jídlo.</summary>
    public const int TicksPerMeal = 30;

    private readonly int _capacity;

    /// <summary>
    /// Poloha nohou. Spojitá, ne index do mřížky.
    /// </summary>
    /// <remarks>
    /// <b>Tohle je ten rozdíl mezi figurkou a postavou.</b> Dřív tu byl <c>Vector3i</c>
    /// a pohyb byl skok o celou buňku jednou za třináct tiků; plynulost dělala až interpolace
    /// při vykreslování, takže to vypadalo jako šachovnice, i když se mezi kroky
    /// nezastavovalo. Mřížka zůstává v hledání cesty, pohyb po ní ne.
    /// </remarks>
    private readonly Vector3[] _position;

    /// <summary>Svislá rychlost. Pád a skok, jinak nula.</summary>
    private readonly float[] _velocityY;

    /// <summary>Stojí kolonista na zemi? Skočit smí jen ten, kdo stojí.</summary>
    private readonly bool[] _onGround;

    private readonly ColonistState[] _state;
    private readonly int[] _job;
    private readonly int[] _timer;
    private readonly ushort[] _carrying;
    private readonly Vector3i[] _path;
    private readonly int[] _pathLength;
    private readonly int[] _pathCursor;
    private readonly bool[] _alive;

    /// <summary>Poloha na začátku tiku. Jen pro plynulé vykreslení.</summary>
    /// <remarks>
    /// <b>Pevný tick a interpolace jsou jedna věta, ne dvě</b>. Simulace
    /// jede 60 Hz, obrazovka může 240 — bez uložené předchozí polohy by se obraz na většině
    /// snímků nehnul. A <b>skok se neinterpoluje</b>: teleport i respawn musí obě polohy
    /// srovnat, jinak postava plynule přeletí přes půl světa.
    /// </remarks>
    private readonly Vector3[] _previousPosition;

    /// <summary>Jde kolonista odevzdat náklad, ne kopat?</summary>
    private readonly bool[] _deliveringPath;

    /// <summary>Kam náklad nese.</summary>
    private readonly Vector3i[] _deliverTarget;

    /// <summary>Jak je kdo hladový, 0 až <see cref="MaxHunger"/>. Roste každý tik.</summary>
    /// <remarks>
    /// Vlastní pole, ne struktura na kolonistu (pravidlo 6.3). Hlad se čte v tick cestě u všech
    /// dvou set lidí, takže patří do souvislé paměti vedle ostatních polí.
    /// </remarks>
    private readonly int[] _hunger;

    /// <summary>Jde kolonista ke skladu se najíst, ne kopat ani odevzdat?</summary>
    private readonly bool[] _eatingPath;

    /// <summary>Jak dlouho se kolonista nebude znovu pokoušet dojít si pro jídlo.</summary>
    /// <remarks>Obdoba <c>JobState.Deferred</c> u kopání: bez ní se z neúspěšné cesty stane smyčka.</remarks>
    private readonly int[] _eatCooldown;

    /// <summary>Jak dlouho ještě postojí, než se zase pohne.</summary>
    private readonly int[] _wanderPause;

    /// <summary>Kolik tiků ještě míří k rozchozenému cíli, než si vybere jiný.</summary>
    private readonly int[] _wanderRemaining;

    /// <summary>
    /// Kolik bloků kolonista v tomhle tiku už ušel. Rozpočet rychlosti.
    /// </summary>
    /// <remarks>
    /// <para><b>Proč rozpočet a ne jen opatrné sčítání.</b> Pohyb se skládá z několika cest:
    /// krok po cestě, krok procházky a rozestupování. Každá je sama o sobě omezená, ale
    /// dokud se hlídaly zvlášť, dal se rozpočet obejít tím, že se v jednom tiku sešly dvě —
    /// naměřeno sondou v běžící hře jako <b>0,153 bloku za tik</b> proti 0,077, kolik dovolí
    /// chůze. Kolonista tím chodil dvakrát rychleji než hráč, což zadání zakazuje přímo.</para>
    ///
    /// <para><b>Jedno počítadlo je odolnější než opatrnost.</b> Hlídat pořadí a stavy znamená,
    /// že další přidaná cesta pohybu tu chybu zavede znovu; společný rozpočet ji zastaví
    /// i tehdy, když na něj autor té cesty nepomyslí.</para>
    /// </remarks>
    private readonly float[] _movedThisTick;

    /// <summary>Kam si kolonista vyšel. Chodí se za cílem, ne náhodným směrem.</summary>
    private readonly Vector3i[] _wanderGoal;

    /// <summary>Vlastní generátor pro každého, ať se nehýbou jako jeden.</summary>
    /// <remarks>
    /// <b>Ne <c>Random</c>.</b> Simulace musí být deterministická (pravidlo 6.6), takže
    /// stejný sav plus stejné vstupy musí dát stejný běh. Tohle je obyčejný posuvný
    /// generátor s vlastním stavem na kolonistu — levný, bez alokace a reprodukovatelný.
    /// </remarks>
    private readonly uint[] _wanderSeed;

    private readonly UpdateList _active = new();
    private readonly PathRequestQueue _requests = new();

    /// <summary>
    /// Kdo kde stojí. Přestavuje se jednou za tik, aby se vyhýbání neptalo každý s každým.
    /// </summary>
    /// <remarks>
    /// Lineární průchod (jak to dělal commit 8b9d43b) je při dvou stech lidech 40 000
    /// porovnání za tik. Prostorový index z toho udělá průchod 3×3×3 přihrádkami.
    /// </remarks>
    private readonly ColonistGrid _neighbours;

    private int _count;

    public ColonySimulation(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _capacity = capacity;
        _position = new Vector3[capacity];
        _velocityY = new float[capacity];
        _onGround = new bool[capacity];
        _state = new ColonistState[capacity];
        _job = new int[capacity];
        _timer = new int[capacity];
        _carrying = new ushort[capacity];
        _path = new Vector3i[capacity * MaxPathLength];
        _pathLength = new int[capacity];
        _pathCursor = new int[capacity];
        _alive = new bool[capacity];
        _previousPosition = new Vector3[capacity];
        _deliveringPath = new bool[capacity];
        _deliverTarget = new Vector3i[capacity];
        _hunger = new int[capacity];
        _eatingPath = new bool[capacity];
        _eatCooldown = new int[capacity];
        _wanderPause = new int[capacity];
        _wanderRemaining = new int[capacity];
        _movedThisTick = new float[capacity];
        _wanderGoal = new Vector3i[capacity];
        _wanderSeed = new uint[capacity];
        _neighbours = new ColonistGrid(capacity);
    }

    /// <summary>
    /// Kam se dá odevzdat náklad. Doplní kolonii pás, stroj nebo sklad.
    /// </summary>
    /// <remarks>
    /// <b>Rozhraní, ne přímá vazba na pásy.</b> Kolonisté o pásech vědět nemusí a nemají —
    /// jinak by se z <c>ColonySimulation</c> stal uzel, přes který vede všechno.
    /// </remarks>
    public interface IDeliveryTargets
    {
        /// <summary>Najde nejbližší místo, kam se tenhle materiál vejde.</summary>
        bool TryFindTarget(ushort item, Vector3i from, out Vector3i cell);

        /// <summary>Odevzdá materiál na zadané buňce. False, když se tam mezitím nevešel.</summary>
        bool TryDeliver(ushort item, Vector3i cell);
    }

    /// <summary>Kam kolonisté nosí vykopané. Bez toho končí všechno v abstraktním skladu.</summary>
    public IDeliveryTargets? Delivery { get; set; }

    /// <summary>Kolik nákladů skončilo v pásu nebo stroji místo ve skladu.</summary>
    public int DeliveredToFactory { get; private set; }

    /// <summary>
    /// Sklad kolonie. Ví, CO v něm leží, ne jen kolik.
    /// </summary>
    /// <remarks>
    /// Dřív to bylo jedno počítadlo, takže vykopaná ruda nikam nedošla — jen se zvětšilo číslo.
    /// Nešlo se podívat, co kolonie má, a hlavně z toho nešlo nic vzít; bez toho nemůže
    /// existovat hlad, protože kolonista nemá kam si dojít pro jídlo.
    /// </remarks>
    public ColonyStore Store { get; } = new();

    /// <summary>Kolik materiálu na skladu leží. Tohle je kritérium hotového T3.</summary>
    public int StoredItems => Store.Total;

    /// <summary>
    /// Uloží materiál rovnou na sklad, aniž by ho někdo nesl.
    /// </summary>
    /// <remarks>
    /// Pro bourání: obsah zbouraného pásu ani stroje se nesmí ztratit. Mizející materiál je
    /// nejhorší druh chyby — nikde se nehlásí a projeví se až tím, že výroba nesedí.
    /// </remarks>
    public void StoreItems(ushort item, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Store.Add(item, count);
    }

    public int Count => _count;

    /// <summary>Kolik lidí je volných. Číslo, na kterém stojí celá hra.</summary>
    public int IdleCount { get; private set; }

    /// <summary>Kolik kolonistů se tikalo v posledním kroku.</summary>
    public int ActiveCount => _active.Count;

    /// <summary>
    /// Poloha nohou. Spojitá, ne střed buňky.
    /// </summary>
    /// <remarks>
    /// Tohle je od téhle chvíle ta pravdivá poloha kolonisty; <see cref="CellOf"/> je z ní
    /// odvozená. Obráceně to bylo dřív a projevovalo se to tím, že postava skákala o blok.
    /// </remarks>
    public Vector3 PositionOf(int colonist) => _position[colonist];

    /// <summary>Poloha na začátku tiku. Mezi ní a <see cref="PositionOf"/> render interpoluje.</summary>
    public Vector3 PreviousPositionOf(int colonist) => _previousPosition[colonist];

    /// <summary>Svislá rychlost. Nenulová při pádu a skoku.</summary>
    public float VerticalVelocityOf(int colonist) => _velocityY[colonist];

    /// <summary>Stojí kolonista na zemi?</summary>
    public bool IsOnGround(int colonist) => _onGround[colonist];

    /// <summary>
    /// Buňka, ve které kolonista stojí.
    /// </summary>
    /// <remarks>
    /// <b>Odvozená hodnota, ne stav.</b> Zůstává, protože v mřížce dál bydlí hledání cesty,
    /// dosah na kopaný blok a odevzdávání — a všechno tohle se ptá „v které buňce jsem".
    /// Pohyb se z ní ale neodvozuje.
    /// </remarks>
    public Vector3i CellOf(int colonist) => ColonistBody.CellOf(_position[colonist]);

    /// <summary>Odkud kolonista právě odchází, po buňkách. Pro sondy a starší kód.</summary>
    public Vector3i PreviousCellOf(int colonist) => ColonistBody.CellOf(_previousPosition[colonist]);

    /// <summary>
    /// Jak daleko je krok mezi buňkami, 0 až 1.
    /// </summary>
    /// <remarks>
    /// <b>Render tohle už nepotřebuje</b> — interpoluje se mezi dvěma spojitými polohami.
    /// Zůstává jako údaj o rozpracovanosti kroku, který trvá v ticích (kopání, jídlo),
    /// a dělí se skutečnou délkou, ne konstantou: hladovému trvá krok dvakrát dýl.
    /// </remarks>
    public float StepProgressOf(int colonist) =>
        Math.Clamp(_timer[colonist] / (float)WorkTicks(colonist, TicksPerStep), 0f, 1f);

    public ColonistState StateOf(int colonist) => _state[colonist];

    /// <summary>Jak je kolonista hladový, 0 až <see cref="MaxHunger"/>.</summary>
    public int HungerOf(int colonist) => _hunger[colonist];

    /// <summary>Má kolonista hlad natolik, že si jde pro jídlo?</summary>
    public bool IsHungry(int colonist) => _hunger[colonist] >= HungryAt;

    /// <summary>Hladoví natolik, že mu vázne práce?</summary>
    public bool IsStarving(int colonist) => _hunger[colonist] >= StarvingAt;

    /// <summary>Kolik lidí právě hladoví natolik, že jim vázne práce. Pro panel a sondy.</summary>
    public int StarvingCount
    {
        get
        {
            int count = 0;
            for (int id = 0; id < _count; id++)
            {
                if (_alive[id] && _hunger[id] >= StarvingAt)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Nejvyšší hlad v kolonii. Jedno číslo, na kterém je stav vidět.</summary>
    public int WorstHunger
    {
        get
        {
            int worst = 0;
            for (int id = 0; id < _count; id++)
            {
                if (_alive[id] && _hunger[id] > worst)
                {
                    worst = _hunger[id];
                }
            }

            return worst;
        }
    }

    /// <summary>Kolik jídel se v kolonii snědlo za celý běh. Důkaz, že hlad opravdu ubývá.</summary>
    public int MealsEaten { get; private set; }

    /// <summary>Nastaví hlad. Jen pro načítání savu a pro sondy.</summary>
    public void SetHunger(int colonist, int hunger)
    {
        if (colonist < 0 || colonist >= _count)
        {
            return;
        }

        _hunger[colonist] = Math.Clamp(hunger, 0, MaxHunger);

        // Hladový člověk musí mít šanci si o jídlo říct, i když zrovna spal.
        if (_hunger[colonist] >= HungryAt && _state[colonist] == ColonistState.Idle)
        {
            _active.Wake(colonist);
        }
    }

    public int JobOf(int colonist) => _job[colonist];

    public ushort CarryingOf(int colonist) => _carrying[colonist];

    /// <summary>Přidá kolonistu doprostřed zadané buňky.</summary>
    public int Add(Vector3i cell) => Add(ColonistBody.CentreOf(cell));

    /// <summary>Přidá kolonistu na přesnou polohu.</summary>
    /// <remarks>
    /// <b>Obě polohy se srovnají.</b> Nový kolonista nikam nepokračuje, takže by se přes něj
    /// nemělo interpolovat — jinak by první snímek po příchodu přiletěl z počátku souřadnic.
    /// </remarks>
    public int Add(Vector3 position)
    {
        if (_count == _capacity)
        {
            throw new InvalidOperationException($"Kolonistů je nejvýš {_capacity}.");
        }

        int id = _count++;
        _position[id] = position;
        _previousPosition[id] = position;
        _velocityY[id] = 0f;
        _onGround[id] = true;
        _state[id] = ColonistState.Idle;
        _job[id] = -1;
        _timer[id] = 0;
        _carrying[id] = BlockRegistry.Air;
        _pathLength[id] = 0;
        _pathCursor[id] = 0;
        _alive[id] = true;
        _deliveringPath[id] = false;

        // Kdo přijde, přišel po svých a najedený. Nový člověk s plným hladem by ve chvíli
        // příchodu zamířil rovnou do skladu a vypadalo by to jako chyba příchodu.
        _hunger[id] = 0;
        _eatingPath[id] = false;
        IdleCount++;

        // Nový kolonista se probudí, aby si došel pro úkol.
        _active.Wake(id);
        return id;
    }

    /// <summary>
    /// Postaví kolonistu ke stroji. Tím přestává být volný.
    /// </summary>
    /// <remarks>
    /// <b>Bez tohohle nemá kritérium T5 co ukazovat.</b> Stroj si operátora pamatoval, ale
    /// kolonista o tom nevěděl a dál se počítal mezi volné — takže se počítadlo po
    /// automatizaci nemělo o co zvednout.
    /// </remarks>
    public bool TryOccupy(int colonist, int machine)
    {
        if (colonist < 0 || colonist >= _count || !_alive[colonist] || _state[colonist] != ColonistState.Idle)
        {
            return false;
        }

        _job[colonist] = -1;
        _pathLength[colonist] = 0;
        SetState(colonist, ColonistState.Operating);

        // U stroje stojí a nic nepočítá — nemusí se tikat.
        _active.Sleep(colonist);
        _ = machine;
        return true;
    }

    /// <summary>Pustí kolonistu od stroje. Zase je volný a půjde si pro práci.</summary>
    public bool Release(int colonist)
    {
        if (colonist < 0 || colonist >= _count || _state[colonist] != ColonistState.Operating)
        {
            return false;
        }

        SetState(colonist, ColonistState.Idle);
        _active.Wake(colonist);
        return true;
    }

    /// <summary>
    /// Postaví kolonistu z uloženého stavu.
    /// </summary>
    /// <remarks>
    /// <para><b>Pochůznost se nekontroluje.</b> Při načítání ještě nemusí být postavená
    /// navigační mřížka toho chunku, takže by kontrola odmítla i platnou buňku a kolonista
    /// by se ztratil. Buňka byla platná ve chvíli uložení a svět se mezitím nezměnil.</para>
    ///
    /// <para><b>Cesta se neobnovuje.</b> Je to odvozený stav a doplánovat ji je levné —
    /// naměřeno 0,858 ms. Uložená cesta by navíc mohla vést přes to, co se od uložení změnilo.
    /// Načtený kolonista je proto vždycky volný a hned si řekne o práci.</para>
    /// </remarks>
    /// <summary>
    /// Postaví kolonistu doprostřed buňky.
    /// </summary>
    /// <remarks>
    /// <b>Tohle přetížení tu je jako past na tichou konverzi.</b> OpenTK má implicitní převod
    /// <c>Vector3i</c> → <c>Vector3</c>, takže bez něj by se <c>Restore(cell, …)</c> přeložil
    /// bez chyby a posadil kolonistu do ROHU buňky, ne doprostřed — tedy o půl bloku vedle,
    /// napůl ve zdi. Překlad by prošel, testy většinou taky a projevilo by se to až tím, že
    /// načtený člověk stojí jinak než uložený.
    /// </remarks>
    public int Restore(Vector3i cell, ushort carrying, int hunger = 0) =>
        Restore(ColonistBody.CentreOf(cell), carrying, hunger);

    /// <inheritdoc cref="Restore(Vector3i, ushort, int)"/>
    public int Restore(Vector3 position, ushort carrying, int hunger = 0)
    {
        int id = Add(position);
        _carrying[id] = carrying;
        _hunger[id] = Math.Clamp(hunger, 0, MaxHunger);

        // Kdo něco nese, musí se toho nejdřív zbavit; volný je až potom.
        if (carrying != BlockRegistry.Air)
        {
            SetState(id, ColonistState.Delivering);
        }

        return id;
    }

    /// <summary>Obnoví počítadla ze savu, aby se statistiky po načtení nevynulovaly.</summary>
    /// <remarks>
    /// Obsah skladu se sem nepředává: ten drží <see cref="Store"/> a načítá se po druzích.
    /// Jedno souhrnné číslo by po načtení nevědělo, co v něm vlastně leží.
    /// </remarks>
    public void RestoreCounters(int delivered)
    {
        DeliveredToFactory = Math.Max(0, delivered);
    }

    /// <summary>Zahodí všechny kolonisty. Pro načítání do už rozběhnuté kolonie.</summary>
    public void Clear()
    {
        for (int id = 0; id < _count; id++)
        {
            _active.Sleep(id);
        }

        _count = 0;
        IdleCount = 0;
        DeliveredToFactory = 0;
        MealsEaten = 0;
        Store.Clear();
    }

    /// <summary>Probudí všechny volné. Volá se, když přibude práce.</summary>
    public void WakeIdle()
    {
        for (int id = 0; id < _count; id++)
        {
            if (_alive[id] && _state[id] == ColonistState.Idle)
            {
                _active.Wake(id);
            }
        }
    }

    /// <summary>
    /// Kde stojí hráč. Kolonisté se mu vyhýbají stejně jako sobě navzájem.
    /// </summary>
    /// <remarks>
    /// <b>Vyhýbání, ne kolize hráče.</b> Hráč má vlastní fyziku a ta o kolonistech neví;
    /// zastavit ho o ně by znamenalo sáhnout na <c>PlayerController</c>, který se v tomhle
    /// úkolu měnit nemá. Kolonista se místo toho ukročí — a to je i lepší chování: dav, který
    /// hráče zazdí v koutě, je horší než dav, který se rozestoupí.
    /// </remarks>
    public Vector3? PlayerPosition { get; set; }

    /// <summary>
    /// Jeden simulační krok.
    /// </summary>
    public void Tick(VoxelWorld world, BlockRegistry blocks, ushort water, NavGraph graph, DigJobQueue jobs)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(jobs);

        DeliverPathAnswers(graph, jobs);
        TickHunger();
        RebuildNeighbours();
        Array.Clear(_movedThisTick, 0, _count);

        // PŘEDCHOZÍ POLOHA SE BERE PŘED POHYBEM, ne po něm, a VŠEM — i těm, kdo se netikají.
        // Kdo se v tomhle tiku nehnul, má obě polohy stejné a interpolace mu dá klid;
        // kdyby se to dělalo jen aktivním, uspaný kolonista by si nesl polohu z tiku,
        // kdy naposledy pracoval, a render by ho každý snímek táhl jinam.
        for (int id = 0; id < _count; id++)
        {
            _previousPosition[id] = _position[id];
        }

        // ODZADU: uspání prohodí poslední prvek na uvolněné místo, takže při průchodu
        // odpředu by se jeden kolonista přeskočil.
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            int id = _active.Active[i];
            Step(world, blocks, water, graph, jobs, id);
        }

        // ROZESTUPOVÁNÍ STOJÍCÍCH, AŽ PO KROKU. Kdo jde, dostal vyhýbání rovnou ve svém
        // kroku (viz MoveTowards); tohle je pro ty, kdo se nikam nechystají a jinak by
        // v sobě zůstali stát — dav u stroje nebo u skladu.
        //
        // POŘADÍ JE PODSTATNÉ. Když tohle běželo PŘED krokem, rozhodovalo se podle stavu
        // z minulého tiku: kolonista, který se právě rozešel, byl v tu chvíli ještě Idle,
        // dostal odstrčení tady a hned nato další ve svém kroku. Ve hře to bylo vidět jako
        // „nejdelsi za tik 0,153 bloku, stav Moving" proti 0,077, kolik dovolí chůze.
        TickSeparation(graph);

        _requests.Process(graph);
        ClampTickSpeed();
    }

    /// <summary>
    /// Ořízne posun za tik na to, co dovolí rychlost. Poslední slovo v celém pohybu.
    /// </summary>
    /// <remarks>
    /// <para><b>Jeden strop na konci místo opatrnosti na pěti místech.</b> Pohyb se skládá
    /// z několika cest — krok po cestě, krok procházky, rozestupování stojících, dokročení
    /// po pádu — a dokud se hlídaly zvlášť, dal se limit obejít tím, že se dvě z nich sešly
    /// v jednom tiku. Postupně jsem takhle zavřel dvě díry (sčítání odstrčení s krokem,
    /// pořadí rozestupování proti kroku) a sonda v běžící hře pokaždé našla další cestu:
    /// nejdřív <b>0,153</b> bloku za tik proti dovoleným 0,077, pak <b>0,100</b> proti 0,050
    /// u procházky. Hádat, kde je ta třetí, nemá cenu.</para>
    ///
    /// <para><b>Porovnává se s polohou na začátku tiku</b>, kterou už simulace drží kvůli
    /// interpolaci renderu. Nezáleží tedy na tom, kolik cest pohybu se v tiku uplatnilo ani
    /// v jakém pořadí; strop platí na jejich součet a další přidaná cesta ho nemůže obejít.</para>
    ///
    /// <para><b>Svislá složka se neořezává.</b> Pád a skok mají vlastní rychlost a s chůzí
    /// nesouvisí; ořezat je tady by znamenalo, že kolonista padá pomaleji než hráč.</para>
    /// </remarks>
    private void ClampTickSpeed()
    {
        for (int id = 0; id < _count; id++)
        {
            if (!_alive[id])
            {
                continue;
            }

            float limit = MaximumSpeedOf(id) * ColonistBody.StepSeconds;

            Vector3 from = _previousPosition[id];
            Vector3 to = _position[id];

            float dx = to.X - from.X;
            float dz = to.Z - from.Z;
            float travelled = MathF.Sqrt((dx * dx) + (dz * dz));

            if (travelled <= limit + 1e-5f || travelled <= 0f)
            {
                continue;
            }

            float scale = limit / travelled;
            _position[id] = new Vector3(
                from.X + (dx * scale),
                to.Y,
                from.Z + (dz * scale));
        }
    }

    /// <summary>Jak rychle smí tenhle kolonista jít. Procházka je pomalejší než práce.</summary>
    /// <remarks>
    /// Hladovému se rychlost dělí stejným číslem, jakým se násobí doba kopání — jinak by
    /// hlad zpomaloval práci, ale ne chůzi, a tlak ze zadání by šel obejít chozením.
    /// </remarks>
    private float MaximumSpeedOf(int id)
    {
        float speed = _state[id] == ColonistState.Wandering
            ? ColonistBody.WanderSpeed
            : ColonistBody.WalkSpeed;

        return _hunger[id] >= StarvingAt ? speed / StarvingSlowdown : speed;
    }

    /// <summary>Je tenhle kolonista právě na cestě? Takový má vyhýbání rovnou v kroku.</summary>
    private bool IsWalking(int id) =>
        _state[id] is ColonistState.Moving or ColonistState.Wandering;

    /// <summary>
    /// Přestaví prostorový index poloh. Jednou za tik, ne při každém dotazu.
    /// </summary>
    /// <remarks>
    /// <b>Prochází se všichni, ne jen aktivní.</b> Spící kolonista pořád stojí a pořád
    /// překáží — kdyby v indexu nebyl, prošel by jím kolega skrz a vypadalo by to jako chyba
    /// vykreslování. Je to průchod souvislým polem, tedy proti jednomu hledání cesty
    /// (0,858 ms) neměřitelné.
    /// </remarks>
    private void RebuildNeighbours()
    {
        _neighbours.Clear();
        for (int id = 0; id < _count; id++)
        {
            if (_alive[id])
            {
                _neighbours.Add(id, _position[id]);
            }
        }
    }

    /// <summary>
    /// Posune tělo o jeden tik směrem k zadanému bodu.
    /// </summary>
    /// <remarks>
    /// <para><b>Tady je celý ten rozdíl proti šachovnici.</b> Nejde se o buňku, jde se
    /// rychlostí za vteřinu vynásobenou délkou tiku — a když je cíl blíž, ujde se jen kus.
    /// Šikmo se jde samo, protože směr je normalizovaný vektor, ne jeden z osmi.</para>
    ///
    /// <para><b>Vyhýbání se PŘIČÍTÁ, nikdy nenahrazuje.</b> Poučení z commitu 3db486f: zákaz
    /// vstupu vyrábí uváznutí všude, kde je úzké místo, a u stroje je úzké místo vždycky.
    /// Kolonista proto pořád míří, kam mířil; jen se u toho odstrčí.</para>
    ///
    /// <para><b>Skáče se, až když to nejde jinak.</b> Kdo do něčeho narazil a za tou překážkou
    /// je o patro výš kde stát, odrazí se. Skok bez té druhé podmínky by znamenal poskakování
    /// u zdi donekonečna.</para>
    /// </remarks>
    /// <returns>Došel kolonista na cíl?</returns>
    private bool MoveTowards(NavGraph graph, int id, Vector3 target, float speed)
    {
        Vector3 position = _position[id];

        float toX = target.X - position.X;
        float toZ = target.Z - position.Z;
        float distance = MathF.Sqrt((toX * toX) + (toZ * toZ));

        float wishX = 0f;
        float wishZ = 0f;

        if (distance > 1e-4f)
        {
            wishX = toX / distance;
            wishZ = toZ / distance;
        }

        // O kolik se za tenhle tik ujde. Nikdy se cíl nepřestřelí — jinak by kolonista
        // kolem něj kmital sem a tam a nikdy nedošel. A nikdy se nepřekročí rozpočet
        // rychlosti; ten platí přes všechny cesty pohybu dohromady.
        float travel = MathF.Min(speed * ColonistBody.StepSeconds, distance);
        travel = MathF.Min(travel, Budget(id, speed));

        // VYHÝBÁNÍ SE SKLÁDÁ SE SMĚREM CHŮZE, NEPŘIČÍTÁ SE K HOTOVÉMU KROKU. Když se obojí
        // počítalo zvlášť a v tiku se sečetlo, ušel kolonista v davu 0,115 bloku za tik proti
        // 0,077, kolik dovolí chůze — tedy chodil rychleji než hráč. Naměřeno sondou v běžící
        // hře; test to potvrdil až ve chvíli, kdy dav zároveň někam MÍŘIL, protože stojící
        // dav se jen rozestupuje a není k čemu přičítat.
        //
        // Skládá se SMĚR, ne dráha: výsledný vektor se normalizuje a teprve pak vynásobí
        // krokem. Kolonista se proto uhne tím, že jde šikmo, ne tím, že zrychlí.
        Vector2 avoid = Separation(id, position);

        float dirX = wishX + avoid.X;
        float dirZ = wishZ + avoid.Y;
        float dirLength = MathF.Sqrt((dirX * dirX) + (dirZ * dirZ));

        if (dirLength > 1f)
        {
            dirX /= dirLength;
            dirZ /= dirLength;
        }

        float dx = dirX * travel;
        float dz = dirZ * travel;

        bool blocked = ColonistBody.TryMoveHorizontal(graph, ref position, dx, dz);

        // Skutečně ušitá dráha, ne zamýšlená: o zeď se kus ukrojí a to se do rozpočtu
        // počítat nemá.
        Spend(id, position, _position[id]);

        if (blocked && _onGround[id] && ColonistBody.ShouldJump(graph, position, wishX, wishZ))
        {
            _velocityY[id] = ColonistBody.JumpVelocity;
            _onGround[id] = false;
        }

        float velocityY = _velocityY[id];
        bool onGround = _onGround[id];
        ColonistBody.ApplyGravity(graph, ref position, ref velocityY, ref onGround);

        _position[id] = position;
        _velocityY[id] = velocityY;
        _onGround[id] = onGround;

        float remainingX = target.X - position.X;
        float remainingZ = target.Z - position.Z;

        return ((remainingX * remainingX) + (remainingZ * remainingZ))
            <= ColonistBody.ArrivalRadius * ColonistBody.ArrivalRadius;
    }

    /// <summary>
    /// Rozestoupí všechny, kdo stojí příliš blízko u sebe.
    /// </summary>
    /// <remarks>
    /// <para><b>Platí i pro toho, kdo nikam nejde</b> — a to je zrovna ten případ, kdy je
    /// splynutí do jednoho místa vidět: dav u stroje, u skladu nebo hlouček bez práce.
    /// Když se vyhýbání počítalo jen uvnitř chůze, stáli lidé v sobě přesně tam, kde se
    /// nejdřív zastavili.</para>
    ///
    /// <para><b>Vyhýbání, ne zákaz vstupu.</b> Tohle je celé poučení z commitu 3db486f: zákaz
    /// vstupu do obsazené buňky rozbil uzavřenou herní smyčku, protože u stroje je úzké místo
    /// vždycky — jeden kolonista se nedostal na buňku, ze které se odevzdává. Odstrčení nikdy
    /// nikomu nezakáže někam dojít, jen ho o kus posune; kdo se rozestoupit nemůže, prostě
    /// zůstane a projdou se.</para>
    ///
    /// <para><b>Ze společného snímku poloh, ne postupně.</b> Index se přestavuje jednou za
    /// tik a odstrčení se počítá proti němu, takže nezáleží na pořadí průchodu — jinak by
    /// dvojice sousedů dopadla jinak podle toho, kdo má menší index (pravidlo 6.6).</para>
    /// </remarks>
    private void TickSeparation(NavGraph graph)
    {
        for (int id = 0; id < _count; id++)
        {
            // KDO JDE, MÁ VYHÝBÁNÍ UŽ V KROKU. Tady se řeší jen stojící, protože jinak by se
            // odstrčení uplatnilo dvakrát za tik a chodec by byl rychlejší než hráč.
            if (!_alive[id] || IsWalking(id))
            {
                continue;
            }

            Vector3 position = _position[id];
            Vector2 avoid = Separation(id, position);

            if (avoid.LengthSquared < 1e-8f)
            {
                continue;
            }

            // ODSTRČENÍ SE OŘEŽE NA JEDNIČKU. Separation sčítá příspěvky od všech sousedů,
            // takže uprostřed davu vyjde vektor delší než jedna — a rychlost vyhýbání by pak
            // nebyla AvoidSpeed, ale její násobek podle toho, kolik lidí kolem stojí.
            // Naměřeno ve hře jako „nejdelsi za tik 0,153 bloku" proti 0,077, kolik dovolí
            // chůze: dva sousedé odstrkávali naráz a součet přebil vlastní pohyb.
            float length = avoid.Length;
            if (length > 1f)
            {
                avoid /= length;
            }

            // ROZPOČET PODLE TOHO, JAK RYCHLE TENHLE ČLOVĚK CHODÍ. Procházka jde pomaleji než
            // práce, takže hlídat ji rychlostí chůze znamená, že se courající kolonista dá
            // odstrčením zrychlit — naměřeno ve hře jako 0,100 bloku za tik ve stavu
            // Wandering proti 0,050, kolik dovolí procházka.
            float limit = _state[id] == ColonistState.Wandering
                ? ColonistBody.WanderSpeed
                : ColonistBody.WalkSpeed;

            float step = AvoidSpeed * ColonistBody.StepSeconds;
            step = MathF.Min(step, Budget(id, limit));

            if (step <= 0f)
            {
                continue;
            }

            Vector3 before = position;
            ColonistBody.TryMoveHorizontal(graph, ref position, avoid.X * step, avoid.Y * step);
            Spend(id, position, before);
            _position[id] = position;
        }
    }

    /// <summary>Kolik bloků smí kolonista v tomhle tiku ještě ujít.</summary>
    private float Budget(int id, float speed) =>
        MathF.Max(0f, (speed * ColonistBody.StepSeconds) - _movedThisTick[id]);

    /// <summary>Odečte z rozpočtu skutečně ušitou vodorovnou dráhu.</summary>
    private void Spend(int id, Vector3 to, Vector3 from)
    {
        float dx = to.X - from.X;
        float dz = to.Z - from.Z;
        _movedThisTick[id] += MathF.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>Kam se má kolonista uhnout, aby nestál v jiném ani v hráči.</summary>
    /// <remarks>
    /// Hráč se přičítá zvlášť: není v poli kolonistů a jeho poloha přichází zvenku, protože
    /// simulace o okně ani o <c>PlayerController</c> nesmí vědět.
    /// </remarks>
    private Vector2 Separation(int id, Vector3 position)
    {
        Vector2 avoid = _neighbours.Separation(id, position, AvoidRadius);

        if (PlayerPosition is not { } player)
        {
            return avoid;
        }

        float dx = position.X - player.X;
        float dz = position.Z - player.Z;

        if (MathF.Abs(position.Y - player.Y) >= 1f)
        {
            return avoid;
        }

        float distanceSquared = (dx * dx) + (dz * dz);
        if (distanceSquared >= AvoidRadius * AvoidRadius)
        {
            return avoid;
        }

        // PŘESNĚ V HRÁČI. Nulový rozdíl nemá směr, takže bez téhle větve by se kolonista,
        // kterému hráč vlezl doprostřed těla, neuhnul VŮBEC — a je to nejběžnější případ,
        // protože hráč do lidí chodí. Rozejde se podle indexu, ať to je reprodukovatelné
        // (pravidlo 6.6); náhoda by z toho udělala jiný běh při stejném savu.
        if (distanceSquared < 1e-6f)
        {
            float angle = id * 2.399963f;
            return new Vector2(
                avoid.X + (MathF.Cos(angle) * PlayerPushWeight),
                avoid.Y + (MathF.Sin(angle) * PlayerPushWeight));
        }

        float distance = MathF.Sqrt(distanceSquared);
        float strength = (AvoidRadius - distance) / AvoidRadius * PlayerPushWeight;

        return new Vector2(
            avoid.X + (dx / distance * strength),
            avoid.Y + (dz / distance * strength));
    }

    /// <summary>
    /// Hlad roste všem, i těm, kdo spí.
    /// </summary>
    /// <remarks>
    /// <para><b>Prochází se všichni, ne jen aktivní</b> — a je to jediné místo v tiku, kde se
    /// to smí. Kdyby hlad rostl jen tikaným lidem (pravidlo 6.4), nehladověl by nikdo, kdo
    /// nemá práci, a hlad by šel obejít tím, že se nic neoznačí. Je to sčítání dvou set
    /// intů, tedy jeden průchod souvislým polem; pathfinding jednoho člověka stojí 0,858 ms,
    /// takže tohle je proti němu neměřitelné.</para>
    ///
    /// <para><b>Kdo vyhládne, probudí se.</b> Spící člověk by si jinak o jídlo nikdy neřekl —
    /// spí přesně proto, že pro něj není práce.</para>
    /// </remarks>
    private void TickHunger()
    {
        for (int id = 0; id < _count; id++)
        {
            if (_eatCooldown[id] > 0)
            {
                _eatCooldown[id]--;
            }

            if (!_alive[id] || _hunger[id] >= MaxHunger)
            {
                continue;
            }

            _hunger[id]++;

            // Právě překročil práh: musí dostat šanci si dojít pro jídlo.
            if (_hunger[id] == HungryAt && _state[id] == ColonistState.Idle)
            {
                _active.Wake(id);
            }
        }
    }

    /// <summary>
    /// Zkusí poslat hladového kolonistu ke skladu pro jídlo.
    /// </summary>
    /// <remarks>
    /// <para><b>Nejdřív se ptá, jestli je co jíst.</b> Poslat člověka přes půl mapy k prázdnému
    /// skladu je horší než ho nechat pracovat — došel by tam, otočil se a mezitím by vyhládl
    /// ještě víc. Bez jídla proto pracuje dál a hlad roste; to je ten tlak ze zadání.</para>
    ///
    /// <para><b>Sklad musí mít místo ve světě.</b> Dokud nestojí radnice, sklad je jen obsah
    /// bez souřadnice — dá se do něj odevzdat, ale nedá se k němu dojít.</para>
    /// </remarks>
    /// <returns>Vzal si kolonista jídlo za úkol?</returns>
    private bool TryGoEat(NavGraph graph, int id)
    {
        if (_hunger[id] < HungryAt || !Store.HasCell || !Store.HasFood || _eatCooldown[id] > 0)
        {
            return false;
        }

        // Stojí u skladu? Tak se rovnou naji, ať se kvůli jednomu kroku nehledá cesta.
        Vector3i delta = Store.Cell - CellOf(id);
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) + Math.Abs(delta.Z) <= 2)
        {
            _eatingPath[id] = false;
            _timer[id] = 0;
            SetState(id, ColonistState.Eating);
            return true;
        }

        if (AdjacentStandable(graph, Store.Cell) is not { } approach || approach == CellOf(id))
        {
            return false;
        }

        _eatingPath[id] = true;
        SetState(id, ColonistState.WaitingForPath);
        _requests.Request(id, CellOf(id), approach);
        return true;
    }

    /// <summary>
    /// Jí. Po <see cref="TicksPerMeal"/> ticích je hlad pryč a jde se dál pracovat.
    /// </summary>
    /// <remarks>
    /// Jídlo se ze skladu bere až tady, ne při vyražení. Jinak by ho člověk vzal a cestou
    /// by se ztratilo, kdyby ho po cestě něco zastavilo.
    /// </remarks>
    private void Eat(int id)
    {
        if (++_timer[id] < TicksPerMeal)
        {
            return;
        }

        _timer[id] = 0;

        if (Store.TryTakeFood(out _))
        {
            _hunger[id] = 0;
            MealsEaten++;
        }

        // I když mezitím jídlo někdo snědl, vrací se do práce. Zůstat stát u prázdného
        // skladu by znamenalo, že hlad člověka nezpomalí, ale úplně vypne.
        SetState(id, ColonistState.Idle);
    }

    private void DeliverPathAnswers(NavGraph graph, DigJobQueue jobs)
    {
        foreach (PathAnswer answer in _requests.Answers)
        {
            int id = answer.AgentId;
            if (id >= _count || !_alive[id] || _state[id] != ColonistState.WaitingForPath)
            {
                continue;
            }

            if (answer.Result != PathResult.Found)
            {
                // Ke skladu se dojít nedá? Nemá cenu se o to prát: vrátí se k práci a hlad
                // poroste dál. Zůstat stát a čekat na jídlo, které nedorazí, je zásek.
                if (_eatingPath[id])
                {
                    // KE SKLADU SE DOJÍT NEDÁ. Bez čekací lhůty se z toho stane smyčka:
                    // stav Idle vede rovnou zpátky do BeginJob, tam se hlad ani sklad
                    // nezměnily, takže se o cestu požádá znovu — a tak každý tik donekonečna.
                    // Kolonista přitom nikdy nesáhne po kopání a nikdy neusne, takže pár
                    // takových lidí trvale sežere rozpočet navigace (0,858 ms na hledání,
                    // osm hledání na tik). Kopání je proti témuž chráněné přes jobs.Defer.
                    _eatCooldown[id] = EatRetryTicks;
                    _eatingPath[id] = false;
                    SetState(id, ColonistState.Idle);
                    continue;
                }

                // Nese náklad a cesta nevede? Zpátky do Delivering, odkud to skončí ve skladu.
                // Bez toho by kolonista zůstal stát s rudou v ruce.
                if (_deliveringPath[id])
                {
                    _deliveringPath[id] = false;
                    SetState(id, ColonistState.Delivering);
                    continue;
                }

                // CESTA NEVEDE — ÚKOL SE ODLOŽÍ, NEVRÁTÍ. Kdyby se vrátil mezi volné, byl by
                // pořád nejbližší, kolonista by si ho vzal znovu a zacyklil by se. Odložený
                // se vrátí do hry, až se svět změní a cesta se možná otevře.
                if (_job[id] >= 0)
                {
                    jobs.Defer(_job[id]);
                }

                ClearJobState(id);
                continue;
            }

            StorePath(graph, id, answer.Path);

            // Přes SetState, ať existuje jediná cesta ke změně stavu. WaitingForPath ani
            // Moving se sice mezi volné nepočítají, takže by tady přímý zápis zrovna
            // neuškodil — ale výjimka „tady to nevadí" je přesně to, co se pak zkopíruje
            // na místo, kde vadí.
            SetState(id, ColonistState.Moving);
        }
    }

    /// <summary>
    /// Rozejde kolonistu bez práce po domovské oblasti.
    /// </summary>
    /// <remarks>
    /// <para><b>Bez hledání cesty.</b> Míří se rovnou k vybranému místu, kdežto jedno A* stojí
    /// naměřených 0,858 ms a do tiku se jich vejde osm. Kdyby si o cestu řeklo dvě stě
    /// zahálejících lidí, sežrala by potulka celý rozpočet navigace — a to všechno kvůli tomu,
    /// aby se někdo popošel o metr. O překážku se procházka zastaví a vybere se jiné místo;
    /// to je v pořádku, procházka není úkol.</para>
    ///
    /// <para><b>Domov je sklad, tedy radnice.</b> Ta se ukládá i načítá, takže po restartu
    /// vědí, kam patří. Bez radnice se netoulá nikdo: kolonie nemá střed a rozejít se do
    /// světa je horší než stát.</para>
    /// </remarks>
    /// <returns>Vydal se někam?</returns>
    private bool TryWander(NavGraph graph, int id)
    {
        if (!Store.HasCell)
        {
            return false;
        }

        if (_wanderPause[id] > 0)
        {
            _wanderPause[id]--;
            return false;
        }

        // Posuvný generátor se stavem na kolonistu: deterministický a bez alokace.
        uint seed = _wanderSeed[id];
        if (seed == 0)
        {
            seed = (uint)(id * 2654435761u) | 1u;
        }

        seed ^= seed << 13;
        seed ^= seed >> 17;
        seed ^= seed << 5;
        _wanderSeed[id] = seed;

        // CHODÍ SE ZA CÍLEM, NE NÁHODNÝM SMĚREM. Náhodná chůze vypadá bezcílně, protože
        // bezcílná je: kolonista se rozejde, po pár krocích si vybere jiný směr a kličkuje
        // sem a tam. Člověk jde NĚKAM — vybere si místo, dojde tam a chvíli postojí.
        int dosahX = (int)(seed % (uint)((HomeRadius * 2) + 1)) - HomeRadius;
        int dosahZ = (int)((seed >> 11) % (uint)((HomeRadius * 2) + 1)) - HomeRadius;

        Vector3i from = CellOf(id);
        var goal = new Vector3i(Store.Cell.X + dosahX, from.Y, Store.Cell.Z + dosahZ);

        // Vybrané místo musí být pochůzné, jinak by kolonista tlačil do zdi celých šest set
        // tiků. Zkouší se i patro nad a pod: domovská oblast bývá na svahu.
        if (!TryLandOn(graph, ref goal))
        {
            _wanderPause[id] = WanderPauseTicks;
            return false;
        }

        _wanderGoal[id] = goal;
        _wanderRemaining[id] = WanderGoalTicks;
        _timer[id] = 0;
        SetState(id, ColonistState.Wandering);
        return true;
    }

    /// <summary>Posadí cíl na nejbližší pochůzné patro. Jen o jedno nahoru a dolů.</summary>
    private static bool TryLandOn(NavGraph graph, ref Vector3i cell)
    {
        for (int step = 0; step <= 2; step++)
        {
            int dy = step == 2 ? -1 : step;
            var candidate = new Vector3i(cell.X, cell.Y + dy, cell.Z);

            if (graph.IsStandable(candidate))
            {
                cell = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Je buňka ještě doma?</summary>
    private bool IsHome(Vector3i cell)
    {
        Vector3i delta = cell - Store.Cell;
        return Math.Abs(delta.X) <= HomeRadius
            && Math.Abs(delta.Z) <= HomeRadius
            && Math.Abs(delta.Y) <= HomeRadius;
    }

    /// <summary>
    /// Popojde kus k cíli procházky.
    /// </summary>
    /// <remarks>
    /// <para><b>Postává se AŽ NA KONCI procházky, ne mezi kroky.</b> Pauza po každé buňce byla
    /// ta „figurka na šachovnici": kolonista se za ni stihl otočit a chůze z toho byla série
    /// krok-zastávka-otočka.</para>
    ///
    /// <para><b>Kdo vyjde z domova, se vrací.</b> Odstrčení od kolegy může vytlačit i za mez
    /// domovské oblasti; místo zákazu se prostě ukončí procházka a vybere se cíl znovu, tedy
    /// zase uvnitř domova. Zakázat pohyb ven by znamenalo, že se v davu u radnice někdo
    /// zasekne mezi zákazem a odstrkáváním.</para>
    /// </remarks>
    private void StepWander(NavGraph graph, int id)
    {
        bool arrived = MoveTowards(graph, id, ColonistBody.CentreOf(_wanderGoal[id]), ColonistBody.WanderSpeed);

        if (!arrived && --_wanderRemaining[id] > 0 && IsHome(CellOf(id)))
        {
            return;
        }

        _wanderRemaining[id] = 0;
        _wanderPause[id] = (int)(_wanderSeed[id] % WanderPauseTicks);
        SetState(id, ColonistState.Idle);
    }

    /// <summary>
    /// Uloží nalezenou cestu a rovnou ji zkrátí.
    /// </summary>
    /// <remarks>
    /// <b>Zkracuje se jednou, tady</b> — ne při každém kroku. A* dává lomenou čáru z pravých
    /// úhlů, protože povoluje jen čtyři vodorovné směry; kdo po ní půjde doslova, chodí do L
    /// i přes prázdnou pláň. Viz <see cref="PathShortening"/>.
    /// </remarks>
    private void StorePath(NavGraph graph, int id, IReadOnlyList<Vector3i> path)
    {
        int length = Math.Min(path.Count, MaxPathLength);
        int offset = id * MaxPathLength;
        for (int i = 0; i < length; i++)
        {
            _path[offset + i] = path[i];
        }

        _pathLength[id] = PathShortening.Shorten(graph, _path.AsSpan(offset, length), length);

        // Nultá buňka je tam, kde kolonista stojí — první krok je až ta další.
        _pathCursor[id] = 1;
    }

    private void Step(
        VoxelWorld world,
        BlockRegistry blocks,
        ushort water,
        NavGraph graph,
        DigJobQueue jobs,
        int id)
    {
        // PÁD PŘED VŠÍM OSTATNÍM. Když pod kolonistou někdo vykopal, musí spadnout dřív,
        // než začne cokoli dělat — jinak by kopal ze vzduchu.
        //
        // ALE PÁD NESMÍ POHYB ZASTAVIT. První verze tohohle bloku poslala každého, kdo není
        // na zemi, do TryFall a skončila tam — takže se kolonista ve vzduchu vodorovně
        // nehnul. U skoku to znamenalo odraz od zdi, dopad na totéž místo a skok znovu:
        // poskakování před překážkou donekonečna. Kdo letí, se hýbat MUSÍ, ať už nahoru
        // (skok přes zeď) nebo dolů (dokročení z okraje).
        //
        // Ti, kdo se v tomhle stavu nikam nechystají, dostanou gravitaci tady; ostatním ji
        // dá MoveTowards spolu s krokem, aby se obojí nepočítalo dvakrát za tik.
        // NEZNÁMÝ CHUNK NENÍ PRÁZDNO, viz ColonistBody.ApplyGravity. Kolonista, od kterého
        // hráč odletí, přijde o navigační mřížku — a kdyby se to bralo jako ztráta země,
        // propadl by světem jen proto, že se na něj nikdo nedívá.
        if (_state[id] is not (ColonistState.Moving or ColonistState.Wandering)
            && graph.IsKnown(CellOf(id))
            && !TickIdleGravity(world, blocks, water, graph, id))
        {
            // Ještě padá. Kopat ani odevzdávat se za letu nedá.
            return;
        }

        switch (_state[id])
        {
            case ColonistState.Idle:
                BeginJob(graph, jobs, id);
                break;

            case ColonistState.WaitingForPath:
                break;

            case ColonistState.Wandering:
                StepWander(graph, id);
                break;

            case ColonistState.Moving:
                Move(graph, jobs, id);
                break;

            case ColonistState.Digging:
                Dig(world, blocks, water, graph, jobs, id);
                break;

            case ColonistState.Delivering:
                Deliver(graph, id);
                break;

            case ColonistState.Eating:
                Eat(id);
                break;
        }
    }

    /// <summary>
    /// Uplatní tíži na kolonistu, který nikam nejde.
    /// </summary>
    /// <remarks>
    /// <para><b>Pád je spojitý, ne teleport na dno.</b> Dřív se hledala první pochůzná buňka
    /// dolů a kolonista se na ni přesunul jedním tikem — díra hluboká čtyřicet bloků se
    /// zdolala okamžitě. Teď platí tíž a padá se rychlostí, jakou padá hráč.</para>
    ///
    /// <para><b>Kdo spadl, přijde o rozdělanou práci.</b> Cesta z původní polohy už nevede
    /// a kopat se dá jen na dosah. Stav se přepne na Idle a o zbytek se postará
    /// <see cref="BeginJob"/> — ten umí spočítat, odkud se na blok dosáhne. Úkol si kolonista
    /// nechává.</para>
    /// </remarks>
    /// <returns>Stojí kolonista na zemi a smí dělat i něco jiného?</returns>
    private bool TickIdleGravity(
        VoxelWorld world,
        BlockRegistry blocks,
        ushort water,
        NavGraph graph,
        int id)
    {
        Vector3 position = _position[id];
        float velocityY = _velocityY[id];
        bool onGround = _onGround[id];

        // Pod světem není kam padat. Zůstat stát je lepší než propadnout.
        if (position.Y <= 0f)
        {
            _velocityY[id] = 0f;
            _onGround[id] = true;
            return true;
        }

        ColonistBody.ApplyGravity(graph, ref position, ref velocityY, ref onGround);

        _position[id] = position;
        _velocityY[id] = velocityY;
        _onGround[id] = onGround;

        if (!onGround)
        {
            _pathLength[id] = 0;
            _pathCursor[id] = 0;

            if (_state[id] is ColonistState.Digging)
            {
                // PŘES SetState, NIKDY PŘÍMO. Počítadlo volných lidí se vede při každé změně
                // stavu, takže přímý zápis ho rozváže — a „VOLNÝCH LIDI" // sekce 2 to číslo, na kterém stojí celá hra. Projevilo se to tím, že po pádu
                // vyšlo volných lidí MÍNUS JEDNA: přechod na Idle počítadlo nezvedl, ale
                // následný odchod z Idle ho snížil.
                SetState(id, ColonistState.Idle);
            }
        }

        _ = world;
        _ = blocks;
        _ = water;

        return onGround;
    }

    private void BeginJob(NavGraph graph, DigJobQueue jobs, int id)
    {
        // HLAD MÁ PŘEDNOST PŘED NOVOU PRACÍ, ne před rozdělanou. Kdo už úkol drží, dokopne ho
        // a najíst se jde až pak — jinak by se rozdělaná práce donekonečna vracela do fronty.
        if (_job[id] < 0 && TryGoEat(graph, id))
        {
            return;
        }

        // Úkol už můžeme držet — třeba po pádu, kdy se jen zahodila cesta. Pak se nebere
        // nový, jen se hledá cesta znovu z nové polohy.
        int job = _job[id];
        if (job < 0 && !jobs.TryClaimNearest(CellOf(id), id, out job))
        {
            // NENÍ PRÁCE, ALE NENÍ DŮVOD ZKAMENĚT. Dřív tady kolonista usnul a stál na místě,
            // dokud mu hráč něco nezadal — a přesně tím vypadala celá kolonie mrtvě.
            // Kdo nemá radnici, nemá ani domov, a pak spát může.
            // KDO MÁ DOMOV, NESPÍ. Potulka se střídá s postáváním, a pauza se musí mít jak
            // odpočítat — uspaný kolonista ji neodtiká a probudí ho až zadaná práce. Prvně
            // to takhle bylo a projevilo se to tím, že se každý pohnul právě jednou.
            // Zahálející člověk stojí pár celočíselných operací za tik; tick p99 drží
            // hledání cest, ne tohle.
            if (!TryWander(graph, id) && !Store.HasCell)
            {
                _active.Sleep(id);
            }

            return;
        }

        Vector3i? reach = AdjacentStandable(graph, jobs.TargetOf(job));
        if (reach is null)
        {
            // Není odkud na blok dosáhnout. Nemá smysl na to posílat hledání cesty —
            // a hlavně nesmí se to vzít za cíl vlastní buňku, protože pak by kolonista
            // "vykopal" blok na druhém konci mapy.
            jobs.Defer(job);
            _active.Sleep(id);
            return;
        }

        _job[id] = job;
        SetState(id, ColonistState.WaitingForPath);
        _requests.Request(id, CellOf(id), reach.Value);
    }

    /// <summary>
    /// Odkud se dá na blok dosáhnout. Kopat se musí ze SOUSEDNÍ buňky, ne z té, kde blok je —
    /// v té se stát nedá, je plná.
    /// </summary>
    private static Vector3i? AdjacentStandable(NavGraph graph, Vector3i target)
    {
        // Nejdřív buňka přímo nad blokem: kope se shora, což je nejběžnější případ.
        var above = new Vector3i(target.X, target.Y + 1, target.Z);
        if (graph.IsStandable(above))
        {
            return above;
        }

        foreach ((int dx, int dz) in PathFinder.HorizontalDirections)
        {
            foreach (int dy in new[] { 0, 1, -1 })
            {
                var side = new Vector3i(target.X + dx, target.Y + dy, target.Z + dz);
                if (graph.IsStandable(side))
                {
                    return side;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Jde po cestě. Spojitě, ne skokem o buňku.
    /// </summary>
    /// <remarks>
    /// <para><b>Tady je ta půlka zadání, která byla nejvíc vidět.</b> Dřív se čekalo třináct
    /// tiků a pak se poloha přepsala na další buňku; plynulost dělala až interpolace při
    /// vykreslování. Teď se každý tik ujde kus rychlostí <see cref="ColonistBody.WalkSpeed"/>
    /// a bod cesty se považuje za dosažený, jakmile je blíž než <see cref="ColonistBody.ArrivalRadius"/>.</para>
    ///
    /// <para><b>Hlad zpomaluje chůzi, ne prodlužuje krok.</b> S pevným počtem tiků na buňku
    /// se to dělalo násobkem doby; se spojitou polohou je to prostě nižší rychlost. Výsledek
    /// je stejný a nekmitá se u toho.</para>
    /// </remarks>
    private void Move(NavGraph graph, DigJobQueue jobs, int id)
    {
        if (_pathCursor[id] >= _pathLength[id])
        {
            _timer[id] = 0;

            if (_eatingPath[id])
            {
                // Došel ke skladu. Jídlo se z něj bere až po dojedení, viz Eat.
                _eatingPath[id] = false;
                _timer[id] = 0;
                SetState(id, ColonistState.Eating);
                return;
            }

            if (_deliveringPath[id])
            {
                // Došel k pásu nebo stroji. Co se tam nevejde, jde do skladu — náklad se
                // nikdy nesmí ztratit.
                _deliveringPath[id] = false;

                if (Delivery is not null && Delivery.TryDeliver(_carrying[id], _deliverTarget[id]))
                {
                    DeliveredToFactory++;
                    _carrying[id] = BlockRegistry.Air;
                    SetState(id, ColonistState.Idle);
                }
                else
                {
                    SetState(id, ColonistState.Delivering);
                }

                return;
            }

            // Došel. Blok se kope ze sousední buňky, takže tady začíná kopání.
            SetState(id, _job[id] >= 0 ? ColonistState.Digging : ColonistState.Idle);
            return;
        }

        Vector3i next = _path[(id * MaxPathLength) + _pathCursor[id]];

        // Cesta mohla zastarat: někdo mezitím postavil zeď.
        if (!graph.IsStandable(next))
        {
            // Cesta zastarala: někdo mezitím vykopal nebo postavil. Úkol se VRÁTÍ do fronty,
            // ne jen zahodí — jinak zůstane navěky zabraný a nikdo si ho nevezme.
            AbandonJob(id, jobs);
            return;
        }

        // HLADOVÝ CHODÍ POMALEJI. Stejný tlak jako u kopání, jen vyjádřený rychlostí.
        float speed = _hunger[id] >= StarvingAt
            ? ColonistBody.WalkSpeed / StarvingSlowdown
            : ColonistBody.WalkSpeed;

        if (MoveTowards(graph, id, ColonistBody.CentreOf(next), speed))
        {
            _pathCursor[id]++;
        }
    }

    private void Dig(
        VoxelWorld world,
        BlockRegistry blocks,
        ushort water,
        NavGraph graph,
        DigJobQueue jobs,
        int id)
    {
        int job = _job[id];
        if (job < 0)
        {
            SetState(id, ColonistState.Idle);
            return;
        }

        Vector3i target = jobs.TargetOf(job);
        ushort block = world.GetBlock(target.X, target.Y, target.Z);
        if (block == BlockRegistry.Air)
        {
            // Někdo to vykopal dřív. Úkol je bezpředmětný.
            jobs.Cancel(job);
            _job[id] = -1;
            SetState(id, ColonistState.Idle);
            return;
        }

        // NA DÁLKU SE NEKOPE. Bez téhle kontroly by stačilo, aby cesta skončila jinde než
        // u bloku, a kolonista by ho vykopal přes celou mapu.
        Vector3i cell = CellOf(id);
        int reach = Math.Abs(cell.X - target.X) + Math.Abs(cell.Y - target.Y) + Math.Abs(cell.Z - target.Z);
        if (reach > 2)
        {
            jobs.Defer(job);
            _job[id] = -1;
            SetState(id, ColonistState.Idle);
            return;
        }

        if (++_timer[id] < WorkTicks(id, TicksPerDig))
        {
            return;
        }

        _timer[id] = 0;
        world.SetBlock(target.X, target.Y, target.Z, BlockRegistry.Air);

        // NAVIGACE MUSÍ VĚDĚT HNED. Bez toho by kolonisté dál chodili po buňkách, které
        // po vykopání zmizely — a hledalo by se to v pathfindingu.
        graph.OnBlockChanged(world, blocks, water, target.X, target.Y, target.Z);

        jobs.Complete(job);

        // Vykopáním se mohla otevřít cesta k něčemu, co bylo předtím nedosažitelné.
        jobs.ReviveDeferred();

        _job[id] = -1;
        _carrying[id] = block;

        // CESTU K ODEVZDÁNÍ SE HLEDAT TEĎ NEDÁ. Kolonista často stojí právě na tom bloku,
        // který vykopal, takže jeho buňka přestala být pochůzná a hledání by z ní vyšlo jako
        // neplatný začátek. Pád se vyřeší na začátku příštího tiku a teprve pak má smysl
        // se ptát — o to se stará stav Delivering.
        SetState(id, ColonistState.Delivering);
    }

    private void Deliver(NavGraph graph, int id)
    {
        if (++_timer[id] < WorkTicks(id, TicksPerStep))
        {
            return;
        }

        _timer[id] = 0;

        // NEJDŘÍV FABRIKA, teprve pak sklad. Tohle je ten důvod, proč se vyplatí postavit
        // pás: bez něj se ruda nosí po svých, s ním ji kolonista jen odloží kousek vedle.
        if (Delivery is not null
            && Delivery.TryFindTarget(_carrying[id], CellOf(id), out Vector3i dropOff))
        {
            // Stojí u toho? Odevzdat rovnou.
            Vector3i delta = dropOff - CellOf(id);
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) + Math.Abs(delta.Z) <= 2
                && Delivery.TryDeliver(_carrying[id], dropOff))
            {
                DeliveredToFactory++;
                _carrying[id] = BlockRegistry.Air;
                SetState(id, ColonistState.Idle);
                return;
            }

            if (AdjacentStandable(graph, dropOff) is { } approach && approach != CellOf(id))
            {
                _deliverTarget[id] = dropOff;
                _deliveringPath[id] = true;
                SetState(id, ColonistState.WaitingForPath);
                _requests.Request(id, CellOf(id), approach);
                return;
            }
        }

        // Není kam, nebo se tam nedá dojít: materiál jde do skladu. Ztratit se nesmí nikdy.
        //
        // DO SKLADU JDE DRUH, NE JEN POČET. Dřív se zvětšilo číslo a co kolonista nesl, se
        // ztratilo — pak se z toho nedalo nic vzít, protože nikdo nevěděl, co v něm leží.
        Store.Add(_carrying[id]);
        _carrying[id] = BlockRegistry.Air;
        SetState(id, ColonistState.Idle);
    }

    /// <summary>
    /// Kolonista se pustí úkolu a ten se vrátí mezi volné.
    /// </summary>
    /// <remarks>
    /// <b>Fronta se to MUSÍ dozvědět.</b> Dřív se čistil jen stav kolonisty a úkol zůstal
    /// ve frontě navěky jako zabraný — nikdo si ho pak nemohl vzít a kolonie se zastavila
    /// s prací, na kterou nikdo nešel. Projevilo se to až se třemi kolonisty, protože sám
    /// si člověk cestu nerozkope.
    /// </remarks>
    private void AbandonJob(int id, DigJobQueue jobs)
    {
        if (_job[id] >= 0)
        {
            jobs.Release(_job[id]);
        }

        ClearJobState(id);
    }

    /// <summary>Vyčistí jen stav kolonisty. Volá se tam, kde už frontu obsloužil volající.</summary>
    private void ClearJobState(int id)
    {
        _pathLength[id] = 0;
        _pathCursor[id] = 0;
        _deliveringPath[id] = false;
        _eatingPath[id] = false;
        _job[id] = -1;
        SetState(id, ColonistState.Idle);
    }

    /// <summary>
    /// Kolik tiků trvá krok práce. Vyhladovělému člověku dvakrát tolik.
    /// </summary>
    /// <remarks>
    /// <para><b>Tohle je ten tlak ze zadání.</b> Když jídlo není, hlad roste dál a práce se
    /// zpomalí — a musí to být poznat na číslech v panelu, ne jen v kódu. Proto se násobí
    /// doba každého kroku, kopnutí i odevzdání: výsledek je vidět na počtu vykopaných bloků.</para>
    ///
    /// <para><b>Násobek, ne odečet.</b> Celočíselný násobek drží determinismus (pravidlo 6.6)
    /// a nemůže vyjít nula ani záporné číslo, kdyby někdo prahy přenastavil.</para>
    /// </remarks>
    private int WorkTicks(int id, int ticks) =>
        _hunger[id] >= StarvingAt ? ticks * StarvingSlowdown : ticks;

    /// <summary>Změní stav a udrží počítadlo volných lidí v souladu.</summary>
    /// <summary>Počítá se tenhle stav mezi volné lidi?</summary>
    private static bool IsFree(ColonistState state) =>
        state is ColonistState.Idle or ColonistState.Wandering;

    private void SetState(int id, ColonistState next)
    {
        if (_state[id] == next)
        {
            return;
        }

        // POTULKA JE VOLNÝ ČLOVĚK. „VOLNÝCH LIDÍ" je číslo, na kterém
        // stojí celá hra — a kdo se jen prochází, protože nemá co dělat, je volný úplně stejně
        // jako ten, kdo stojí. Kdyby se počítal jako zaneprázdněný, spadlo by to číslo tím,
        // že jsem kolonisty rozchodil.
        if (IsFree(_state[id]))
        {
            IdleCount--;
        }

        if (IsFree(next))
        {
            IdleCount++;
        }

        _state[id] = next;
    }

    /// <summary>Vrátí zabrané úkoly zpátky do fronty. Volá se při zrušení označené oblasti.</summary>
    public void AbandonAll(DigJobQueue jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        for (int id = 0; id < _count; id++)
        {
            if (_job[id] >= 0)
            {
                jobs.Release(_job[id]);
            }

            ClearJobState(id);
            _active.Wake(id);
        }
    }
}
