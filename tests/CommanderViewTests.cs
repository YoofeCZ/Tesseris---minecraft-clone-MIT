using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Režim velitele (T6) — přepínání pohledu a označování oblastí.
/// </summary>
/// <remarks>
/// pohled shora je <b>záměrně omezený</b>. Není to „totéž, jen shora" —
/// napětí mezi režimy je hook hry. Proto se omezení testují jako pravidlo, ne jako detail UI.
/// </remarks>
public sealed class CommanderViewTests
{
    // ================= PŘEPÍNÁNÍ =================

    [Fact]
    public void Game_starts_in_first_person()
    {
        var view = new CommanderView();

        Assert.False(view.Commanding);
        Assert.Equal(0f, view.Blend);
        Assert.True(view.Abilities.CanDigByHand);
    }

    [Fact]
    public void Transition_is_smooth_not_a_cut()
    {
        var view = new CommanderView();
        view.Toggle();

        // Po jednom tiku 60 Hz je obraz teprve na cestě, ne na místě.
        view.Update(1f / 60f);

        Assert.True(view.Blend > 0f, "Přechod se vůbec nerozjel.");
        Assert.True(view.Blend < 1f, "Přechod byl střih, ne přechod.");
        Assert.True(view.InTransition);
    }

    [Fact]
    public void Transition_finishes_in_the_declared_time()
    {
        var view = new CommanderView();
        view.Toggle();

        for (int tick = 0; tick < 60; tick++)
        {
            view.Update(1f / 60f);
        }

        // Za 0,45 s musí být hotovo; 60 tiků je vteřina s rezervou.
        Assert.Equal(1f, view.Blend);
        Assert.False(view.InTransition);
    }

    [Fact]
    public void Toggling_back_mid_transition_returns_to_first_person()
    {
        var view = new CommanderView();
        view.Toggle();
        view.Update(0.2f);
        float midway = view.Blend;

        view.Toggle();
        view.Update(0.2f);

        Assert.True(view.Blend < midway, "Přechod se po přepnutí nevrací.");

        for (int tick = 0; tick < 60; tick++)
        {
            view.Update(1f / 60f);
        }

        Assert.Equal(0f, view.Blend);
        Assert.False(view.Commanding);
    }

    // ================= OMEZENÍ REŽIMŮ =================

    /// <summary>
    /// Jádro sekce 3: velitel nemá ruce. Kdyby mohl kopat, jsou to dvě hry místo dvou režimů.
    /// </summary>
    [Fact]
    public void Commander_has_no_hands()
    {
        ModeAbilities commander = CommanderView.Commander;

        Assert.False(commander.CanDigByHand);
        Assert.False(commander.CanPlaceBlocks);
        Assert.False(commander.CanFight);
        Assert.False(commander.CanCarry);
    }

    [Fact]
    public void Commander_can_plan()
    {
        ModeAbilities commander = CommanderView.Commander;

        Assert.True(commander.CanMarkArea);
        Assert.True(commander.CanPlaceMachines);
        Assert.True(commander.CanAssignWork);
        Assert.True(commander.CanSeeStockpiles);
    }

    [Fact]
    public void First_person_cannot_command()
    {
        ModeAbilities first = CommanderView.FirstPerson;

        Assert.False(first.CanMarkArea);
        Assert.False(first.CanAssignWork);
        Assert.False(first.CanSeeStockpiles);
        Assert.True(first.CanDigByHand);
    }

    /// <summary>
    /// Během přechodu neplatí ani jedna sada. Jinak by šlo rychlým přepínáním obejít,
    /// že velitel nemá ruce.
    /// </summary>
    [Fact]
    public void Nothing_is_allowed_mid_transition()
    {
        var view = new CommanderView();
        view.Toggle();
        view.Update(0.1f);

        ModeAbilities abilities = view.Abilities;

        Assert.False(abilities.CanDigByHand);
        Assert.False(abilities.CanMarkArea);
        Assert.False(abilities.CanFight);
        Assert.False(abilities.CanPlaceMachines);
    }

    // ================= ŘEZ PATREM =================

    [Fact]
    public void Slice_moves_up_and_down_within_limits()
    {
        var view = new CommanderView();
        view.SetSlice(10, 0, 20);

        view.MoveSlice(3, 0, 20);
        Assert.Equal(13, view.SliceY);

        view.MoveSlice(-20, 0, 20);
        Assert.Equal(0, view.SliceY);

        view.MoveSlice(100, 0, 20);
        Assert.Equal(20, view.SliceY);
    }

