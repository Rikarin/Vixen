// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 45 § step 4 on the three surfaces neither presenter builds.</summary>
/// <remarks>
///     <para>
///         The viewport's radial menu, the viewport's context list and the preferences window's
///         command toggles each computed <c>CanExecute</c> by hand, with a <c>command is null</c>
///         branch beside it. ⚠ <b>Losing that branch is the point rather than the saving:</b> an id
///         the registry does not know resolves to nothing, and nothing responding is what greys a
///         bound control — so an unregistered id and a disabled command become one state instead of
///         two spellings of it.
///     </para>
///     <para>
///         ⚠ <b>And the context list was the reason to look.</b> Five of its seven entries named ids
///         nothing registers — <c>entity.duplicate</c> and four others, where the verbs are
///         <c>edit.duplicate</c> and friends — and the "only for commands that exist" guard turned
///         that into a shorter menu with nothing said anywhere. Two of seven lines had been offered
///         since the surface was written.
///     </para>
/// </remarks>
public class SceneMenuBindingTests {
    [Fact]
    public void The_scene_context_list_offers_every_line_it_names() {
        using var fixture = EditorSession.Start();

        fixture.Settle();

        var offered = fixture.Extensions.All<SceneMenuItem>()
            .Where(static item => (item.Surface & SceneMenuSurface.Context) != 0)
            .ToList();

        Assert.Equal(7, offered.Count);

        foreach (var item in offered) {
            Assert.True(
                fixture.Shell.Commands.TryGet(item.CommandId, out _),
                item.CommandId + " is offered in the scene context menu and is registered nowhere"
            );
        }
    }

    /// <summary>An entry whose command is out of scope opens greyed, through the binding.</summary>
    /// <remarks>
    ///     ⚠ <b>An id nothing registers, deliberately.</b> That is the case the deleted
    ///     <c>command is null</c> branch used to cover, and the claim is that the binding covers it
    ///     without being told: <c>CommandRoute</c> resolves it to nothing, and a bound item with
    ///     nothing responding is disabled.
    /// </remarks>
    [Fact]
    public void A_context_entry_whose_command_nothing_answers_is_greyed_by_the_binding() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Settle();

        using var registration = fixture.Extensions.Add(
            new SceneMenuItem("test.nothing-answers-this", SceneMenuSurface.Context) { Label = "Absent Verb" }
        );

        fixture.Run("scene.context-menu").Settle();

        var item = Assert.Single(Items(fixture), candidate => candidate.Label == "Absent Verb");

        Assert.True(item.Disabled, "an entry whose id nothing responds to was offered as pressable");
        Assert.Equal("test.nothing-answers-this", item.Command);
    }

    /// <summary>The radial menu greys the same two cases, though it cannot be bound.</summary>
    /// <remarks>
    ///     ⚠ <b>The one surface of the three with no test until this one, and the one most able to
    ///     drift.</b> A <c>RadialItem</c> cannot take <c>ButtonBase.Command</c> — the hold-and-release
    ///     gesture runs a wedge through <c>RadialMenu.Accept</c> without a click, so a bound wedge would
    ///     run twice on the click path — and so <c>SceneMenus.OpenRadialMenu</c> copies what the binding
    ///     does by hand. A copy is exactly what goes stale when the binding changes; this pins that it
    ///     still greys an id nothing answers <i>and</i> a command that says no, and leaves an answering
    ///     one pressable.
    /// </remarks>
    [Fact]
    public void A_radial_wedge_greys_when_nothing_answers_or_its_command_says_no() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Scene.Selection.Clear();
        fixture.Settle();

        var refusing = true;

        fixture.Shell.Commands.Add(
            new EditorCommand("test.radial-refuses", new StringId("test.radial-refuses", "Refusing Wedge"), static () => { }) {
                Enablement = () => !refusing
            }
        );

        using var absent = fixture.Extensions.Add(
            new SceneMenuItem("test.nothing-answers-this", SceneMenuSurface.Radial) { Label = "Absent Wedge" }
        );

        using var refused = fixture.Extensions.Add(
            new SceneMenuItem("test.radial-refuses", SceneMenuSurface.Radial) { Label = "Refusing Wedge" }
        );

        fixture.Run("scene.radial-menu").Settle();

        Assert.True(Wedge(fixture, "Absent Wedge").Disabled, "a wedge whose id nothing answers was pressable");
        Assert.True(Wedge(fixture, "Refusing Wedge").Disabled, "a wedge whose command said no was pressable");

        Radial(fixture).Close(CloseReason.Code);
        refusing = false;
        fixture.Run("scene.radial-menu").Settle();

        Assert.False(Wedge(fixture, "Refusing Wedge").Disabled, "a wedge whose command said yes stayed greyed");
        Assert.True(Wedge(fixture, "Absent Wedge").Disabled);
    }

    static RadialMenu Radial(EditorSession fixture) =>
        Descendants(fixture.Document.Root).OfType<RadialMenu>().FirstOrDefault(static menu => menu.IsOpen)
        ?? throw fixture.Fail("the radial menu did not open");

    static RadialItem Wedge(EditorSession fixture, string label) =>
        Assert.Single(Radial(fixture).Items, candidate => candidate.Label == label);

    /// <summary>And one whose command says no right now is greyed for the ordinary reason.</summary>
    [Fact]
    public void A_context_entry_whose_command_is_disabled_greys_and_un_greys_with_the_selection() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Scene.Selection.Clear();
        fixture.Settle();

        fixture.Run("scene.context-menu").Settle();

        var line = fixture.Shell.Commands["entity.snap-to-floor"]!.Title.Text;

        Assert.True(
            Assert.Single(Items(fixture), candidate => candidate.Label == line).Disabled,
            "Snap To Floor was pressable with nothing selected"
        );

        fixture.Scene.Selection.Set([fixture.Scene.Add("Menu Test Crate", default)]);
        fixture.Settle();

        fixture.Run("scene.context-menu").Settle();

        Assert.False(
            Assert.Single(Items(fixture), candidate => candidate.Label == line).Disabled,
            "Snap To Floor stayed greyed with something selected"
        );
    }

    /// <summary>The preferences window's toggles are bound too, and still snap back on a refusal.</summary>
    [Fact]
    public void A_preference_toggle_is_bound_and_shows_what_the_command_says() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");

        var view = fixture.Control<SettingsView>("preferences");

        Assert.True(view.Select("scene-view"));
        fixture.Settle();

        var toggles = Descendants(view.Pane).OfType<ToggleButton>()
            .Where(static toggle => toggle.Command is { Length: > 0 })
            .ToList();

        Assert.NotEmpty(toggles);

        foreach (var toggle in toggles) {
            var command = fixture.Shell.Commands[toggle.Command!];

            Assert.NotNull(command);

            // ⚠ Whatever the command says, from the binding rather than from a line in the panel.
            Assert.Equal(!fixture.Shell.Commands.CanExecute(command), toggle.Disabled);
            Assert.Equal(command.IsChecked, toggle.IsChecked);
        }

        // And pressing one leaves the toggle showing what the command says afterwards, which is what
        // makes a command that refused not appear to have worked.
        var pressed = toggles.First(static toggle => !toggle.Disabled);
        var behind = fixture.Shell.Commands[pressed.Command!]!;

        fixture.Click(pressed);
        fixture.Settle();

        Assert.Equal(behind.IsChecked, pressed.IsChecked);
    }

    static IEnumerable<MenuItem> Items(EditorSession fixture) =>
        Descendants(fixture.Document.Root).OfType<ContextMenu>().SelectMany(static menu => menu.Items);

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
