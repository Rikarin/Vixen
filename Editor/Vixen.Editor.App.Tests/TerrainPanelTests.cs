// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Terrain;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 31's Part 2, end to end: the four panels, and what they say when there is nothing.</summary>
/// <remarks>
///     ⚠ <b>The empty states are half of these tests, on purpose.</b> Every one of these panels is
///     first met by somebody with no terrain, no volume and no roads — and doc 20's first bar is that
///     a verb which is not reachable right now is <i>visibly</i> not reachable rather than absent. A
///     panel that draws nothing in that state is the failure this suite exists to catch.
/// </remarks>
public class TerrainPanelTests {
    [Fact]
    public void The_editor_registers_the_five_panels() {
        using var fixture = EditorSession.Start();

        foreach (var id in (string[]) [
            TerrainMode.PanelId,
            FoliageMode.PanelId,
            "terrain.growth",
            "terrain.splines",
            "terrain.grass"
        ]) {
            Assert.Contains(fixture.Shell.Workspace.Panels, panel => panel.Id == id);
        }
    }

    /// <summary>Entering a mode opens its panel, and leaving it closes it again.</summary>
    /// <remarks>
    ///     ⚠ <b>A settings panel left behind for a tool nobody is holding</b> is what
    ///     <c>IEditorMode.Panel</c> exists to prevent, and the mode names the panel precisely so the
    ///     mode machinery rather than the panel is what opens it.
    /// </remarks>
    [Fact]
    public void Entering_terrain_mode_opens_the_terrain_panel_and_leaving_closes_it() {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.Shell.Modes.Activate(TerrainMode.ModeId));
        fixture.Frames(2);

        Assert.True(fixture.Shell.Workspace.IsOpen(TerrainMode.PanelId));

        Assert.True(fixture.Shell.Modes.Activate("select"));
        fixture.Frames(2);

