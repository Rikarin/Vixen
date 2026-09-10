// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>
///     Which types may key a shader variant.
/// </summary>
/// <remarks>
///     <para>
///         Two rules ask this question — <c>RVN2062</c> about a <c>[Permutation]</c> field's type and
///         <c>RVN2081</c> about a value parameter's — and they are meant to be the same restriction,
///         because both values end up in the same place: the key a compiled variant is cached under.
///     </para>
///     <para>
///         ⚠ <b>They were the same predicate written out twice, and nothing held them together.</b>
///         Widening one to admit, say, a small enum left the other refusing it, and the failure is a
///         shader that compiles as a permutation and not as a value parameter. Worse, the duplicate
///         is a decoy for anyone proving a negative fixture has teeth: a widening applied to the
///         first textual match leaves the value-parameter fixture green, which reads exactly like a
///         fixture that proves nothing (see
///         <c>NegativeDiagnosticTests.A_bool_an_int_and_a_uint_value_parameter_are_all_allowed</c>).
///         One named predicate, read from both sites, is what makes the comment's claim a reference.
///     </para>
/// </remarks>
static class CacheKeyTypes {
    /// <summary>
    ///     Whether a value of this type may key a variant.
    /// </summary>
    /// <remarks>
    ///     bool for flags, int/uint for counts (tap counts, cascade counts, light limits). Floats are
    ///     deliberately excluded: they make poor cache keys — two values that differ in the last bit
    ///     are two compiled variants — and a shader wanting one should take a uniform.
    /// </remarks>
    /// <param name="special">The type in question, or <c>null</c> when it is not a primitive at all.</param>
    /// <returns>Whether the type is one a variant key may have.</returns>
    public static bool IsCacheKeyType(this SpecialType? special) =>
        special is SpecialType.Bool or SpecialType.Int or SpecialType.UInt;
}
