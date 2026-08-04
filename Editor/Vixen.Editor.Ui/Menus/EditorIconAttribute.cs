// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Ui;

/// <summary>Declares what a type looks like, as SVG path data or a file beside its assembly.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D6, spelled the way doc 36 spells it.</b> This was documented as a mechanism
///         that could not be built — "there is no SVG path parser in this repository and its absence
///         is a decision" — and the decision was that turning <c>M12 2L2 22h20z</c> into segments
///         belongs to an asset pipeline. There is a parser now (<see cref="SvgPath" />), it is a
///         hundred and fifty lines, and the argument it was resting on does not survive that: a
///         component's icon is one string, and asking every game to register one in code was asking
///         them to transcribe path data into <c>LineTo</c> calls by hand.
///     </para>
///     <code language="csharp">
///         [EditorIcon("M12 2 2 22h20z", Tint = "#7cc4ff")]
///         public struct Health { … }
///
///         [EditorIcon("Icons/health.svg")]
///         public struct Stamina { … }
///     </code>
///     <para>
///         ⚠ <b>Two forms, told apart by the extension, because both are what people actually
///         have.</b> An icon copied out of Material or Lucide is a <c>d</c> string and wants no file
///         and no IO; an icon a designer drew is an <c>.svg</c> on disk beside the plugin. Anything
///         ending in <c>.svg</c> is a path relative to the declaring plugin's directory, and anything
///         else is the data itself.
///     </para>
///     <para>
///         ⚠ <b>A bad icon is a diagnostic, never an exception.</b> The string is written by somebody
///         who is not here, and one component with a stray character in its path data must not be a
///         plugin that will not load — the same rule <c>PluginDiscovery</c> keeps for a manifest that
///         does not parse.
///     </para>
/// </remarks>
/// <param name="source">SVG path data, or a path to an <c>.svg</c> file beside the assembly.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed partial class EditorIconAttribute(string source) : Attribute {
    /// <summary>The path data, or the file.</summary>
    public string Source { get; } = source;

    /// <summary>What to fill it with — <c>#rrggbb</c>, or empty for the inherited text colour.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty means "follow the theme" and is the right default.</b> A literal colour is what
    ///     a file-type glyph in a grid wants, where being scannable by hue is the whole job; a
    ///     component's icon sits on a row of text and wants that row's colour, including when the row
    ///     is selected and the background has gone dark under it. See <see cref="IconPaint" />.
    /// </remarks>
    public string Tint { get; init; } = string.Empty;

    /// <summary>The grid the data was drawn against. Twenty-four square unless it says otherwise.</summary>
    /// <remarks>
    ///     Material, Lucide, Feather and Fluent all author against 24; the property is here for the
    ///     sets that do not, and because an icon scaled against the wrong box is off-centre rather
    ///     than obviously broken.
    /// </remarks>
    public float ViewBox { get; init; } = 24f;

    /// <summary>Which of two declarations for one type wins; the higher one.</summary>
    public int Order { get; init; }

    /// <summary>Turns the declaration into art.</summary>
    /// <param name="directory">
    ///     Where the declaring plugin lives, for the file form. Empty for a built-in, which cannot use
    ///     it.
    /// </param>
    /// <param name="reason">Why it could not be read, when it could not.</param>
    /// <returns>The art, or <see langword="null" />.</returns>
    /// <remarks>
    ///     ⚠ <b>The file is read relative to the plugin's own directory and is not allowed to leave
    ///     it.</b> A manifest is a file a third party wrote; <c>"../../../etc/passwd"</c> in an
    ///     attribute would otherwise be a plugin reading whatever it likes through a mechanism nobody
    ///     was watching. It is the same containment <c>PluginDiscovery</c> gives an assembly path.
    /// </remarks>
    public IconArt? Resolve(string directory, out string? reason) {
        reason = null;

        var source = Source?.Trim() ?? string.Empty;

        if (source.Length == 0) {
            reason = "names no icon";
            return null;
        }

        var data = source;

        if (source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) {
            if (Read(directory, source, out data, out reason) is false) {
                return null;
            }
        }

        if (SvgPath.TryParse(data) is not { Count: > 0 } geometry) {
            reason = "is not path data this build can read";
            return null;
        }

        var box = ViewBox > 0f ? ViewBox : 24f;

        return new IconArt(
            new Rectangle(0f, 0f, box, box),
            [new IconPath(geometry, Paint(Tint), FillRule: PathFillRule.NonZero)]
        );
    }

    /// <summary>Reads an <c>.svg</c> and pulls the path data out of it.</summary>
    /// <remarks>
    ///     ⚠ <b>Every <c>d</c> in the file, joined, rather than the first.</b> An icon exported from a
    ///     drawing tool is routinely several subpaths as several <c>&lt;path&gt;</c> elements, and
    ///     taking the first would draw a third of the picture — which reads as the icon being wrong
    ///     rather than as the reader being lazy. They share one paint, because that is all
    ///     <see cref="Tint" /> can say; an icon that needs a colour per path is one somebody registers
    ///     an <see cref="IconArt" /> for directly.
    /// </remarks>
    static bool Read(string directory, string source, out string data, out string? reason) {
        data = string.Empty;
        reason = null;

        if (string.IsNullOrEmpty(directory)) {
            reason = "names a file, and a built-in module has no directory to read it from";
            return false;
        }

        var root = Path.GetFullPath(directory);
        var file = Path.GetFullPath(Path.Combine(root, source));

        if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            reason = "names a file outside the plugin's own folder";
            return false;
        }

        string text;

        try {
            text = File.ReadAllText(file);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            reason = $"names {source}, which could not be read: {exception.Message}";
            return false;
        }

        var found = Data().Matches(text);

        if (found.Count == 0) {
            reason = $"names {source}, which has no path in it";
            return false;
        }

        data = string.Join(' ', found.Select(match => match.Groups[1].Value));
        return true;
    }

    /// <summary>A <c>d="…"</c> attribute, in either quote.</summary>
    /// <remarks>
    ///     ⚠ <b>A regular expression rather than an XML parse, and the bound is deliberate.</b> What
    ///     is wanted from the file is its geometry; a full SVG reader is transforms, groups, gradients,
    ///     <c>&lt;use&gt;</c> and a stylesheet language, which is an asset importer rather than an
    ///     attribute. This reads what an icon set exports and says so plainly — anything richer is a
    ///     picture that will not come out right, and it is better for that to be visible.
    /// </remarks>
    [GeneratedRegex("""\bd\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex Data();

    /// <summary>The paint a tint names.</summary>
    static IconPaint Paint(string tint) {
        var text = tint.Trim();

        if (text.Length == 0) {
            return IconPaint.Foreground;
        }

        // ⚠ A custom property passes through as one, so an icon can say `--danger` and follow a
        // retheme. It is the case a literal cannot serve and costs one branch to offer.
        if (text.StartsWith("--", StringComparison.Ordinal)) {
            return IconPaint.Named(text);
        }

        return Colour(text) is { } colour ? IconPaint.Of(colour) : IconPaint.Foreground;
    }

    /// <summary>A <c>#rgb</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c> colour.</summary>
    static Color4? Colour(string text) {
        var digits = text.StartsWith('#') ? text[1..] : text;

        if (digits.Length == 3) {
            digits = string.Concat(digits.Select(character => new string(character, 2)));
        }

        if (digits.Length is not (6 or 8)) {
            return null;
        }

        Span<float> channels = [0f, 0f, 0f, 1f];

        for (var index = 0; index * 2 < digits.Length; index++) {
            if (!byte.TryParse(digits.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) {
                return null;
            }

            channels[index] = value / 255f;
        }

        return new Color4(channels[0], channels[1], channels[2], channels[3]);
    }
}
