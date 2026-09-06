using Tesseris.Engine.Core;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Seznam tikaných entit (pravidlo 6.4). Chyba tady se projeví jako stroj, který přestane
/// pracovat, nebo naopak jako stroj tikaný dvakrát — obojí se ve hře hledá špatně.
/// </summary>
public sealed class UpdateListTests
{
    [Fact]
    public void New_list_is_empty()
    {
        var list = new UpdateList();

        Assert.Equal(0, list.Count);
        Assert.True(list.Active.IsEmpty);
        Assert.False(list.IsAwake(0));
    }

    [Fact]
    public void Woken_entity_appears_among_active()
    {
        var list = new UpdateList();

        Assert.True(list.Wake(7));

        Assert.Equal(1, list.Count);
        Assert.True(list.IsAwake(7));
        Assert.Equal(7, list.Active[0]);
    }

    [Fact]
    public void Waking_twice_does_not_duplicate()
    {
        var list = new UpdateList();

        Assert.True(list.Wake(3));
        Assert.False(list.Wake(3));

        Assert.Equal(1, list.Count);
    }

    [Fact]
    public void Sleeping_removes_the_entity()
    {
        var list = new UpdateList();
        list.Wake(3);

        Assert.True(list.Sleep(3));

        Assert.Equal(0, list.Count);
        Assert.False(list.IsAwake(3));
    }

    [Fact]
    public void Sleeping_an_absent_entity_is_harmless()
    {
        var list = new UpdateList();

        Assert.False(list.Sleep(99));
        Assert.False(list.Sleep(3));
        Assert.Equal(0, list.Count);
    }

    /// <summary>
    /// Odebrání prohodí poslední prvek na uvolněné místo. Kdyby se u přesunutého prvku
    /// neopravil záznam v řídkém poli, další Sleep by odebral někoho jiného.
    /// </summary>
    [Fact]
    public void Swap_remove_keeps_every_remaining_entity_findable()
    {
        var list = new UpdateList();
        for (int id = 0; id < 5; id++)
        {
            list.Wake(id);
        }

        list.Sleep(1);

        Assert.Equal(4, list.Count);
        foreach (int id in new[] { 0, 2, 3, 4 })
        {
            Assert.True(list.IsAwake(id));
        }

        Assert.False(list.IsAwake(1));

        // A ještě jednou, ať se ověří i opravený index přesunutého prvku.
        list.Sleep(4);
        Assert.Equal(3, list.Count);
        Assert.False(list.IsAwake(4));
        Assert.True(list.IsAwake(0));
        Assert.True(list.IsAwake(2));
        Assert.True(list.IsAwake(3));
    }

    /// <summary>
    /// Doporučený způsob průchodu: odzadu. Stroj, který se během vlastního tiku uspí,
    /// nesmí způsobit, že se na někoho jiného nedostane.
    /// </summary>
    [Fact]
    public void Backward_iteration_visits_everyone_even_when_they_sleep_themselves()
    {
        var list = new UpdateList();
        for (int id = 0; id < 10; id++)
        {
            list.Wake(id);
        }

        var visited = new List<int>();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            int id = list.Active[i];
            visited.Add(id);
            list.Sleep(id);
        }

        Assert.Equal(10, visited.Count);
        Assert.Equal(Enumerable.Range(0, 10).OrderByDescending(v => v), visited);
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void Entity_woken_during_a_tick_waits_for_the_next_one()
    {
        var list = new UpdateList();
        list.Wake(0);

        var visited = new List<int>();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            int id = list.Active[i];
            visited.Add(id);
            if (id == 0)
            {
                list.Wake(1);
            }
        }

        // Probuzený soused přijde na řadu až příští tik — jinak by šlo jedním tikem
        // protáhnout řetěz strojů přes celou továrnu.
        Assert.Equal(new[] { 0 }, visited);
        Assert.True(list.IsAwake(1));
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void Sparse_storage_grows_for_high_identifiers()
    {
        var list = new UpdateList(initialCapacity: 4);

        list.Wake(1000);

        Assert.True(list.IsAwake(1000));
        Assert.Equal(1, list.Count);
        Assert.Equal(1000, list.Active[0]);
    }

    [Fact]
    public void Dense_storage_grows_past_initial_capacity()
    {
        var list = new UpdateList(initialCapacity: 2);

        for (int id = 0; id < 100; id++)
        {
            list.Wake(id);
        }

        Assert.Equal(100, list.Count);
        for (int id = 0; id < 100; id++)
        {
            Assert.True(list.IsAwake(id));
        }
    }

    [Fact]
    public void Clear_puts_everyone_to_sleep()
    {
        var list = new UpdateList();
        for (int id = 0; id < 20; id++)
        {
            list.Wake(id);
        }

        list.Clear();

        Assert.Equal(0, list.Count);
        for (int id = 0; id < 20; id++)
        {
            Assert.False(list.IsAwake(id));
        }

        // A po vyprázdnění se musí dát zase probouzet.
        Assert.True(list.Wake(5));
        Assert.Equal(1, list.Count);
    }

    [Fact]
    public void Wake_and_sleep_survive_heavy_churn()
    {
        var list = new UpdateList(initialCapacity: 8);
        var expected = new HashSet<int>();
        var random = new Random(20260816);

        for (int step = 0; step < 20_000; step++)
        {
            int id = random.Next(0, 500);
            if (random.Next(2) == 0)
            {
                Assert.Equal(expected.Add(id), list.Wake(id));
            }
            else
            {
                Assert.Equal(expected.Remove(id), list.Sleep(id));
            }
        }

        Assert.Equal(expected.Count, list.Count);
        foreach (int id in expected)
        {
            Assert.True(list.IsAwake(id));
        }

        // A žádný duplikát: každý aktivní identifikátor je v hustém poli právě jednou.
        Assert.Equal(list.Count, list.Active.ToArray().Distinct().Count());
    }

    [Fact]
    public void Negative_identifiers_are_rejected()
    {
        var list = new UpdateList();

        Assert.Throws<ArgumentOutOfRangeException>(() => list.Wake(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Sleep(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.IsAwake(-1));
    }
}
