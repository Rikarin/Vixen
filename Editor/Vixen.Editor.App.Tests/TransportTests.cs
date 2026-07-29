// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The play controls, which are read at a glance rather than looked at.</summary>
/// <remarks>
///     ⚠ <b>"Am I in play mode" has to be answerable without reading anything.</b> Doc 20 calls the
///     transport the most-clicked control in either reference editor; four identical grey glyphs
///     answer neither that question nor "which of these is Stop". The colour is the answer, and the
///     filled state is what makes the running editor unmistakable.
/// </remarks>
public class TransportTests {
    [Theory]
    [InlineData("play.play", "transport-play")]
    [InlineData("play.pause", "transport-pause")]
    [InlineData("play.step", "transport-step")]
    [InlineData("play.stop", "transport-stop")]
    public void Each_transport_verb_carries_an_icon_and_a_colour(string id, string className) {
        using var fixture = EditorSession.Start();

        var command = fixture.Shell.Commands[id];

        Assert.NotNull(command);
        Assert.NotNull(command.Icon);
        Assert.Equal(className, command.ClassName);
    }

    [Fact]
    public void The_play_button_fills_while_the_editor_is_playing() {
        using var fixture = EditorSession.Start();

        var button = Button(fixture, "transport-play");

        // Off: a green triangle on the surface.
        Assert.False(button.State.HasFlag(ElementState.Checked));

        Assert.True(fixture.Shell.Commands.Execute("play.play"));
        fixture.Frames(2);

        // On: `:checked` is what the theme fills, so the button is green and the glyph is white.
        Assert.True(button.State.HasFlag(ElementState.Checked));

        Assert.True(fixture.Shell.Commands.Execute("play.stop"));
        fixture.Frames(2);

        Assert.False(button.State.HasFlag(ElementState.Checked));
    }

    [Fact]
    public void Stop_and_step_are_disabled_until_there_is_something_to_stop() {
        using var fixture = EditorSession.Start();

        var stop = Button(fixture, "transport-stop");
        var step = Button(fixture, "transport-step");

        Assert.True(stop.Disabled);
        Assert.True(step.Disabled);

        Assert.True(fixture.Shell.Commands.Execute("play.play"));
        fixture.Frames(2);

        Assert.False(stop.Disabled);

        // Step is for a paused editor: stepping a running one is a frame nobody sees.
        Assert.True(step.Disabled);

        Assert.True(fixture.Shell.Commands.Execute("play.pause"));
        fixture.Frames(2);

        Assert.False(step.Disabled);
    }

    /// <summary>
    ///     ⚠ The four are one box, and it is a different argument from the gizmo modes'. Those are
    ///     one <i>choice</i> and the box says so; a transport is one <i>control</i> — a single object
    ///     in every editor, every player and every tape machine there has ever been — and four
    ///     buttons with gaps between them read as four unrelated verbs that happen to be adjacent.
    /// </summary>
    [Fact]
    public void The_transport_is_drawn_as_one_control() {
        using var fixture = EditorSession.Start();

        var strip = fixture.Shell.Toolbar.Strip;

        var group = Assert.Single(
            strip.Children.Where(child => child.Tag == "toolbar-group"),
            box => Descendants(box).Any(child => child.HasClass("transport-play"))
        );

        // All four in it and nothing else: a transport with the gizmo modes boxed in beside it would
        // be a control claiming that Rotate and Stop are the same kind of thing.
        var buttons = Descendants(group).OfType<ButtonBase>().ToList();

        Assert.Equal(4, buttons.Count);

        foreach (var name in new[] { "transport-play", "transport-pause", "transport-step", "transport-stop" }) {
            Assert.Contains(buttons, button => button.HasClass(name));
        }
    }

    static ButtonBase Button(EditorSession fixture, string className) =>
        Descendants(fixture.Shell.Toolbar.Strip)
            .OfType<ButtonBase>()
            .FirstOrDefault(button => button.HasClass(className))
        ?? throw new InvalidOperationException($"the toolbar has no '{className}' button");

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