    [Fact]
    public void Camera_rises_above_the_slice_when_commanding()
    {
        var view = new CommanderView();
        view.SetSlice(40, 0, 100);
        view.Toggle();

        for (int tick = 0; tick < 60; tick++)
        {
            view.Update(1f / 60f);
        }

        var eye = new Vector3(10f, 5f, 20f);
        Vector3 camera = view.CameraPosition(eye);

        Assert.Equal(40f + CommanderView.CameraHeight, camera.Y, 2);

        // ŠIKMO, ne kolmo: kamera se odtáhne dozadu proti směru pohledu, takže vodorovně
        // nad středem záběru už nestojí. Výška nad řezem ale zůstává stejná.
        Vector3 focus = new(eye.X, 40f, eye.Z);
        Vector3 toFocus = focus - camera;
        Assert.True(toFocus.Length > 1f, $"Kamera se neodtáhla: {camera}.");

        // A dívá se pořád na totéž místo jako dřív, jen zešikma.
        Vector3 look = view.ViewDirection(new Vector3(0f, 0f, -1f));
        Assert.Equal(1f, Vector3.Dot(Vector3.Normalize(toFocus), look), 3);
    }

    /// <summary>
    /// Sklon 55 stupňů, ne kolmice. Kolmý pohled nemá ve voxelovém světě hloubku a je to
    /// degenerovaný případ pro LookAt.
    /// </summary>
    [Fact]
    public void Commander_view_is_tilted_not_straight_down()
    {
        var view = new CommanderView();
        view.Toggle();
        for (int tick = 0; tick < 60; tick++)
        {
            view.Update(1f / 60f);
        }

        Vector3 look = view.ViewDirection(new Vector3(0f, 0f, -1f));

        Assert.Equal(-MathF.Sin(MathHelper.DegreesToRadians(CommanderView.PitchDegrees)), look.Y, 3);
        Assert.True(look.Y < -0.5f, $"Pohled je moc naplocho: {look}.");
        Assert.True(look.Y > -0.99f, $"Pohled je pořád skoro kolmý: {look}.");

        // Vodorovná složka musí být nenulová, jinak by to byla zpátky kolmice.
        Assert.True(MathF.Sqrt((look.X * look.X) + (look.Z * look.Z)) > 0.3f, $"Bez hloubky: {look}.");
    }

    /// <summary>Q a E otáčí po čtvrtinách kruhu a po čtyřech krocích je pohled zpátky.</summary>
    [Fact]
    public void Rotation_steps_through_four_fixed_headings()
    {
        var view = new CommanderView();
        Assert.Equal(0, view.Facing);

        var seen = new List<Vector3>();
        for (int step = 0; step < 4; step++)
        {
            seen.Add(view.CommanderForward());
            view.RotateRight();
        }

        Assert.Equal(0, view.Facing);

        // Čtyři různé směry, každé dva po sobě jdoucí na sebe kolmé ve vodorovné rovině.
        for (int i = 0; i < 4; i++)
        {
            var a = new Vector2(seen[i].X, seen[i].Z);
            var b = new Vector2(seen[(i + 1) % 4].X, seen[(i + 1) % 4].Z);
            Assert.Equal(0f, Vector2.Dot(a, b), 3);

            // Sklon je u všech stran stejný.
            Assert.Equal(seen[0].Y, seen[i].Y, 4);
        }

        view.RotateLeft();
        Assert.Equal(3, view.Facing);
    }

    /// <summary>Otočení musí posunout i polohu kamery, jinak by se odtahovala pořád stejně.</summary>
    [Fact]
    public void Rotating_moves_the_camera_to_the_other_side()
    {
        var view = new CommanderView();
        view.SetSlice(20, 0, 100);
        view.Toggle();
        for (int tick = 0; tick < 60; tick++)
        {
            view.Update(1f / 60f);
        }

        var eye = new Vector3(0f, 20f, 0f);
        Vector3 before = view.CameraPosition(eye);

        view.RotateRight();
        view.RotateRight();
        Vector3 opposite = view.CameraPosition(eye);

        // O 180 stupňů: vodorovný odstup se překlopil, výška zůstala.
        Assert.Equal(before.Y, opposite.Y, 3);
        Assert.Equal(-(before.X - eye.X), opposite.X - eye.X, 3);
        Assert.Equal(-(before.Z - eye.Z), opposite.Z - eye.Z, 3);
    }

