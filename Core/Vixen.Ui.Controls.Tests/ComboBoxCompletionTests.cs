// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>ComboBox.Completes</c>: doc 49's named gap "<c>ComboBox</c> has no completion" (#666).</summary>
/// <remarks>
///     Driven with real text-input events and keys through the document, because the completion is a
///     reaction to a keystroke that has already been handled — a test that called a method directly
///     would pass with the handler never registered.
/// </remarks>
public class ComboBoxCompletionTests {
    static (ControlFixture Fixture, ComboBox Box) Focused(bool completes = true) {
        var fixture = new ControlFixture();
        var box = fixture.Add<ComboBox>();

        box.AddOption("Standard");
        box.AddOption("Subsurface");
        box.AddOption("Unlit");
        box.Completes = completes;

        fixture.Document.Focus(box.Editor);
        fixture.Update();

        return (fixture, box);
    }

    [Fact]
    public void Typing_the_start_of_a_suggestion_fills_in_the_rest_selected() {
        var (fixture, box) = Focused();
        using var _ = fixture;

        fixture.TypeText("S");

        // The first suggestion in order that the text begins, its remainder appended and selected.
        Assert.Equal("Standard", box.Value);
        Assert.Equal("tandard", box.Editor.SelectedText);

        // The next character replaces the selected part rather than following it.
        fixture.TypeText("u");

        Assert.Equal("Subsurface", box.Value);
        Assert.Equal("bsurface", box.Editor.SelectedText);
    }

    [Fact]
    public void Matching_ignores_case_and_keeps_what_was_typed() {
        var (fixture, box) = Focused();
        using var _ = fixture;

        fixture.TypeText("U");

        Assert.Equal("Unlit", box.Value);

        fixture.TypeText("NL");

        // The typed prefix stays as typed; only the remainder is the suggestion's.
        Assert.Equal("UNLit", box.Value);
        Assert.Equal("it", box.Editor.SelectedText);
    }

    [Fact]
    public void Backspace_removes_the_completion_and_does_not_bring_it_back() {
        var (fixture, box) = Focused();
        using var _ = fixture;

        fixture.TypeText("st");
        Assert.Equal("standard", box.Value);

        fixture.Type(InputKey.Backspace);

        Assert.Equal("st", box.Value);
        Assert.False(box.Editor.HasSelection);
    }

    [Fact]
    public void Text_no_suggestion_begins_is_left_alone() {
        var (fixture, box) = Focused();
        using var _ = fixture;

        fixture.TypeText("x");

        Assert.Equal("x", box.Value);
        Assert.False(box.Editor.HasSelection);
    }

    [Fact]
    public void A_combo_box_that_does_not_ask_for_completion_gets_none() {
        var (fixture, box) = Focused(completes: false);
        using var _ = fixture;

        fixture.TypeText("s");

        Assert.Equal("s", box.Value);
    }
}
