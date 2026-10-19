// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><see cref="UiDocument.Unload" />: a sheet taken back out of the cascade (#1484).</summary>
/// <remarks>
///     <para>
///         The oracle is a colour only the removed sheet sets. Red is the override's and nothing else
///         in the fixture has a red channel, so "no command is red" is exactly "no rule of that sheet
///         reached the picture" — and blue is what the remaining sheet says the cards are.
///     </para>
///     <para>
///         ⚠ <b>The retained half runs with the Debug verifier on.</b> A replay of a subtree drawn
///         while the sheet was in would be a stale picture, and in a Debug build every replay is
///         compared with a fresh emission and throws on a difference — so the test asserts that frames
///         after the removal did replay, which is what puts the verifier in the path at all.
///     </para>
/// </remarks>
public class SheetRemovalTests {
    const int Cards = 6;

    const string Base = """
        root { width: 400px; height: 300px; }
        panel { display: flex; flex-wrap: wrap; width: 400px; height: 100px; }
        card { width: 40px; height: 20px; margin: 2px; background-color: #0000ff; }
        """;

    const string Override = ".hot { background-color: #ff0000; }";

    static UiDocument Board(bool withOverride, out int overrideSheet) {
        var document = new UiDocument(400f, 300f);
        document.Load(Base);
        overrideSheet = withOverride ? document.Load(Override) : -1;

        var panel = document.Root.Add("panel");

        for (var index = 0; index < Cards; index++) {
            var card = panel.Add("card");

            if (index % 2 == 0) {
                card.AddClass("hot");
            }
        }

        return document;
    }

    static void Frame(UiDocument document) {
        document.Update();
        document.Draw();
    }

    static int Red(UiDocument document) => document.Drawing.Commands.Count(command => command.Color.R > 0.5f);

    static int Blue(UiDocument document) => document.Drawing.Commands.Count(command => command.Color.B > 0.5f);

    [Fact]
    public void Removing_a_sheet_restyles_every_element_it_styled_from_what_remains() {
        using var document = Board(withOverride: true, out var sheet);

        Frame(document);

        // The other half: the override is in the picture before it is removed.
        Assert.Equal(Cards / 2, Red(document));
        Assert.Equal(Cards / 2, Blue(document));

        document.Unload(sheet);
        Frame(document);

        Assert.Equal(0, Red(document));
        Assert.Equal(Cards, Blue(document));
    }

    [Fact]
    public void A_removed_sheet_leaves_the_loaded_count_where_it_was_and_keeps_its_slot() {
        using var document = Board(withOverride: false, out _);
        var styles = document.Styles;
        var loaded = styles.LoadedSheetCount;
        var slots = styles.SheetCount;

        var sheet = document.Load(Override);
        Assert.Equal(loaded + 1, styles.LoadedSheetCount);

        document.Unload(sheet);

        Assert.Equal(loaded, styles.LoadedSheetCount);
        Assert.Equal(slots + 1, styles.SheetCount);
        Assert.True(styles.IsRemoved(sheet));
        Assert.Equal(string.Empty, styles.SheetText(sheet));
    }

    /// <summary>The handle problem #1484 names: an index taken after the removed one still names its own sheet.</summary>
    [Fact]
    public void An_index_handed_out_after_the_removed_sheet_still_names_the_sheet_it_named() {
        using var document = Board(withOverride: true, out var removed);
        var later = document.Load("card { height: 20px; }");

        document.Unload(removed);
        document.ReloadStyles(later, ".hot { background-color: #ff0000; }");
        Frame(document);

        Assert.Equal(".hot { background-color: #ff0000; }", document.Styles.SheetText(later));
        Assert.Equal(Cards / 2, Red(document));
    }

    [Fact]
    public void A_sheet_cannot_be_removed_twice_or_replaced_once_removed() {
        using var document = Board(withOverride: true, out var sheet);

        document.Unload(sheet);

        Assert.Throws<InvalidOperationException>(() => document.Unload(sheet));
        Assert.Throws<InvalidOperationException>(() => document.ReloadStyles(sheet, Override));
    }

    [Fact]
    public void A_retained_board_replays_after_a_removal_without_the_removed_rules_under_the_verifier() {
        using var document = Board(withOverride: true, out var sheet);
        document.RetainsDrawing = true;
        Assert.True(document.VerifiesRetainedDrawing, "this suite is meant to run as a Debug build");

        Frame(document);
        Frame(document);
        Assert.Equal(Cards / 2, Red(document));

        document.Unload(sheet);
        Frame(document);

        var replayed = document.Diagnostics.DrawCommandsReplayed;
        Frame(document);

        // ⚠ The half that makes the verifier part of the test: a frame after the removal replayed,
        // so every replayed subtree was checked against a fresh emission of itself.
        Assert.True(document.Diagnostics.DrawCommandsReplayed > replayed, "nothing replayed after the removal");
        Assert.Equal(0, Red(document));
        Assert.Equal(Cards, Blue(document));
    }

    /// <summary>The Release half: with the verifier off, the retained picture is the one a board that never had the sheet draws.</summary>
    [Fact]
    public void A_retained_board_after_a_removal_draws_what_a_board_that_never_had_the_sheet_draws() {
        using var retained = Board(withOverride: true, out var sheet);
        using var never = Board(withOverride: false, out _);
        retained.RetainsDrawing = true;
        retained.VerifiesRetainedDrawing = false;
        never.RetainsDrawing = false;

        Frame(retained);
        Frame(retained);

        retained.Unload(sheet);

        for (var frame = 0; frame < 3; frame++) {
            Frame(retained);
            Frame(never);

            Assert.True(
                retained.Drawing.Commands.SequenceEqual(never.Drawing.Commands),
                $"frame {frame} after the removal differs from a board that never had the sheet"
            );
        }
    }
}
