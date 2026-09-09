// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What a family of ids is, and the two ways it is allowed to fail.</summary>
/// <remarks>
///     The property that matters is the one the type exists for: every member reaches
///     <see cref="Strings.Template" />. An id built at a call site did not, which is what made a
///     whole editor toolset untranslatable while every test about it passed.
/// </remarks>
public class StringFamilyTests {
    static StringFamily Commands { get; } = new(
        "editor.command.",
        [
            new("water.finish", "Finish Water Body"),
            new("water.cancel", "Cancel Water Draw")
        ]
    );

    /// <summary>An id is the prefix and the key, which is the concatenation the call sites wrote.</summary>
    [Fact]
    public void A_member_is_the_prefix_and_the_key() {
        Assert.Equal("editor.command.water.finish", Commands["water.finish"].Id);
        Assert.Equal("Finish Water Body", Commands["water.finish"].Source);
    }

    /// <summary>
    ///     The whole point: a family's members go into a translator's template.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion that was impossible before.</b> The same two strings written as
    ///     <c>new StringId("editor.command." + id, label)</c> at a registration are in no
    ///     <c>All</c> list, so the template below would have had nothing in it and no test anywhere
    ///     would have said so.
    /// </remarks>
    [Fact]
    public void Every_member_reaches_a_template() {
        var template = Strings.Template("cs", Commands.All);

        // ⚠ The count is asserted as well as the two lookups. A template built from an empty family
        // would satisfy two `Assert.Null`s and nothing else, which is the vacuous shape this
        // repository keeps finding in loops that assert inside themselves.
        Assert.Equal(2, template.Count);
        Assert.Equal("Finish Water Body", template.Find("editor.command.water.finish"));
        Assert.Equal("Cancel Water Draw", template.Find("editor.command.water.cancel"));
    }

    /// <summary>Declaration order, because that is the order a translator reads them in.</summary>
    [Fact]
    public void Members_keep_their_declaration_order() =>
        Assert.Equal(
            ["editor.command.water.finish", "editor.command.water.cancel"],
            Commands.All.Select(member => member.Id)
        );

    /// <summary><see cref="StringFamily.Covers" /> is the question the indexer answers by throwing.</summary>
    [Fact]
    public void Covers_answers_for_a_member_and_against_a_stranger() {
        Assert.True(Commands.Covers("water.finish"));
        Assert.False(Commands.Covers("water.zone-create"));
    }

    /// <summary>
    ///     ⚠ A key the family does not carry throws rather than inventing a string.
    /// </summary>
    /// <remarks>
    ///     Answering with the key would put <c>editor.command.water.zone-create</c> on a menu and
    ///     answering with an empty string would put nothing there. Both are the quiet failure the
    ///     type exists to end, and both would leave every test about the mode green.
    /// </remarks>
    [Fact]
    public void An_uncovered_key_throws() =>
        Assert.Throws<KeyNotFoundException>(() => Commands["water.zone-create"]);

    /// <summary>A duplicated key exports one entry, because a catalogue is a map.</summary>
    [Fact]
    public void A_repeated_key_is_declared_once() {
        var family = new StringFamily("shop.", [new("buy", "Buy"), new("buy", "Purchase")]);

        Assert.Single(family.All);
        Assert.Equal("Buy", family["buy"].Source);
    }
}
