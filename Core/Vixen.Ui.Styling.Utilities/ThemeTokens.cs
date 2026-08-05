// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Yaml;

namespace Vixen.Ui.Styling.Utilities;

/// <summary>How a dark theme is selected.</summary>
public enum DarkModeStrategy : byte {
    /// <summary>By the surface's <c>prefers-color-scheme</c>.</summary>
    Media,

    /// <summary>By a <c>dark</c> class on an ancestor.</summary>
    Class
}

/// <summary>A font size and the line height that goes with it.</summary>
/// <param name="Size">The font size in pixels.</param>
/// <param name="LineHeight">The line height in pixels.</param>
public readonly record struct FontSizeToken(float Size, float LineHeight);

/// <summary>The design tokens every generated utility is measured in.</summary>
/// <remarks>
///     <para>
///         The whole argument for a utility system is here rather than in the generator: "accent is
///         now teal" is one line of one file, and the editor's two hundred components follow. That is
///         what makes this worth building instead of hand-writing CSS, and it applies more strongly
///         to one monolithic application than it ever did to a website.
///     </para>
///     <para>
///         Tokens are <i>data</i>, deliberately — a YAML asset rather than a config script. It can be
///         hot-reloaded, diffed, and edited by a live theme editor, none of which is true of code.
///     </para>
/// </remarks>
public sealed class ThemeTokens {
    /// <summary>The key a colour family uses for its unqualified form.</summary>
    /// <remarks>
    ///     <c>accent: { DEFAULT: "#4f7cff", hover: "#6a91ff" }</c> makes <c>bg-accent</c> the first
    ///     and <c>bg-accent-hover</c> the second. Uppercase because it is a marker rather than a
    ///     name, and a colour called <c>default</c> is a thing someone might reasonably write.
    /// </remarks>
    public const string DefaultKey = "DEFAULT";

    /// <summary>Colours, keyed by their utility suffix — <c>surface-1</c>, <c>accent</c>.</summary>
    public Dictionary<string, string> Colors { get; } = new(StringComparer.Ordinal);

    /// <summary>Corner radii in pixels, keyed by suffix.</summary>
    public Dictionary<string, float> Radius { get; } = new(StringComparer.Ordinal);

    /// <summary>Shadows, as the <c>box-shadow</c> text they stand for, keyed by suffix.</summary>
    /// <remarks>
    ///     Whole declarations rather than a set of numbers to assemble, because a shadow is a
    ///     designed thing: the offset, the blur and the alpha are chosen together to read as one
    ///     height above the surface, and a scale that let them be picked apart would invite exactly
    ///     the combinations that do not.
    /// </remarks>
    public Dictionary<string, string> Shadow { get; } = new(StringComparer.Ordinal);

    /// <summary>Font sizes, keyed by suffix.</summary>
    public Dictionary<string, FontSizeToken> FontSize { get; } = new(StringComparer.Ordinal);

    /// <summary>Font weights, keyed by suffix.</summary>
    public Dictionary<string, float> FontWeight { get; } = new(StringComparer.Ordinal);

    /// <summary>Breakpoint widths in pixels, keyed by variant name.</summary>
    public Dictionary<string, float> Screens { get; } = new(StringComparer.Ordinal);

    /// <summary>The spacing unit. <c>p-4</c> is four of these.</summary>
    /// <remarks>
    ///     One number rather than a table of named steps, which is the choice that makes the spacing
    ///     scale <i>arithmetic</i>: <c>p-4</c> is twice <c>p-2</c> and everyone can see it. A named
    ///     scale reads better in a design tool and worse in a stylesheet, because nothing about
    ///     <c>p-md</c> says whether it is bigger than <c>p-sm</c> by a little or a lot.
    /// </remarks>
    public float SpacingBase { get; set; } = 4f;

    /// <summary>How a dark theme is selected.</summary>
    public DarkModeStrategy DarkMode { get; set; } = DarkModeStrategy.Media;

    /// <summary>The globs to scan for utilities in use.</summary>
    public List<string> Content { get; } = [];

    /// <summary>Everything that could not be read, and why.</summary>
    public List<string> Diagnostics { get; } = [];

