// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Raven.Symbols.Source;

/// <summary>
///     Matching a value supplied from outside the source — a <c>[Permutation]</c> define, a
///     <c>val</c> type argument — against the type it was declared with.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ The supplied value's CLR type is <em>not</em> a statement about the author's intent,
///         and reading it as one is an over-fire that made <c>uint</c> keys unusable.
///         <see cref="PermutationValues.TryParse" /> turns text into one of bool, int and uint by
///         trying each in turn, so <c>-D Slots=16</c> yields an <c>int</c> whatever the key it is
///         meant for: the <c>uint</c> branch is reached only above <c>int.MaxValue</c>. Demanding
///         <c>value is uint</c> for a <c>uint</c> key therefore rejects every value a build can
///         actually supply for one, with <c>RVN2064</c> — and <c>RVN2083</c> for a <c>val</c>
///         parameter — on a define that is correct.
///     </para>
///     <para>
///         So the declared type decides and the parsed type only has to be able to reach it. A
///         negative integer against <c>uint</c> is still a mismatch, because that one is a fact
///         about the value rather than about how the text happened to parse; so is a <c>bool</c>
///         against a count, in either direction. What crosses is coerced, so a folded key is the
///         CLR type its declaration promised rather than whatever the command line produced.
///     </para>
/// </remarks>
static class SuppliedValue {
    /// <summary>
    ///     Whether <paramref name="value" /> can be <paramref name="type" />, and what it is once
    ///     it is.
    /// </summary>
    /// <param name="type">The declared type, which is the authority.</param>
    /// <param name="value">The supplied value, already narrowed to bool, int or uint.</param>
    /// <param name="coerced">The value as the declared type, meaningful only when this is true.</param>
    public static bool TryCoerce(TypeSymbol type, object value, out object coerced) {
        coerced = value;

        switch ((type as PrimitiveTypeSymbol)?.SpecialType) {
            case SpecialType.Bool:
                return value is bool;

            case SpecialType.Int:
                switch (value) {
                    case int:
                        return true;

                    // Only from text above int.MaxValue, which no int can hold.
                    case uint unsigned when unsigned <= int.MaxValue:
                        coerced = (int)unsigned;
                        return true;

                    default:
                        return false;
                }

            case SpecialType.UInt:
                switch (value) {
                    case uint:
                        return true;

                    // The ordinary case: every in-range define parses as int.
                    case int signed when signed >= 0:
                        coerced = (uint)signed;
                        return true;

                    default:
                        return false;
                }

            default:
                return false;
        }
    }
}