    [Fact]
    public void Camera_stays_at_the_eyes_in_first_person()
    {
        var view = new CommanderView();
        var eye = new Vector3(10f, 5f, 20f);

        Assert.Equal(eye, view.CameraPosition(eye));
    }

    [Fact]
    public void View_direction_never_becomes_degenerate()
    {
        var view = new CommanderView();
        view.Toggle();

        for (int tick = 0; tick <= 60; tick++)
        {
            view.Update(1f / 60f);
            Vector3 direction = view.ViewDirection(new Vector3(0f, -1f, 0f));

            Assert.True(
                direction.Length > 0.5f,
                $"Směr pohledu zdegeneroval v tiku {tick}: {direction}.");
        }
    }

    // ================= VÝBĚR OBLASTI =================

    [Fact]
    public void Selection_starts_empty()
    {
        var selection = new AreaSelection();

        Assert.False(selection.Active);
        Assert.Equal(0, selection.Volume);
    }

    [Fact]
    public void Dragging_a_box_gives_normalized_corners()
    {
        var selection = new AreaSelection();

        // Schválně odzadu dopředu, aby se ukázalo, že se rohy srovnají.
        selection.Begin(new Vector3i(10, 5, 10));
        selection.DragTo(new Vector3i(4, 2, 7));

        Assert.Equal(new Vector3i(4, 2, 7), selection.Minimum);
        Assert.Equal(new Vector3i(10, 5, 10), selection.Maximum);
        Assert.Equal(7 * 4 * 4, selection.Volume);
    }

    [Fact]
    public void Single_cell_selection_has_volume_one()
    {
        var selection = new AreaSelection();
        selection.Begin(new Vector3i(3, 3, 3));

        Assert.Equal(1, selection.Volume);
        Assert.True(selection.WithinLimit);
    }

    [Fact]
    public void Oversized_selection_is_refused()
    {
        var selection = new AreaSelection();
        selection.Begin(Vector3i.Zero);
        selection.DragTo(new Vector3i(200, 200, 200));

        Assert.False(selection.WithinLimit);
        Assert.False(selection.TryComplete(out _, out _));

        // A zůstane rozdělaný, aby ho šlo zmenšit místo začínání znovu.
        Assert.True(selection.Active);
    }

    [Fact]
    public void Completing_a_selection_closes_it()
    {
        var selection = new AreaSelection();
        selection.Begin(new Vector3i(1, 1, 1));
        selection.DragTo(new Vector3i(3, 1, 3));

        Assert.True(selection.TryComplete(out Vector3i min, out Vector3i max));

        Assert.Equal(new Vector3i(1, 1, 1), min);
        Assert.Equal(new Vector3i(3, 1, 3), max);
        Assert.False(selection.Active);
    }

    [Fact]
    public void Cancelled_selection_produces_nothing()
    {
        var selection = new AreaSelection();
        selection.Begin(Vector3i.Zero);
        selection.DragTo(new Vector3i(5, 0, 5));
        selection.Cancel();

        Assert.False(selection.TryComplete(out _, out _));
    }

    /// <summary>
    /// Celý řetěz T6 → T3: velitel označí kus stěny a vznikne z toho fronta úkolů.
    /// </summary>
    [Fact]
    public void Marked_area_turns_into_dig_jobs()
    {
        var world = new VoxelWorld(BlockRegistry.Create(
        [
            new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
        ]));

        ushort stone = world.Registry.IndexOf("test:stone");
        for (int x = 4; x < 8; x++)
        {
            for (int z = 4; z < 6; z++)
            {
                world.SetBlock(x, 2, z, stone);
            }
        }

        var view = new CommanderView();
        view.Toggle();
        for (int tick = 0; tick < 60; tick++)
        {
            view.Update(1f / 60f);
        }

        Assert.True(view.Abilities.CanMarkArea);

        var selection = new AreaSelection();
        selection.Begin(new Vector3i(4, 2, 4));
        selection.DragTo(new Vector3i(7, 2, 5));
        Assert.True(selection.TryComplete(out Vector3i min, out Vector3i max));

        var jobs = new DigJobQueue();
        int added = jobs.MarkArea(world, world.Registry, min, max);

        Assert.Equal(8, added);
        Assert.Equal(8, jobs.OpenCount);
    }
}
