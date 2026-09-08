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
///     ⚠ <b><see cref="SettingDefinition.Accepts" /> has no production caller, and this suite is not
///     an argument that it does.</b> It is here because the predicate was exact-case while every
///     refusal it was built to mirror is not: <c>TextureSettings.Enum</c> parses with
///     <c>ignoreCase: true</c>, and the two nodes that walk a list of their own compare with
///     <see cref="StringComparison.OrdinalIgnoreCase" />. A graph holding <c>multiply</c> compiles.
///     So the reason nothing could adopt this predicate was that adopting it would have refused
///     graphs that compile — a fact worth pinning before somebody adopts it anyway.
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
}