    /// <summary>Reads a <c>vixen.ui.yaml</c>.</summary>
    /// <param name="yaml">Its text.</param>
    /// <returns>The tokens.</returns>
    /// <remarks>
    ///     Read from the YAML DOM rather than deserialised into a record, because the shapes are
    ///     genuinely heterogeneous: a colour is a string <i>or</i> a map of shades, and a font size
    ///     is a pair. Forcing that through a typed deserialiser would mean either a schema nobody
    ///     wants to write or a converter per field, and the DOM walk is shorter than either.
    ///     <para>
    ///         ⚠ <b>A file that is not YAML at all is a diagnostic like any other, not an exception.</b>
    ///         Everything else this reads reports through <see cref="Diagnostics" /> — a colour that is
    ///         a list, a radius that is not a number, a root that is not a mapping — and a theme file
    ///         is hand-written, so the one failure that threw was the likeliest one to happen. It came
    ///         out of <see cref="YamlReader" /> and stopped whoever was building a stylesheet, with the
    ///         other diagnostics in the file unreported.
    ///     </para>
    /// </remarks>
    public static ThemeTokens Parse(string yaml) {
        ArgumentNullException.ThrowIfNull(yaml);

        var tokens = new ThemeTokens();
        YamlNode document;

        try {
            document = YamlReader.Read(yaml);
        } catch (YamlParseException failure) {
            tokens.Diagnostics.Add($"the theme file is not YAML: {failure.Message}");
            return tokens;
        }

        if (document is not YamlMapping root) {
            tokens.Diagnostics.Add("the theme file is not a mapping");
            return tokens;
        }

        if (root["theme"] is YamlMapping theme) {
            tokens.ReadColors(theme["colors"]);
            tokens.ReadSpacing(theme["spacing"]);
            tokens.ReadNumbers(theme["radius"], tokens.Radius, "radius");
            tokens.ReadNumbers(theme["fontWeight"], tokens.FontWeight, "fontWeight");
            tokens.ReadNumbers(theme["screens"], tokens.Screens, "screens");
            tokens.ReadFontSizes(theme["fontSize"]);
            tokens.ReadStrings(theme["shadow"], tokens.Shadow, "shadow");
        }

        if (root["darkMode"] is YamlScalar dark) {
            tokens.DarkMode = dark.Value.Equals("class", StringComparison.OrdinalIgnoreCase)
                ? DarkModeStrategy.Class
                : DarkModeStrategy.Media;
        }

        if (root["content"] is YamlSequence content) {
            foreach (var item in content) {
                if (item is YamlScalar glob) {
                    tokens.Content.Add(glob.Value);
                }
            }
        }

        return tokens;
    }

    /// <summary>Looks a colour up, allowing for the <c>DEFAULT</c> shade.</summary>
    /// <param name="name">The utility suffix, such as <c>accent</c> or <c>surface-1</c>.</param>
    /// <param name="value">Receives the colour as written in the theme.</param>
    /// <returns>Whether it is a known colour.</returns>
    public bool TryGetColor(string name, out string value) {
        ArgumentNullException.ThrowIfNull(name);
        return Colors.TryGetValue(name, out value!);
    }

    void ReadColors(YamlNode? node) {
        if (node is not YamlMapping families) {
            return;
        }

        foreach (var (family, value) in families) {
            switch (value) {
                case YamlScalar single:
                    Colors[family] = single.Value;
                    break;

                case YamlMapping shades: {
                    foreach (var (shade, colour) in shades) {
                        if (colour is not YamlScalar text) {
                            Diagnostics.Add($"colour '{family}-{shade}' is not a value");
                            continue;
                        }

                        Colors[shade == DefaultKey ? family : $"{family}-{shade}"] = text.Value;
                    }

                    break;
                }

                default:
                    Diagnostics.Add($"colour '{family}' is neither a value nor a set of shades");
                    break;
            }
        }
    }

    void ReadSpacing(YamlNode? node) {
        if (node is YamlMapping spacing && spacing["base"] is YamlScalar value && TryNumber(value.Value, out var number)) {
            SpacingBase = number;
            return;
        }

        if (node is YamlScalar direct && TryNumber(direct.Value, out var scalar)) {
            SpacingBase = scalar;
        }
    }

    void ReadStrings(YamlNode? node, Dictionary<string, string> into, string what) {
        if (node is not YamlMapping mapping) {
            return;
        }

        foreach (var (key, value) in mapping) {
            if (value is YamlScalar scalar) {
                into[key] = scalar.Value;
                continue;
            }

            Diagnostics.Add($"{what} '{key}' is not a value");
        }
    }

    void ReadNumbers(YamlNode? node, Dictionary<string, float> into, string what) {
        if (node is not YamlMapping mapping) {
            return;
        }

        foreach (var (key, value) in mapping) {
            if (value is YamlScalar scalar && TryNumber(scalar.Value, out var number)) {
                into[key] = number;
                continue;
            }

            Diagnostics.Add($"{what} '{key}' is not a number");
        }
    }

    void ReadFontSizes(YamlNode? node) {
        if (node is not YamlMapping mapping) {
            return;
        }

        foreach (var (key, value) in mapping) {
            switch (value) {
                // `base: 14` is allowed and means "no opinion about line height", which is what the
                // 1.4 multiplier below stands in for. Writing the pair is how you take one.
                case YamlScalar single when TryNumber(single.Value, out var size):
                    FontSize[key] = new FontSizeToken(size, MathF.Round(size * 1.4f));
                    break;

                case YamlSequence pair when pair.Count == 2
                    && pair[0] is YamlScalar first
                    && pair[1] is YamlScalar second
                    && TryNumber(first.Value, out var fontSize)
                    && TryNumber(second.Value, out var lineHeight):
                    FontSize[key] = new FontSizeToken(fontSize, lineHeight);
                    break;

                default:
                    Diagnostics.Add($"fontSize '{key}' is neither a size nor a size and line height");
                    break;
            }
        }
    }

    static bool TryNumber(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
