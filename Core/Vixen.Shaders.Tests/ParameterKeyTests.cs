// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Interned, typed parameter keys — the engine half of docs/plan/07 § Generated C# bindings.
/// </summary>
/// <remarks>
///     Names here are prefixed per test so the process-wide intern table cannot make one test's
///     registration decide another's outcome. That is a property of the thing under test rather
///     than an inconvenience: the table is global on purpose, because two assemblies generating
///     bindings from the same shader must arrive at the same key.
/// </remarks>
public class ParameterKeyTests {
    [Fact]
    public void The_same_name_is_the_same_object() {
        var first = ParameterKeys.New<float>("Test.Same.Value");
        var second = ParameterKeys.New<float>("Test.Same.Value");

        Assert.Same(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    /// <summary>
    ///     The first registration decides the default, and a later one does not silently replace it.
    /// </summary>
    /// <remarks>
    ///     Two shaders declaring the same key with different defaults is a question with no good
    ///     answer, so the answer is "the one that got there first" and it is documented rather than
    ///     surprising. It is also rare by construction: generated names are shader-qualified.
    /// </remarks>
    [Fact]
    public void The_first_registration_owns_the_default() {
        Assert.Equal(3f, ParameterKeys.New("Test.Default.Value", 3f).DefaultValue);
        Assert.Equal(3f, ParameterKeys.New("Test.Default.Value", 9f).DefaultValue);
    }

    /// <summary>
    ///     A name that means two types is refused, because the alternative is a silent bad write.
    /// </summary>
    /// <remarks>
    ///     Without this, a key registered as <c>float</c> and fetched as <c>int</c> would write four
    ///     bytes of the wrong interpretation into a correctly sized slot — a value that is wrong
    ///     rather than absent, which is the harder kind to notice.
    /// </remarks>
    [Fact]
    public void A_name_registered_twice_with_different_types_is_refused() {
        ParameterKeys.New<float>("Test.Conflict.Value");

        var error = Assert.Throws<InvalidOperationException>(() => ParameterKeys.New<int>("Test.Conflict.Value"));

        Assert.Contains("Test.Conflict.Value", error.Message, StringComparison.Ordinal);
        Assert.Contains("Single", error.Message, StringComparison.Ordinal);
        Assert.Contains("Int32", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A value key and a permutation key of the same type are still different things.</summary>
    /// <remarks>
    ///     Confusing them is expensive in one direction and pointless in the other: setting a
    ///     permutation at draw time does nothing until something recompiles, and making a value key
    ///     a permutation multiplies the shader cache for no gain.
    /// </remarks>
    [Fact]
    public void A_value_key_and_a_permutation_key_cannot_share_a_name() {
        ParameterKeys.New("Test.Kind.Value", false);

        var error = Assert.Throws<InvalidOperationException>(
            () => ParameterKeys.NewPermutation(false, "Test.Kind.Value")
        );

        Assert.Contains("value key", error.Message, StringComparison.Ordinal);
        Assert.Contains("permutation key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_can_be_found_by_the_name_an_asset_spells_it_with() {
        var created = ParameterKeys.New<float>("Test.Lookup.Value");

        Assert.True(ParameterKeys.TryGet("Test.Lookup.Value", out var found));
        Assert.Same(created, found);
        Assert.False(ParameterKeys.TryGet("Test.Lookup.Missing", out _));
    }

    [Fact]
    public void An_empty_name_is_refused() {
        Assert.Throws<ArgumentException>(() => ParameterKeys.New<float>(string.Empty));
        Assert.Throws<ArgumentNullException>(() => ParameterKeys.New<float>(null!));
    }
}
