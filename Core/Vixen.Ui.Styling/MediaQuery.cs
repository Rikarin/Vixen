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

/// <summary>Whether the user has asked for less animation.</summary>
public enum MotionPreference : byte {
    /// <summary>No preference expressed — CSS's <c>no-preference</c>.</summary>
    NoPreference,

    /// <summary>Animation should be reduced — CSS's <c>reduce</c>.</summary>
    Reduce
}

/// <summary>Whether the user has asked for more or less contrast.</summary>
/// <remarks>
///     ⚠ <b><c>custom</c> is a fourth value and not a synonym for either.</b> Media Queries 5 § 8.3
///     gives it to a platform whose palette was chosen by the user rather than pushed towards or
///     away from the extremes, and a rule written for <c>more</c> is not what that user asked for.
///     Collapsing it would make every high-contrast rule apply to a chosen palette as well.
/// </remarks>
public enum ContrastPreference : byte {
    /// <summary>No preference expressed.</summary>
    NoPreference,

    /// <summary>More contrast.</summary>
    More,

    /// <summary>Less contrast.</summary>
    Less,

    /// <summary>A palette the user chose, which is neither more nor less.</summary>
    Custom
}

/// <summary>What a pointing device can do, as CSS's <c>pointer</c> and <c>any-pointer</c> ask.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Flags, and the reason is <c>any-pointer</c> rather than <c>pointer</c>.</b> Media
///         Queries 5 § 9.2 says <c>pointer</c> describes the <i>primary</i> mechanism and takes one
///         value, while <c>any-pointer</c> is true of every value some attached device satisfies — a
///         tablet with a stylus and a finger is coarse <b>and</b> fine at once. One type serves both,
///         because a single value is a one-bit set.
///     </para>
///     <para>
///         ⚠ <b><see cref="NoDevice" /> is a bit rather than the zero, and that is the whole design
///         of this enum.</b> CSS's <c>none</c> is the empty capability set, and the empty set is also
///         what a field nobody assigned holds — so if zero meant "no pointing device" then
///         <c>default(MediaContext)</c> would assert a machine with no mouse and
///         <c>pointer-none:</c> would be the rule that always applies. Zero therefore means
///         <see cref="Unspecified" />, which <see cref="MediaContext" /> resolves to
///         <see cref="Fine" />; <see cref="NoDevice" /> is the <i>stated</i> empty set and carries a
///         bit so that it can be told from the unstated one. Both read as "no pointing device" to a
///         query, since the test is for the absence of <see cref="Coarse" /> and <see cref="Fine" />.
///     </para>
/// </remarks>
[Flags]
public enum PointerCapability : byte {
    /// <summary>Nothing has been said about the pointing devices. Read as <see cref="Fine" />.</summary>
    Unspecified = 0,

    /// <summary>A device with limited accuracy — a finger.</summary>
    Coarse = 1,

    /// <summary>A device that can point accurately — a mouse or a stylus.</summary>
    Fine = 2,

    /// <summary>Stated: there is no pointing device at all, which is CSS's <c>none</c>.</summary>
    NoDevice = 4
}

