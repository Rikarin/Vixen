// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;
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
        using var fixture = new EditorFixture();

        var command = fixture.Editor.Shell.Commands[id];

        Assert.NotNull(command);
        Assert.NotNull(command.Icon);
        Assert.Equal(className, command.ClassName);
    }

    [Fact]
    public void The_play_button_fills_while_the_editor_is_playing() {
        using var fixture = new EditorFixture();

        var button = Button(fixture, "transport-play");

        // Off: a green triangle on the surface.
        Assert.False(button.State.HasFlag(ElementState.Checked));

        Assert.True(fixture.Editor.Shell.Commands.Execute("play.play"));
        fixture.Frames(2);

        // On: `:checked` is what the theme fills, so the button is green and the glyph is white.
        Assert.True(button.State.HasFlag(ElementState.Checked));

        Assert.True(fixture.Editor.Shell.Commands.Execute("play.stop"));
        fixture.Frames(2);

        Assert.False(button.State.HasFlag(ElementState.Checked));
    }

    [Fact]
    public void Stop_and_step_are_disabled_until_there_is_something_to_stop() {
        using var fixture = new EditorFixture();

        var stop = Button(fixture, "transport-stop");
        var step = Button(fixture, "transport-step");

        Assert.True(stop.Disabled);
        Assert.True(step.Disabled);

        Assert.True(fixture.Editor.Shell.Commands.Execute("play.play"));
        fixture.Frames(2);

        Assert.False(stop.Disabled);

        // Step is for a paused editor: stepping a running one is a frame nobody sees.
        Assert.True(step.Disabled);

        Assert.True(fixture.Editor.Shell.Commands.Execute("play.pause"));
        fixture.Frames(2);

        Assert.False(step.Disabled);
    }

    /// <summary>
    ///     ⚠ Four buttons and not a segmented group. Translate/Rotate/Scale are boxed because they
    ///     are one choice; the transport is two toggles and two actions, and boxing it would claim an
    ///     exclusivity it does not have.
    /// </summary>
    [Fact]
    public void The_transport_is_not_drawn_as_one_choice() {
        using var fixture = new EditorFixture();

        var strip = fixture.Editor.Shell.Toolbar.Strip;

        foreach (var group in strip.Children.Where(child => child.Tag == "toolbar-group")) {
            Assert.DoesNotContain(Descendants(group), child => child.HasClass("transport-play"));
        }
    }

    static ButtonBase Button(EditorFixture fixture, string className) =>
        Descendants(fixture.Editor.Shell.Toolbar.Strip)
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
