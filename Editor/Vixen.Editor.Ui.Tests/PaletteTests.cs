// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The matcher, and the palette over it.</summary>
public class PaletteTests {
    static StringId Title(string text) => new("test." + text, text);

    [Theory]
    [InlineData("rl", "Reset Layout")]
    [InlineData("opj", "Open Project…")]
    [InlineData("save", "Save All")]
    [InlineData("", "Anything")]
    public void An_ordered_subsequence_matches(string query, string candidate) =>
        Assert.True(FuzzyMatcher.Matches(query, candidate));

    [Theory]
    [InlineData("lr", "Reset Layout")]
    [InlineData("savez", "Save")]
    public void Letters_out_of_order_or_absent_do_not(string query, string candidate) =>
        Assert.False(FuzzyMatcher.Matches(query, candidate));

    [Fact]
    public void The_shorter_of_two_equal_matches_wins() {
        // Both match `save` at a word start throughout, so without the length penalty the one the
        // user meant is decided by whatever order the registry was in.
        Assert.True(FuzzyMatcher.Score("save", "Save") > FuzzyMatcher.Score("save", "Save Scene As…"));
    }

    [Fact]
    public void A_run_beats_the_same_letters_spread_out() =>
        Assert.True(FuzzyMatcher.Score("sav", "Save") > FuzzyMatcher.Score("sav", "Show Alpha Values"));

    [Fact]
    public void The_palette_ranks_across_every_source() {
        using var shell = new EditorShell(900f, 600f);

        shell.Commands.Add("file.save", Title("Save"), () => { });
        shell.Commands.Add("file.save-all", Title("Save All"), () => { });

        shell.Palette.AddSource(
            new DelegatePaletteSource("Asset", () => ["Materials/Water.vxmat", "Scenes/Sandbox.vxscene"], _ => { })
        );

        shell.Palette.OpenPalette();
        shell.Palette.Field.Value = "save";
        shell.Palette.Refresh();

        Assert.Equal("Save", shell.Palette.Results[0].Title);
        Assert.Equal("Save All", shell.Palette.Results[1].Title);

        // `Scenes/Sandbox.vxscene` really does contain s-a-v-e in order, so it matches and belongs
        // in the list — below both commands, which is the whole point of scoring rather than
        // filtering. A palette that dropped it would be one that could not find an asset by the
        // letters somebody remembered.
        Assert.Contains(shell.Palette.Results, item => item.Category == "Asset");
        Assert.True(shell.Palette.Results[0].Score > shell.Palette.Results[^1].Score);
    }

    [Fact]
    public void Choosing_a_result_closes_the_palette_before_it_runs() {
        using var shell = new EditorShell(900f, 600f);

        var openWhileRunning = true;
        shell.Commands.Add("test.run", Title("Run It"), () => openWhileRunning = shell.Palette.IsOpen);

        shell.Palette.OpenPalette();
        shell.Palette.Field.Value = "run it";
        shell.Palette.Refresh();

        Assert.True(shell.Palette.Accept());

        // A command that opens a dialog would otherwise be covered by the palette that started it.
        Assert.False(openWhileRunning);
        Assert.False(shell.Palette.IsOpen);
    }

    [Fact]
    public void The_highlight_wraps_and_the_field_keeps_the_focus() {
        using var shell = new EditorShell(900f, 600f);

        shell.Commands.Add("a.one", Title("Alpha"), () => { });
        shell.Commands.Add("a.two", Title("Beta"), () => { });

        shell.Palette.OpenPalette();

        Assert.Equal(0, shell.Palette.Highlighted);

        shell.Palette.Move(-1);
        Assert.Equal(shell.Palette.Results.Count - 1, shell.Palette.Highlighted);

        shell.Palette.Move(1);
        Assert.Equal(0, shell.Palette.Highlighted);

        // A palette where Down moved the focus into the list is one where the next letter typed
        // goes nowhere.
        Assert.True(shell.Palette.Field.IsFocused);
    }

    [Fact]
    public void The_command_that_opens_the_palette_is_not_in_it() {
        using var shell = new EditorShell(900f, 600f);

        shell.Palette.OpenPalette();
        shell.Palette.Field.Value = "palette";
        shell.Palette.Refresh();

        Assert.DoesNotContain(shell.Palette.Results, item => item.Title == EditorStrings.CommandPalette.Source);
    }

    [Fact]
    public void A_disabled_command_is_shown_and_refuses() {
        var registry = new CommandRegistry();
        var ran = 0;

        registry.Add(new EditorCommand("edit.undo", Title("Undo"), () => ran++) { Enablement = () => false });

        var results = new List<PaletteItem>();
        new CommandPaletteSource(registry, new KeyMap()).Search("undo", results, 10);

        // Hiding it would make "there is nothing to undo" and "the editor has forgotten how"
        // indistinguishable.
        var item = Assert.Single(results);
        item.Run();

        Assert.Equal(0, ran);
    }

    [Fact]
    public void A_row_click_runs_what_the_row_is_showing() {
        using var shell = new EditorShell(900f, 600f);

        var ran = 0;
        shell.Commands.Add("test.run", Title("Run It"), () => ran++);

        shell.Palette.OpenPalette();
        shell.Palette.Field.Value = "run";
        shell.Palette.Refresh();
        shell.Document.Update();

        var row = Assert.IsType<PaletteRow>(Find(shell.Palette.List, 0));
        row.Raise(new ClickEvent { Device = ActivationDevice.Pointer });

        Assert.Equal(1, ran);
    }

    static UiElement? Find(UiElement parent, int index) => index < parent.Children.Count ? parent.Children[index] : null;
}