        Assert.False(fixture.Shell.Workspace.IsOpen(TerrainMode.PanelId));
    }

    [Fact]
    public void And_the_foliage_one() {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.Shell.Modes.Activate(FoliageMode.ModeId));
        fixture.Frames(2);

        Assert.True(fixture.Shell.Workspace.IsOpen(FoliageMode.PanelId));
    }

    /// <summary>The create form shows what it would cost, while it is being filled in.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the dialog where a person accidentally asks for eight gigabytes.</b> The
    ///     numbers are derived and labelled as derived, and they are on screen rather than behind a
    ///     "recompute" button — which would put the eight-gigabyte answer one press away from the
    ///     person about to press Create.
    /// </remarks>
    [Fact]
    public void The_create_form_shows_its_derived_numbers() {
        using var fixture = EditorSession.Start();

        fixture.Open(TerrainMode.PanelId);

        var text = Text(fixture.Panel(TerrainMode.PanelId));

        Assert.Contains(text, line => line.Contains("Extent", StringComparison.Ordinal));
        Assert.Contains(text, line => line.Contains("(derived)", StringComparison.Ordinal));
        Assert.Contains(text, line => line.Contains("MB", StringComparison.Ordinal));
    }

    /// <summary>With no terrain, the layer stack says so rather than being blank.</summary>
    [Fact]
    public void With_no_terrain_the_layer_stack_says_what_to_do() {
        using var fixture = EditorSession.Start();

        fixture.Open(TerrainMode.PanelId);

        Assert.Contains(Text(fixture.Panel(TerrainMode.PanelId)), line => line.Contains("No terrain", StringComparison.Ordinal));
    }

    /// <summary>And with no palette, the foliage panel does.</summary>
    /// <remarks>
    ///     ⚠ <b>Entering a mode that does nothing and says nothing is the state every one of these
    ///     toolsets puts a new user in.</b> <c>FoliageEdit.Refusal</c> is what the panel says instead.
    /// </remarks>
    [Fact]
    public void With_no_palette_the_foliage_panel_says_what_to_do() {
        using var fixture = EditorSession.Start();

        fixture.Open(FoliageMode.PanelId);

        Assert.Contains(Text(fixture.Panel(FoliageMode.PanelId)), line => line.Contains("No types", StringComparison.Ordinal));
    }

    /// <summary>The growth panel says it has not run rather than showing a forest of zeroes.</summary>
    [Fact]
    public void The_growth_panel_says_it_has_not_run_yet() {
        using var fixture = EditorSession.Start();

        fixture.Open("terrain.growth");

        Assert.Contains(Text(fixture.Panel("terrain.growth")), line => line.Contains("Not run yet", StringComparison.Ordinal));
    }

    /// <summary>Growing with no volume is a notification, not an exception and not silence.</summary>
    /// <remarks>
    ///     ⚠ <b>This runs from a button's own handler, where an exception takes the frame down with
    ///     the scene unsaved</b> — and a growth run with no volume is an ordinary thing to attempt.
    /// </remarks>
    [Fact]
    public void Growing_with_nothing_to_grow_into_is_a_notification() {
        using var fixture = EditorSession.Start();

        fixture.Open("terrain.growth");

        var grow = Button(fixture.Panel("terrain.growth"), "Grow");

        Assert.NotNull(grow);

        grow.Activate();
        fixture.Frames(2);

        // The editor is still up, and the panel still reads as never having run.
        Assert.Contains(Text(fixture.Panel("terrain.growth")), line => line.Contains("Not run yet", StringComparison.Ordinal));
    }

    /// <summary>The spline panel's reach is derived from the profile and moves with it.</summary>
    /// <remarks>
    ///     ⚠ <b>The wider side, not the average.</b> A rect sized to the mean leaves the wide side's
    ///     last metres unrebuilt, which draws as a seam that only appears on one side of the road.
    /// </remarks>
    [Fact]
    public void The_spline_panel_derives_the_roads_reach() {
        using var fixture = EditorSession.Start();

        fixture.Open("terrain.splines");

        Assert.Contains(
            Text(fixture.Panel("terrain.splines")),
            line => line.Contains("Reach from centre line", StringComparison.Ordinal)
        );

        // And it says the curve editor is owed rather than pretending a panel can author one.
        Assert.Contains(
            Text(fixture.Panel("terrain.splines")),
            line => line.Contains("not yet on the gizmo", StringComparison.Ordinal)
        );
    }

    /// <summary>Regenerating with no terrain is a notification too.</summary>
    [Fact]
    public void Regenerating_roads_with_no_terrain_is_a_notification() {
        using var fixture = EditorSession.Start();

        fixture.Open("terrain.splines");

        var regenerate = Button(fixture.Panel("terrain.splines"), "Regenerate roads");

        Assert.NotNull(regenerate);

        regenerate.Activate();
        fixture.Frames(2);
    }

    /// <summary>Grass is a rule rather than a mode, and the rule's cost is on the panel.</summary>
    /// <remarks>
    ///     ⚠ <b>[§ D8]: a person does not paint grass, they change the rule that produces it.</b> So
    ///     there is no fifth viewport mode — there would be nothing to click on — and the only place
    ///     the memory is visible is this readout. A rule whose cost is invisible is one somebody turns
    ///     up until the editor stops.
    /// </remarks>
    [Fact]
    public void The_grass_panel_shows_what_the_rule_costs() {
        using var fixture = EditorSession.Start();

        fixture.Open("terrain.grass");

        var text = Text(fixture.Panel("terrain.grass"));

        Assert.Contains(text, line => line.Contains("Ring size", StringComparison.Ordinal));
        Assert.Contains(text, line => line.Contains("MB (derived)", StringComparison.Ordinal));

        // And it is not a mode: nothing in the mode list claims it.
        Assert.DoesNotContain("grass", fixture.Shell.Modes.Modes.Select(mode => mode.Id));
    }

    /// <summary>A switch off and a density of zero produce the same field, and it says which.</summary>
    [Fact]
    public void The_grass_settings_distinguish_off_from_empty() {
        var settings = new TerrainGrassSettings { Density = 0.5f };

        Assert.Equal(0.5f, settings.DensityScale);

        settings.IsEnabled = false;

        // ⚠ A switch rather than a density of zero. Zero still dispatches the scatter for every
        // resident cell and rejects every candidate, which costs the whole pass to draw nothing.
        Assert.Equal(0f, settings.DensityScale);
    }

    /// <summary>The wind's strength scales and its direction replaces.</summary>
    /// <remarks>
    ///     ⚠ <b>Two fields on one level blowing in different directions is not weather, it is a
    ///     bug</b> — but two fields fluttering differently in the same wind is exactly what an author
    ///     authored them for.
    /// </remarks>
    [Fact]
    public void The_wind_scales_the_strength_and_replaces_the_direction() {
        var settings = new TerrainGrassSettings { Wind = 0.5f, Bearing = 90f };
        var authored = Vixen.Foliage.GrassWind.Breeze;

        var applied = settings.Apply(authored);

        Assert.Equal(authored.Strength * 0.5f, applied.Strength, 5);
        Assert.Equal(authored.Flutter, applied.Flutter);

        // Ninety degrees clockwise from north is +X.
        Assert.Equal(1f, applied.Direction.X, 5);
        Assert.Equal(0f, applied.Direction.Y, 5);
    }

    /// <summary>And a range doubled is four times the ring.</summary>
    [Fact]
    public void Doubling_the_range_quadruples_the_ring() {
        var near = new TerrainGrassSettings { Range = 160f };
        var far = new TerrainGrassSettings { Range = 320f };

        var ratio = (double)far.RingBytes() / near.RingBytes();

        Assert.InRange(ratio, 3.5, 4.5);
    }

    /// <summary>The settings objects behind the panels agree with the kernels they feed.</summary>
    [Fact]
    public void The_growth_settings_become_what_the_kernel_takes() {
        var settings = new TerrainGrowthSettings { SizeX = 120f, SizeZ = 80f, Steps = 5, MaxPlants = 900 };

        settings.CentreOn(new(50f, -20f));

        var kernel = settings.ToSettings();

        Assert.Equal(5, kernel.Steps);
        Assert.Equal(900, kernel.MaxPlants);
        Assert.Equal(120f * 80f, kernel.Area);

        // Centred means the corner is half a size back from the point a person aimed at.
        Assert.Equal(50f - 60f, kernel.Origin.X);
        Assert.Equal(-20f - 40f, kernel.Origin.Y);
        Assert.True(kernel.Contains(new(50f, -20f)));

        Assert.Null(settings.Validate());
        Assert.True(settings.IsValid);
    }

    [Fact]
    public void And_the_spline_settings_do() {
        var settings = new TerrainSplineSettings { HalfWidth = 3f, FalloffLeft = 9f, FalloffRight = 2f, Strength = 2f };

        var profile = settings.ToProfile();

        Assert.Equal(3f, profile.HalfWidth);

        // Clamped, because a strength above one is a road that overshoots the height it is levelling to.
        Assert.Equal(1f, profile.Strength);

        // The wider side, which is what a rebuild rect has to be sized from.
        Assert.Equal(3f + 9f, settings.Reach);

        Assert.False(settings.Paints);
        Assert.False(settings.Places);
    }

    static Button? Button(UiElement root, string label) =>
        Descendants(root).OfType<Button>().FirstOrDefault(button => button.Label == label);

    static List<string> Text(UiElement root) =>
        [.. Descendants(root).Select(element => element.Text ?? string.Empty).Where(text => text.Length > 0)];

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
