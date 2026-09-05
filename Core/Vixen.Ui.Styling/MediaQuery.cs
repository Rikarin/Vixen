// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;

namespace Vixen.Ui.Styling;

/// <summary>Whether the UI prefers a light or a dark palette.</summary>
public enum ColorSchemePreference : byte {
    /// <summary>No preference expressed.</summary>
    NoPreference,

    /// <summary>Light.</summary>
    Light,

    /// <summary>Dark.</summary>
    Dark
}

/// <summary>Whether the user has asked for less movement on screen.</summary>
/// <remarks>
///     ⚠ <b>Two values and not three, unlike <see cref="ColorSchemePreference" />.</b> CSS Media
///     Queries 5 § 6.2 gives <c>prefers-reduced-motion</c> exactly <c>no-preference</c> and
///     <c>reduce</c>: there is no way for a user to ask for <i>more</i> motion, so an
///     <c>Increase</c> here would be a member no operating system can ever produce and every
///     <c>switch</c> over it would need an arm nothing reaches.
/// </remarks>
public enum MotionPreference : byte {
    /// <summary>Nothing was asked for, which is not the same as asking for motion.</summary>
    NoPreference,

    /// <summary>Less movement, please.</summary>
    Reduce
}

/// <summary>What a media query is asked about.</summary>
/// <param name="Width">The surface width in logical pixels.</param>
/// <param name="Height">The surface height in logical pixels.</param>
/// <param name="Resolution">Device pixels per logical pixel.</param>
/// <param name="ColorScheme">The palette preference.</param>
/// <param name="Gamut">
///     What the surface can actually show, which is the swapchain's granted gamut and not the
///     monitor's specification sheet.
/// </param>
/// <param name="ReducedMotion">
///     Whether the user has asked for less movement. ⚠ Unlike every other member here it is a
///     statement about the <i>person</i> rather than about the surface, and it is on this record
///     anyway because a media query is the only thing that asks — and because a document shown in
///     two windows on two machines is not a case this framework has.
/// </param>
/// <remarks>
///     <para>
///         A surface rather than a screen. A game's UI can be a full window, a split-screen viewport
///         or a world-space panel on the side of a crate, and all three want
///         <c>@media (max-width: …)</c> to mean "this panel", never "the monitor".
///     </para>
///     <para>
///         ⚠ <b><see cref="Gamut" /> is the one fact here that is about output rather than layout,
///         and it is still a property of the surface.</b> The honest source for it is
///         <c>ISwapChain.Gamut</c> — what the swapchain was granted — rather than what the display
///         claims to be capable of. A panel rendered into an sRGB swapchain on a P3 monitor can show
///         sRGB, and a stylesheet told otherwise would pick colours that get mapped away again.
///     </para>
/// </remarks>
public readonly record struct MediaContext(
    float Width,
    float Height,
    float Resolution = 1f,
    ColorSchemePreference ColorScheme = ColorSchemePreference.NoPreference,
    ColorGamut Gamut = ColorGamut.Srgb,
    MotionPreference ReducedMotion = MotionPreference.NoPreference
);

