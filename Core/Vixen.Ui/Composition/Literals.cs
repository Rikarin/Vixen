// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Composition;

/// <summary>
///     Turns the text of a quoted attribute into the value the property it is assigned to wants.
/// </summary>
/// <remarks>
///     <para>
///         <c>Variant="Subtle"</c> reads the way an attribute should and is not a string:
///         <c>ControlVariant.Variant</c> is an enum, and the markup compiler cannot know that
///         because it resolves no types. So the emitter writes
///         <c>n1.Variant = Literals.Of(n1.Variant, "Subtle")</c> and the <i>C# compiler</i> picks
///         which conversion that means, from the type of the property it is about to assign.
///     </para>
///     <para>
///         ⚠ <b>The first argument exists to be inferred from, and is never read.</b> There is no
///         other way to get the target's type into a generic method — C# does not infer a type
///         argument from what an expression is being assigned to. It is a property read, so a
///         property whose getter does work does it once per literal attribute at build time; a
///         property that cannot be read at all is one to write with <c>@expr</c>.
///     </para>
///     <para>
///         ⚠ <b>An overload set rather than a converter, and that is the whole design.</b> A single
///         <c>object Convert(Type, string)</c> would compile for every property and fail at run time
///         on the ones it cannot do. Here a property of a type nothing converts to — a
///         <c>PathBuilder</c>, say — is a <i>compile</i> error on the characters of the attribute,
///         which is the bargain every other part of the markup channel is written to keep. It is
///         also why this is AOT-clean: <see cref="Enum.Parse{T}(string, bool)" /> under a
///         <c>struct, Enum</c> constraint is static generic code, not reflection over a
///         <see cref="Type" />.
///     </para>
/// </remarks>
public static class Literals {
    /// <summary>A property that is already text.</summary>
    /// <param name="shape">The property, read only to infer from. Never used.</param>
    /// <param name="text">What the markup wrote.</param>
    /// <returns>The text.</returns>
    public static string Of(string? shape, string text) => text;

    /// <summary>An enum-valued property, named by member rather than by qualified expression.</summary>
    /// <typeparam name="T">The enum, inferred from the property.</typeparam>
    /// <param name="shape">The property, read only to infer from. Never used.</param>
    /// <param name="text">The member's name, in any case.</param>
    /// <returns>The member.</returns>
    /// <exception cref="ArgumentException">No member of <typeparamref name="T" /> is called that.</exception>
    /// <remarks>
    ///     Case-insensitive, because a stylesheet spells <c>subtle</c> and C# spells <c>Subtle</c>
    ///     and an attribute sits between the two. ⚠ <b>A misspelling is caught here rather than by
    ///     the compiler</b>, which is the price of the shorthand and is why the message lists what
    ///     the member could have been — <c>@ControlVariant.Subtle</c> remains available for anyone
    ///     who would rather have the error at build time.
    /// </remarks>
    public static T Of<T>(T shape, string text) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value)
            ? value
            : throw new ArgumentException(
                $"'{text}' is not a {typeof(T).Name}. It is one of: {string.Join(", ", Enum.GetNames<T>())}.",
                nameof(text)
            );

    /// <inheritdoc cref="Of{T}(T, string)" />
    /// <typeparam name="T">The enum, inferred from the property.</typeparam>
    /// <param name="shape">The property, read only to infer from. Never used.</param>
    /// <param name="text">The member's name, in any case.</param>
    public static T? Of<T>(T? shape, string text) where T : struct, Enum => Of(default(T), text);

    /// <summary>A flag, written as it is in C# and in CSS both.</summary>
    /// <param name="shape">The property, read only to infer from. Never used.</param>
    /// <param name="text">The value.</param>
    /// <returns>The flag.</returns>
    public static bool Of(bool shape, string text) => bool.Parse(text);

    /// <inheritdoc cref="Of(bool, string)" />
    /// <param name="shape">The property, read only to infer from. Never used.</param>
    /// <param name="text">The value.</param>
    public static bool? Of(bool? shape, string text) => bool.Parse(text);

    /// <summary>A whole number.</summary>
    /// <param name="shape">The property, read only to infer from. Never used.</param>
    /// <param name="text">The value.</param>
    /// <returns>The number.</returns>
    /// <remarks>
    ///     Invariant culture, everywhere below. A <c>.vxml</c> is source text: the same file has to
    ///     mean the same thing on a machine whose decimal separator is a comma.
    /// </remarks>
    public static int Of(int shape, string text) => int.Parse(text, CultureInfo.InvariantCulture);

    /// <inheritdoc cref="Of(int, string)" />
    /// <param name="shape">The property, read only to infer from. Never used.</param>
    /// <param name="text">The value.</param>
    public static int? Of(int? shape, string text) => int.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>A number.</summary>
    /// <param name="shape">The property, read only to infer from. Never used.</param>
    /// <param name="text">The value.</param>
    /// <returns>The number.</returns>
    public static float Of(float shape, string text) => float.Parse(text, CultureInfo.InvariantCulture);

    /// <inheritdoc cref="Of(float, string)" />
    /// <param name="shape">The property, read only to infer from. Never used.</param>
    /// <param name="text">The value.</param>
    public static float? Of(float? shape, string text) => float.Parse(text, CultureInfo.InvariantCulture);

    /// <inheritdoc cref="Of(float, string)" />
    /// <param name="shape">The property, read only to infer from. Never used.</param>
    /// <param name="text">The value.</param>
    public static double Of(double shape, string text) => double.Parse(text, CultureInfo.InvariantCulture);

    /// <inheritdoc cref="Of(float, string)" />
    /// <param name="shape">The property, read only to infer from. Never used.</param>
    /// <param name="text">The value.</param>
    public static double? Of(double? shape, string text) => double.Parse(text, CultureInfo.InvariantCulture);
}
