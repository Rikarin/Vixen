// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Platform.Linux.Tests;

/// <summary>The accent read that fills <c>--accent</c> on a GNOME desktop.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>GNOME's setting is a name, so unlike macOS this reader owns a table of colours —
///         and a table nothing asserts is nine constants that drift.</b>
///         <c>org.gnome.desktop.interface accent-color</c> answers <c>'teal'</c>; the colour that
///         name stands for is libadwaita's. There is no semantic colour to read instead, which is
///         the whole reason the table exists, and it is why the table rather than the subprocess is
///         what these tests are about.
///     </para>
///     <para>
///         ⚠ <b>The count is asserted, not just the entries.</b> A table read by a
///         <c>switch</c> loses an arm without failing to build — the arm simply never matches — so a
///         test that walked the nine names it knows about would keep passing after somebody deleted
///         one from the reader. The names are listed here independently of the reader and the list's
///         own length is checked, which is the half that makes iterating it mean anything.
///     </para>
///     <para>
///         <b>The subprocess half is deliberately absent.</b> Spawning <c>gsettings</c> needs a
///         GNOME session; CI's Linux leg is a container that has neither, and asserting "it returned
///         Unknown" there would be a test that passes on the day the reader does nothing. What can
///         be asserted anywhere is the decode, and that is what is asserted.
///     </para>
/// </remarks>
public class LinuxAccentTests {
    /// <summary>
    ///     The nine <c>accent-color</c> values GNOME 47 defines, written out here rather than taken
    ///     from the reader under test.
    /// </summary>
    static readonly string[] Names = [
        "blue", "teal", "green", "yellow", "orange", "red", "pink", "purple", "slate"
    ];

    [Fact]
    public void EveryGnomeAccentNameResolvesToItsOwnOpaqueColourAndAWhiteText() {
        // ⚠ The count first. Without it this loop is a claim about `Names` rather than about the
        // reader, and an eighth arm quietly deleted from the switch would still leave it green
        // because the loop would simply visit one name fewer than it should have.
        Assert.Equal(9, Names.Length);

        var seen = new HashSet<(float R, float G, float B)>();

        foreach (var name in Names) {
            var accent = LinuxAccent.FromName(name);

            Assert.True(accent.IsKnown, $"'{name}' has no colour in the table.");

            var colour = accent.Color!.Value;

            // Opaque, because an accent at zero alpha is invisible rather than wrong — the failure
            // a colour comparison alone would call a pass.
            Assert.Equal(1f, colour.A);

            // ⚠ And distinct, which is what catches the mistake a table invites: nine arms all
            // returning the same blue satisfies every other assertion here.
            Assert.True(seen.Add((colour.R, colour.G, colour.B)), $"'{name}' repeats an earlier colour.");

            // The pair, because half a read does not move the theme's token: `PlatformInput` puts
            // `SystemPalette.PlatformAccentClass` on for the accent AND its text or not at all.
            // libadwaita draws white on all nine.
            Assert.NotNull(accent.Text);
            Assert.Equal(1f, accent.Text!.Value.R);
            Assert.Equal(1f, accent.Text!.Value.G);
            Assert.Equal(1f, accent.Text!.Value.B);
            Assert.Equal(1f, accent.Text!.Value.A);
        }
    }

    [Theory]
    // libadwaita's `AdwAccentColor` values, as a human writes them, against a decode that has to
    // put the bytes back in that order. Two of the nine, chosen because their components differ in
    // every position: a red/blue swap survives a symmetric colour.
    [InlineData("blue", 0x35, 0x84, 0xe4)]
    [InlineData("orange", 0xed, 0x5b, 0x00)]
    public void TheTableIsLibadwaitasValuesAndNotSomeOtherBlue(string name, int red, int green, int blue) {
        var colour = LinuxAccent.FromName(name).Color;

        Assert.NotNull(colour);
        Assert.Equal(red / 255f, colour!.Value.R, 0.001f);
        Assert.Equal(green / 255f, colour.Value.G, 0.001f);
        Assert.Equal(blue / 255f, colour.Value.B, 0.001f);
    }

    [Theory]
    // An older GNOME with no such key, a name from a GNOME newer than this table, and the shapes a
    // mis-parsed gsettings line takes.
    [InlineData("")]
    [InlineData("default")]
    [InlineData("'teal'")]
    [InlineData("Blue")]
    [InlineData("mauve")]
    public void ANameWithNoColourHereIsUnknownRatherThanTheDefaultBlue(string name) {
        var accent = LinuxAccent.FromName(name);

        // ⚠ Answering some blue for a name whose colour is not known here would be
        // indistinguishable at the destination from having read it off the machine, which is the
        // one thing `SystemAccent`'s nullability exists to prevent.
        Assert.False(accent.IsKnown);
        Assert.Null(accent.Color);
        Assert.Null(accent.Text);
    }
}
