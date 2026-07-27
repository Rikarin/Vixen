// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

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

/// <summary>What a media query is asked about.</summary>
/// <param name="Width">The surface width in logical pixels.</param>
/// <param name="Height">The surface height in logical pixels.</param>
/// <param name="Resolution">Device pixels per logical pixel.</param>
/// <param name="ColorScheme">The palette preference.</param>
/// <remarks>
///     A surface rather than a screen. A game's UI can be a full window, a split-screen viewport or a
///     world-space panel on the side of a crate, and all three want <c>@media (max-width: …)</c> to
///     mean "this panel", never "the monitor". Nothing here can be asked about the display.
/// </remarks>
public readonly record struct MediaContext(
    float Width,
    float Height,
    float Resolution = 1f,
    ColorSchemePreference ColorScheme = ColorSchemePreference.NoPreference
);

/// <summary>Evaluates the <c>@media</c> conditions doc 09 lists as supported.</summary>
/// <remarks>
///     <para>
///         A condition is <c>and</c>-joined features, each <c>(name)</c> or <c>(name: value)</c>,
///         with the <c>min-</c> and <c>max-</c> prefixes that carry nearly all real usage. That is
///         deliberately less than CSS Media Queries 4 — no <c>or</c>, no <c>not</c>, no range syntax
///         (<c>width &gt;= 600px</c>).
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

        var colon = feature.IndexOf(':');
        var name = (colon < 0 ? feature : feature[..colon]).Trim();
        var value = colon < 0 ? [] : feature[(colon + 1)..].Trim();

        var comparison = Comparison.Exact;
        if (name.StartsWith("min-", StringComparison.OrdinalIgnoreCase)) {
            comparison = Comparison.AtLeast;
            name = name[4..];
        } else if (name.StartsWith("max-", StringComparison.OrdinalIgnoreCase)) {
            comparison = Comparison.AtMost;
            name = name[4..];
        }

        if (name.Equals("orientation", StringComparison.OrdinalIgnoreCase)) {
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

        if (value.IsEmpty) {
            // `(width)` asks whether the feature is non-zero, which is what CSS means by the
            // boolean form.
            matches = actual != 0f;
            return true;
        }

        if (!TryLength(value, out var wanted)) {
            reason = $"'{value}' is not a length Vixen can compare";
            return false;
        }

        matches = comparison switch {
            Comparison.AtLeast => actual >= wanted,
            Comparison.AtMost => actual <= wanted,
            _ => actual == wanted
        };

        return true;
    }

    static bool TryLength(ReadOnlySpan<char> text, out float value) {
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

    enum Comparison : byte {
        Exact,
        AtLeast,
        AtMost
    }
}