/// <summary>What the platform has been asked for, as one value.</summary>
/// <param name="Motion">Whether the user has asked for less animation.</param>
/// <param name="Contrast">Whether the user has asked for more or less contrast.</param>
/// <param name="ForcedColors">Whether the platform is overriding the palette entirely.</param>
/// <param name="InvertedColors">Whether the platform is showing the surface inverted.</param>
/// <param name="Pointer">What the primary pointing device can do.</param>
/// <param name="AnyPointer">What any attached pointing device can do, as a union.</param>
/// <remarks>
///     <para>
///         ⚠ <b>One value rather than six fields on <see cref="MediaContext" />, because these six
///         come from one place and the other five do not.</b> Width, height and scale are the
///         surface's own and follow a resize; the gamut is the swapchain's; the colour scheme is the
///         platform's appearance. These are the accessibility settings, which arrive together from
///         whatever the host reads them out of — so a host sets one property and a window learns all
///         six, instead of six properties each with its own change check and its own chance to be
///         the one nobody wired.
///     </para>
///     <para>
///         ⚠ <b>Every default is the zero of its type, and that is load-bearing rather than tidy.</b>
///         A positional record struct's parameter defaults are what a <i>constructor call</i> omits,
///         not what <c>default</c> produces — so an axis whose safe answer was not its zero would be
///         safe everywhere except in the state this struct is most often in. See
///         <see cref="PointerCapability" />, which is the axis where that cost a bit.
///     </para>
/// </remarks>
public readonly record struct MediaPreferences(
    MotionPreference Motion = MotionPreference.NoPreference,
    ContrastPreference Contrast = ContrastPreference.NoPreference,
    bool ForcedColors = false,
    bool InvertedColors = false,
    PointerCapability Pointer = PointerCapability.Unspecified,
    PointerCapability AnyPointer = PointerCapability.Unspecified
) {
    /// <summary><see cref="Pointer" /> with an unstated value read as a mouse.</summary>
    public PointerCapability PrimaryPointer =>
        Pointer == PointerCapability.Unspecified ? PointerCapability.Fine : Pointer;

    /// <summary><see cref="AnyPointer" /> with an unstated value read as the primary device alone.</summary>
    /// <remarks>
    ///     ⚠ <b>Falls back to <see cref="PrimaryPointer" /> and not to <see cref="PointerCapability.Fine" />.</b>
    ///     A host that has bothered to say the primary device is a finger and has said nothing about
    ///     the rest has told us what it knows, and answering <c>any-pointer: fine</c> yes for it
    ///     would contradict the one fact it gave — CSS's own note that <c>any-pointer</c> is at least
    ///     as capable as <c>pointer</c>, read the only way that cannot invent a second device.
    /// </remarks>
    public PointerCapability AnyPointerOrPrimary =>
        AnyPointer == PointerCapability.Unspecified ? PrimaryPointer : AnyPointer;
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
/// <param name="Preferences">The platform's accessibility settings.</param>
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
///     <para>
///         ⚠ <b><see cref="Preferences" /> is a value and not six more parameters here</b>, because
///         those six arrive together from the platform while these five each have a source of their
///         own. See <see cref="MediaPreferences" />.
///     </para>
/// </remarks>
public readonly record struct MediaContext(
    float Width,
    float Height,
    float Resolution = 1f,
    ColorSchemePreference ColorScheme = ColorSchemePreference.NoPreference,
    ColorGamut Gamut = ColorGamut.Srgb,
    MediaPreferences Preferences = default
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

            // ⚠ Three media types are understood and only two of them can hold. A Vixen surface is
            // always a screen, so `all` and `screen` are true and `print` is false — and `print`
            // being *understood* rather than refused is the point of naming it: paged media is
            // permanently out of scope (Part 8 § 1 of doc 43), so `print:` is a variant that always
            // resolves and never matches, which costs one comparison and lets a stylesheet shared
            // with a web codebase load unchanged instead of failing a block.
            if (term.Equals("all", StringComparison.OrdinalIgnoreCase)
                || term.Equals("screen", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (term.Equals("print", StringComparison.OrdinalIgnoreCase)) {
                matches = false;
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
            return TryKeyword(
                name,
                terms.Value,
                terms.IsRanged,
                out matches,
                out reason,
                ("no-preference", context.Preferences.Motion == MotionPreference.NoPreference),
                ("reduce", context.Preferences.Motion == MotionPreference.Reduce)
            );
        }

        if (name.Equals("prefers-contrast", StringComparison.OrdinalIgnoreCase)) {
            // ⚠ The boolean form `(prefers-contrast)` is true of *any* stated preference including
            // `custom`, per Media Queries 5 § 8.3 — so it is not a synonym for `more`, which is the
            // reading that would make a high-contrast rule apply to a chosen palette.
            return TryKeyword(
                name,
                terms.Value,
                terms.IsRanged,
                out matches,
                out reason,
                ("no-preference", context.Preferences.Contrast == ContrastPreference.NoPreference),
                ("more", context.Preferences.Contrast == ContrastPreference.More),
                ("less", context.Preferences.Contrast == ContrastPreference.Less),
                ("custom", context.Preferences.Contrast == ContrastPreference.Custom),
                ("", context.Preferences.Contrast != ContrastPreference.NoPreference)
            );
        }

        if (name.Equals("forced-colors", StringComparison.OrdinalIgnoreCase)) {
            return TryKeyword(
                name,
                terms.Value,
                terms.IsRanged,
                out matches,
                out reason,
                ("none", !context.Preferences.ForcedColors),
                ("active", context.Preferences.ForcedColors),
                ("", context.Preferences.ForcedColors)
            );
        }

        if (name.Equals("inverted-colors", StringComparison.OrdinalIgnoreCase)) {
            return TryKeyword(
                name,
                terms.Value,
                terms.IsRanged,
                out matches,
                out reason,
                ("none", !context.Preferences.InvertedColors),
                ("inverted", context.Preferences.InvertedColors),
                ("", context.Preferences.InvertedColors)
            );
        }

        // ⚠ `scripting` is answered from a constant and has no field, because there is no state it
        // could read: a Vixen document is driven by the process that built it, so `enabled` is true
        // by construction and there is no configuration under which it is not. That makes
        // `noscript:` — v4's `(scripting: none)` — a variant that resolves and never matches, the
        // same bargain `print` makes one level up, and a field for it would be a knob whose only
        // honest setting is the one it already has.
        if (name.Equals("scripting", StringComparison.OrdinalIgnoreCase)) {
            return TryKeyword(
                name,
                terms.Value,
                terms.IsRanged,
                out matches,
                out reason,
                ("enabled", true),
                ("none", false),
                ("initial-only", false),
                ("", true)
            );
        }

        if (name.Equals("pointer", StringComparison.OrdinalIgnoreCase)) {
            return TryPointer(name, terms.Value, terms.IsRanged, context.Preferences.PrimaryPointer, out matches, out reason);
        }

        if (name.Equals("any-pointer", StringComparison.OrdinalIgnoreCase)) {
            return TryPointer(name, terms.Value, terms.IsRanged, context.Preferences.AnyPointerOrPrimary, out matches, out reason);
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

    /// <summary>Answers a discrete feature from a table of keyword-to-verdict pairs.</summary>
    /// <param name="name">The feature name, for the diagnostic.</param>
    /// <param name="value">The keyword written, or empty for the boolean form.</param>
    /// <param name="ranged">Whether a range or a <c>min-</c>/<c>max-</c> prefix was used.</param>
    /// <param name="matches">Receives the verdict.</param>
    /// <param name="reason">Receives why it could not be answered.</param>
    /// <param name="options">The keywords, each with what the context says about it.</param>
    /// <returns>Whether it could be answered.</returns>
    /// <remarks>
    ///     ⚠ <b>The empty keyword is the boolean form and is listed per feature rather than
    ///     defaulted, because CSS does not agree with itself about what it means.</b>
    ///     <c>(forced-colors)</c> is true when forcing is active, but <c>(prefers-contrast)</c> is
    ///     true of <i>any</i> stated preference including <c>custom</c> — so a shared rule such as
    ///     "the boolean form is the first non-default keyword" would be right for one feature and
    ///     quietly wrong for the other. A feature that omits it refuses the boolean form, which is
    ///     what <c>orientation</c> and <c>prefers-color-scheme</c> above already do by hand.
    /// </remarks>
    static bool TryKeyword(
        ReadOnlySpan<char> name,
        ReadOnlySpan<char> value,
        bool ranged,
        out bool matches,
        out string? reason,
        params ReadOnlySpan<(string Keyword, bool Held)> options
    ) {
        matches = false;

        if (ranged) {
            reason = $"'{name}' is discrete, so it has no range or min-/max- form";
            return false;
        }

        foreach (var (keyword, held) in options) {
            if (value.Equals(keyword, StringComparison.OrdinalIgnoreCase)) {
                matches = held;
                reason = null;
                return true;
            }
        }

        reason = value.IsEmpty
            ? $"'{name}' has no boolean form in Vixen"
            : $"'{value}' is not a value of '{name}'";

        return false;
    }

    /// <summary>Answers <c>pointer</c> and <c>any-pointer</c> against one capability set.</summary>
    /// <param name="name">Which of the two, for the diagnostic.</param>
    /// <param name="value">The keyword written, or empty for the boolean form.</param>
    /// <param name="ranged">Whether a range or a <c>min-</c>/<c>max-</c> prefix was used.</param>
    /// <param name="capability">What the context says is attached.</param>
    /// <param name="matches">Receives the verdict.</param>
    /// <param name="reason">Receives why it could not be answered.</param>
    /// <returns>Whether it could be answered.</returns>
    /// <remarks>
    ///     ⚠ <b><c>none</c> is the absence of both capabilities rather than the presence of a third
    ///     one</b>, which is what makes <see cref="PointerCapability.Unspecified" /> and
    ///     <see cref="PointerCapability.NoDevice" /> answer it differently: the first has already
    ///     been resolved to a mouse by the time it arrives here, and the second has not.
    /// </remarks>
    static bool TryPointer(
        ReadOnlySpan<char> name,
        ReadOnlySpan<char> value,
        bool ranged,
        PointerCapability capability,
        out bool matches,
        out string? reason
    ) {
        var pointing = capability & (PointerCapability.Coarse | PointerCapability.Fine);

        return TryKeyword(
            name,
            value,
            ranged,
            out matches,
            out reason,
            ("none", pointing == PointerCapability.Unspecified),
            ("coarse", (pointing & PointerCapability.Coarse) != 0),
            ("fine", (pointing & PointerCapability.Fine) != 0),
            ("", pointing != PointerCapability.Unspecified)
        );
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
