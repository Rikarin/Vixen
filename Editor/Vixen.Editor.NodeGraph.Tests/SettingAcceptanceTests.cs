// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Xunit;

namespace Tests;

/// <summary>
///     What <see cref="SettingDefinition.Accepts" /> answers, and why it is the answer every refusal
///     in the tree already gives — #1017.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="SettingDefinition.Accepts" /> still has no production caller, and
///         <c>Canonical</c> is the shape that got two</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1044">#1044</a>. They share an index, so
///         this suite covers both; it is not an argument that the predicate is reached. The
///         predicate
///         was never going to acquire one in that shape: the two refusals that would adopt it need
///         the <em>canonical spelling</em> back, because a graph written <c>Multiply</c> and one
///         written <c>multiply</c> have to compile to one thing, and a <c>bool</c> throws that away.
///         <see cref="SettingDefinition.Canonical" /> answers it, both eight-line walks collapsed
///         into it, and <see cref="SettingDefinition.Accepts" /> shares its index.
///     </para>
///     <para>
///         The case rule is what the rest of this suite pins, and it is still the reason nothing
///         could adopt the predicate before: it was exact-case while every refusal it mirrors is
///         not — <c>TextureSettings.Enum</c> parses with <c>ignoreCase: true</c>, and both nodes
///         compared with <see cref="StringComparison.OrdinalIgnoreCase" />. A graph holding
///         <c>multiply</c> compiles, so an exact-case predicate would have refused graphs that do.
///     </para>
/// </remarks>
public class SettingAcceptanceTests {
    static SettingDefinition Mode { get; } =
        new("Mode", "Copy", Accepted: ["Copy", "Multiply", "Screen"]);

    /// <summary>A declared name is accepted.</summary>
    [Fact]
    public void A_declared_name_is_accepted() {
        Assert.True(Mode.Accepts("Multiply"));
        Assert.True(Mode.IsChoice);
    }

    /// <summary>A name in another case is accepted, because every refusal accepts it.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that was false.</b> <c>Enum.TryParse(ignoreCase: true)</c> takes
    ///     <c>multiply</c>, so a graph holding it compiles and draws; a predicate that called it
    ///     unaccepted would have been a second opinion, and the stricter one.
    /// </remarks>
    [Theory]
    [InlineData("multiply")]
    [InlineData("MULTIPLY")]
    [InlineData("mUlTiPlY")]
    public void A_name_in_another_case_is_accepted_because_the_compiler_accepts_it(string written) =>
        Assert.True(Mode.Accepts(written));

    /// <summary>A name nothing declares is refused.</summary>
    /// <remarks>
    ///     The other half: ignoring case must not become ignoring the list, which is the failure a
    ///     comparison loosened one step too far would produce.
    /// </remarks>
    [Theory]
    [InlineData("mulitply")]
    [InlineData("Multiplyy")]
    [InlineData("")]
    public void A_name_nothing_declares_is_refused(string written) => Assert.False(Mode.Accepts(written));

    /// <summary>A setting that declares no list accepts anything, including nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Not "accepts nothing", which is what an empty list read as a list would mean.</b> The
    ///     many settings that are genuinely a name — a menu path, an asset reference, a Raven
    ///     expression — declare no list, and a predicate that refused them all would refuse most of
    ///     the settings in the tree.
    /// </remarks>
    [Fact]
    public void A_setting_that_declares_no_list_accepts_anything() {
        SettingDefinition free = new("Ramp", "");

        Assert.True(free.Accepts("anything at all"));
        Assert.True(free.Accepts(""));
        Assert.False(free.IsChoice);
    }

    /// <summary>⚠ A name written in another case comes back in the case the setting states.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The answer the two refusals needed and <see cref="SettingDefinition.Accepts" />
    ///         could not give</b> — <a href="https://github.com/Rikarin/Vixen/issues/1044">#1044</a>.
    ///         A graph written <c>multiply</c> and one written <c>Multiply</c> have to compile to one
    ///         thing, and what makes that true is that the compiler stores the declaration's
    ///         spelling rather than the author's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted with <c>Ordinal</c>, which is the whole test.</b> xunit's default string
    ///         comparison is ordinal too, but saying so here is the difference between an assertion
    ///         about the spelling and one that would pass on any answer differing only in case —
    ///         which is precisely the failure this method exists to prevent.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("multiply")]
    [InlineData("MULTIPLY")]
    [InlineData("Multiply")]
    public void A_name_comes_back_in_the_case_the_setting_states(string written) =>
        Assert.Equal("Multiply", Mode.Canonical(written), StringComparer.Ordinal);

    /// <summary>A name nothing declares canonicalises to nothing, so a caller can tell.</summary>
    /// <remarks>
    ///     Empty rather than the value itself, which is the one thing a caller with a closed set
    ///     cannot be given: <c>TextureUsages.Canonical</c>'s refusal is written as "the answer was
    ///     empty", and a method that echoed the misspelling back would make every typo a usage.
    /// </remarks>
    [Theory]
    [InlineData("mulitply")]
    [InlineData("Multiplyy")]
    [InlineData("")]
    public void A_name_nothing_declares_canonicalises_to_nothing(string written) =>
        Assert.Equal("", Mode.Canonical(written), StringComparer.Ordinal);

    /// <summary>⚠ And a setting that states no list answers with the value, not with nothing.</summary>
    /// <remarks>
    ///     <b>The trap #1044 names, and the reason the two methods share an index rather than one
    ///     calling the other.</b> <c>Canonical("")</c> on a setting with no list has to answer
    ///     <c>""</c> <em>and</em> <c>Accepts("")</c> has to stay true — so
    ///     <c>Accepts(v) == Canonical(v).Length > 0</c> is not an identity, and writing one as the
    ///     other would have made a free-text setting refuse the empty string it plainly accepts.
    /// </remarks>
    [Fact]
    public void A_setting_that_declares_no_list_canonicalises_to_what_it_was_given() {
        SettingDefinition free = new("Ramp", "");

        Assert.Equal("anything at all", free.Canonical("anything at all"), StringComparer.Ordinal);
        Assert.Equal("", free.Canonical(""), StringComparer.Ordinal);
        Assert.True(free.Accepts(""));
    }

    /// <summary>⚠ And a list that states the empty string accepts it, which the shared index buys.</summary>
    /// <remarks>
    ///     <b>The case the shorthand gets wrong.</b> Nothing in the tree declares such a list today
    ///     — it would be a setting whose "unset" is a stated member — but it is expressible, and
    ///     <c>Accepts</c> written as <c>Canonical(value).Length > 0</c> would refuse the one value
    ///     the declaration most plainly states. Held here so that a later simplification of the two
    ///     into one expression goes red rather than quiet.
    /// </remarks>
    [Fact]
    public void A_list_that_states_the_empty_string_accepts_it() {
        SettingDefinition optional = new("Ramp", "", Accepted: ["", "Linear", "Smooth"]);

        Assert.True(optional.Accepts(""));
        Assert.Equal("", optional.Canonical(""), StringComparer.Ordinal);
        Assert.False(optional.Accepts("Bezier"));
    }
}