/// <summary>Evaluates the <c>@media</c> conditions doc 09 lists as supported.</summary>
/// <remarks>
///     <para>
///         A condition is <c>and</c>-joined features, each <c>(name)</c>, <c>(name: value)</c> or
///         Media Queries 4 § 2.4's range syntax — <c>(width &lt; 24rem)</c>,
///         <c>(400px &lt;= width &lt; 600px)</c> — with the <c>min-</c> and <c>max-</c> prefixes that
///         carry nearly all real usage. That is deliberately less than CSS Media Queries 4: no
///         <c>or</c> and no <c>not</c>.
///     </para>
///     <para>
///         ⚠ <b>The range operators are not sugar for the prefixes.</b> <c>max-width: 448px</c> is
///         <c>&lt;=</c> and <c>width &lt; 448px</c> is <c>&lt;</c>, and the two disagree on exactly
///         one width — the threshold. Tailwind v4's <c>@max-*</c> is the exclusive one, so the
///         prefix form alone made every <c>max-</c> breakpoint in this engine off by one pixel in a
///         way that reads as an author mis-picking their breakpoint. See
///         <see cref="FeatureComparison" />.
///     </para>
///     <para>
///         <b>A condition this cannot evaluate makes the whole block fail to load, with a
///         diagnostic.</b> The alternative is choosing a default, and both defaults are wrong in a
///         way nobody will notice: treating it as false silently drops styles, and treating it as
///         true silently applies phone styles on a desktop. The same rule the selector compiler
///         follows, for the same reason.
///     </para>
/// </remarks>
public static class MediaQuery {
    /// <summary>Evaluates a condition.</summary>
    /// <param name="condition">The text between <c>@media</c> and the block.</param>
    /// <param name="context">What to evaluate it against.</param>
    /// <param name="matches">Whether the condition holds.</param>
    /// <param name="reason">Why it could not be evaluated, when it could not.</param>
    /// <returns>Whether it could be evaluated at all.</returns>
    public static bool TryEvaluate(string? condition, MediaContext context, out bool matches, out string? reason) {
        matches = true;
        reason = null;

        if (string.IsNullOrWhiteSpace(condition)) {
            // `@media { … }` — no condition, so nothing to fail.
            return true;
        }

        foreach (var range in condition.AsSpan().Split(" and ")) {
            var term = condition.AsSpan()[range].Trim();

            if (term.IsEmpty) {
                continue;
            }

            // `all` is the only media type Vixen has; `screen` and `print` mean nothing to a game.
            if (term.Equals("all", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (term[0] != '(' || term[^1] != ')') {
                reason = $"'{term}' is not a media feature Vixen understands";
                return false;
            }

            if (!TryFeature(term[1..^1].Trim(), context, out var held, out reason)) {
                return false;
            }

            matches &= held;
        }

        return true;
    }

    static bool TryFeature(ReadOnlySpan<char> feature, MediaContext context, out bool matches, out string? reason) {
        matches = false;
        reason = null;

        if (!FeatureRange.TryRead(feature, out var terms, out reason)) {
            return false;
        }

        var name = terms.Name;
        var value = terms.Value;

        if (name.Equals("orientation", StringComparison.OrdinalIgnoreCase)) {
            if (terms.IsRanged) {
                reason = "'orientation' is discrete, so it has no range or min-/max- form";
                return false;
            }

            var landscape = context.Width >= context.Height;
            if (value.Equals("landscape", StringComparison.OrdinalIgnoreCase)) {
                matches = landscape;
                return true;
            }

            if (value.Equals("portrait", StringComparison.OrdinalIgnoreCase)) {
                matches = !landscape;
                return true;
            }

            reason = $"'{value}' is not an orientation";
            return false;
        }

        if (name.Equals("prefers-color-scheme", StringComparison.OrdinalIgnoreCase)) {
            if (terms.IsRanged) {
                reason = "'prefers-color-scheme' is discrete, so it has no range or min-/max- form";
                return false;
            }

            if (value.Equals("dark", StringComparison.OrdinalIgnoreCase)) {
                matches = context.ColorScheme == ColorSchemePreference.Dark;
                return true;
            }

            if (value.Equals("light", StringComparison.OrdinalIgnoreCase)) {
                matches = context.ColorScheme == ColorSchemePreference.Light;
                return true;
            }

            reason = $"'{value}' is not a colour scheme";
            return false;
        }

        if (name.Equals("prefers-reduced-motion", StringComparison.OrdinalIgnoreCase)) {
            // Discrete, on `color-gamut`'s terms: Media Queries 5 gives it no range type, so
            // `min-prefers-reduced-motion` is a typo rather than a spelling variant — and so is
            // `(prefers-reduced-motion > reduce)`, which is why the guard is `IsRanged` and not a
            // comparison of its own.
            if (terms.IsRanged) {
                reason = "'prefers-reduced-motion' is discrete, so it has no range or min-/max- form";
                return false;
            }

            if (value.Equals("reduce", StringComparison.OrdinalIgnoreCase)) {
                matches = context.ReducedMotion == MotionPreference.Reduce;
                return true;
            }

            if (value.Equals("no-preference", StringComparison.OrdinalIgnoreCase)) {
                matches = context.ReducedMotion == MotionPreference.NoPreference;
                return true;
            }

            if (terms.IsBoolean) {
                // ⚠ The boolean form is answered here and not for `prefers-color-scheme`, and that
                // is the specification's asymmetry rather than an omission. Media Queries 5 § 6.2
                // makes `no-preference` the *false* value of this feature, so
                // `@media (prefers-reduced-motion)` means "reduce" and is the spelling almost every
                // sheet in the wild uses. A colour scheme has no false value in the same sense —
                // every display shows one of the two — so a bare `(prefers-color-scheme)` is a
                // question with no useful answer and stays a diagnostic.
                matches = context.ReducedMotion == MotionPreference.Reduce;
                return true;
            }

            reason = $"'{value}' is not a motion preference";
            return false;
        }

        if (name.Equals("color-gamut", StringComparison.OrdinalIgnoreCase)) {
            // ⚠ Discrete, so `min-`, `max-` and the range operators are not spelling variants of it
            // — Media Queries 5 gives the feature no range type, and the prefix was already stripped
            // above. Rejecting them keeps `@media (min-color-gamut: p3)` a diagnostic instead of a
            // query that quietly means something the author did not write.
            if (terms.IsRanged) {
                reason = "'color-gamut' is discrete, so it has no range or min-/max- form";

                return false;
            }

            // ⚠ Ascending, not equality. Media Queries 5 § 5.4: "an output device can return true for
            // multiple values of this media feature ... one gamut is a subset of another supported
            // gamut", and the note recommends authors set a base at `srgb` then override at `p3`.
            // Testing for equality instead would make the base rule stop applying on exactly the
            // displays it was written for.
            var supported = context.Gamut switch {
                ColorGamut.Rec2020 => 2,
                ColorGamut.DisplayP3 => 1,
                _ => 0
            };

            var asked = -1;

            if (value.Equals("srgb", StringComparison.OrdinalIgnoreCase)) {
                asked = 0;
            } else if (value.Equals("p3", StringComparison.OrdinalIgnoreCase)) {
                asked = 1;
            } else if (value.Equals("rec2020", StringComparison.OrdinalIgnoreCase)) {
                asked = 2;
            } else if (value.IsEmpty) {
                // The boolean form. `not (color-gamut)` is how the specification says to test for a
                // display that cannot manage even sRGB, so the bare feature is true whenever one can.
                matches = true;

                return true;
            }

            if (asked < 0) {
                reason = $"'{value}' is not a colour gamut";

                return false;
            }

            matches = supported >= asked;

            return true;
        }

        var actual = name switch {
            _ when name.Equals("width", StringComparison.OrdinalIgnoreCase) => context.Width,
            _ when name.Equals("height", StringComparison.OrdinalIgnoreCase) => context.Height,
            _ when name.Equals("resolution", StringComparison.OrdinalIgnoreCase) => context.Resolution,
            _ => float.NaN
        };

        if (float.IsNaN(actual)) {
            reason = $"'{name}' is not a media feature Vixen supports";
            return false;
        }

        if (terms.IsBoolean) {
            // `(width)` asks whether the feature is non-zero, which is what CSS means by the
            // boolean form.
            matches = actual != 0f;
            return true;
        }

        if (!TryLength(value, out var wanted)) {
            reason = $"'{value}' is not a length Vixen can compare";
            return false;
        }

        matches = FeatureRange.Holds(actual, terms.Comparison, wanted);

        if (!terms.HasSecond) {
            return true;
        }

        if (!TryLength(terms.SecondValue, out var second)) {
            reason = $"'{terms.SecondValue}' is not a length Vixen can compare";
            return false;
        }

        matches &= FeatureRange.Holds(actual, terms.SecondComparison, second);

        return true;
    }

    /// <summary>Reads a length, shared with <see cref="ContainerQuery" />.</summary>
    /// <remarks>
    ///     Internal rather than duplicated, because <c>@container (min-width: 20rem)</c> and
    ///     <c>@media (min-width: 20rem)</c> mean the same length and a second copy is a second unit
    ///     table to forget to extend.
    /// </remarks>
    internal static bool TryLength(ReadOnlySpan<char> text, out float value) {
        var scale = 1f;

        foreach (var (unit, factor) in Units) {
            if (!text.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            text = text[..^unit.Length];
            scale = factor;
            break;
        }

        if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) {
            return false;
        }

        value *= scale;
        return true;
    }

    // Longest suffix first, or `2dppx` reads as a length in pixels ending in "d" and `2x` never
    // gets the chance to be a resolution at all.
    static readonly (string Unit, float Factor)[] Units = [
        ("dppx", 1f), ("dpcm", 2.54f / 96f), ("dpi", 1f / 96f), ("px", 1f), ("x", 1f)
    ];
}
