// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Globalization;
using System.Text;

namespace Vixen.Core.Yaml;

/// <summary>Writes a <see cref="YamlNode" /> tree in the Vixen dialect.</summary>
/// <remarks>
///     <para>
///         Vixen's own rather than YamlDotNet's emitter, because the dialect
///         [08](../../../docs/plan/08-asset-pipeline-and-addressables.md) fixes is narrower than
///         anything that emitter can be configured into, and because byte fidelity is a requirement
///         here rather than a nicety: a migration that rewrites every <c>.meta</c> file in a project
///         must produce a diff of the lines it meant to change and no others.
///     </para>
///     <para>
///         The dialect: two-space indent, block style, flow style for small all-scalar collections,
///         no document-start marker, keys in the order the mapping holds them (which is the order
///         the C# record declares them), <c>\n</c> line endings, and a trailing newline.
///     </para>
/// </remarks>
public static class YamlWriter {
    /// <summary>
    ///     How wide a flow collection may render, including its indent, before it is written as a
    ///     block instead.
    /// </summary>
    /// <remarks>
    ///     Sixty, and the number is not arbitrary. It is the smallest round number that keeps every
    ///     flow collection in doc 08's worked examples on one line — <c>{ u: Repeat, v: Repeat }</c>,
    ///     <c>{ count: 3, reduction: 0.5 }</c>, <c>[ui, hd]</c>, and a <c>subAssets</c> entry at 46
    ///     columns — while sending that document's <c>materialMapping</c>, whose two GUID values
    ///     come to over ninety, to the block form the document shows it in. A wider threshold would
    ///     make a one-line change to one material a one-line diff of the whole mapping.
    /// </remarks>
    public const int FlowWidthLimit = 60;

    const string Indent = "  ";

    /// <summary>Writes a document.</summary>
    /// <param name="root">The root node.</param>
    /// <returns>The YAML, ending in a newline.</returns>
    public static string Write(YamlNode root) {
        ArgumentNullException.ThrowIfNull(root);
        var builder = new StringBuilder();
        WriteTo(builder, root);
        return builder.ToString();
    }

    /// <summary>Writes a document.</summary>
    /// <param name="output">Where to write it.</param>
    /// <param name="root">The root node.</param>
    public static void Write(TextWriter output, YamlNode root) {
        ArgumentNullException.ThrowIfNull(output);
        output.Write(Write(root));
    }

    static void WriteTo(StringBuilder builder, YamlNode root) {
        WriteComments(builder, root.LeadingComments, 0);

        switch (root) {
            case YamlMapping { Count: 0 }:
            case YamlSequence { Count: 0 }:
                // An empty root has no block form at all; '{}' is what it is.
                builder.Append(Flow(root)).Append('\n');
                break;

            // The root is always a block, however small it is and whatever style it carries. A .meta
            // file with two keys is still a file whose next edit adds a third, and one that rendered
            // as `{ guid: abc, metaVersion: 1 }` because it happened to be short would turn that edit
            // into a whole-line diff — which is the one thing the dialect exists to avoid.
            case YamlMapping mapping:
                WriteTag(builder, mapping, 0);
                WriteBlockMapping(builder, mapping, 0);
                break;

            case YamlSequence sequence:
                WriteTag(builder, sequence, 0);
                WriteBlockSequence(builder, sequence, 0);
                break;

            default:
                builder.Append(Tagged(root, Flow(root)));
                WriteTrailing(builder, root);
                builder.Append('\n');
                break;
        }

        // A file that is only comments still ends in a newline, and one that is nothing at all is
        // an empty file rather than a stray newline.
        if (builder.Length > 0 && builder[^1] != '\n') {
            builder.Append('\n');
        }
    }

    static void WriteBlockMapping(StringBuilder builder, YamlMapping mapping, int depth) {
        foreach (var (key, value) in mapping.Entries) {
            WriteComments(builder, value.LeadingComments, depth);
            AppendIndent(builder, depth).Append(EmitKey(key)).Append(':');
            WriteValue(builder, value, depth);
        }
    }

    static void WriteBlockSequence(StringBuilder builder, YamlSequence sequence, int depth) {
        foreach (var item in sequence.Items) {
            WriteComments(builder, item.LeadingComments, depth);
            AppendIndent(builder, depth).Append('-');

            if (item is YamlMapping nested && !IsFlow(nested, depth + 1) && nested.Count > 0 && nested.Tag is null) {
                // The compact form: the first key shares the dash's line and the rest line up under
                // it. Anything else costs a line per item for no gain, and is not what doc 08 shows.
                var first = true;

                foreach (var (key, value) in nested.Entries) {
                    if (first) {
                        builder.Append(' ').Append(EmitKey(key)).Append(':');
                        WriteValue(builder, value, depth + 1);
                        first = false;
                        continue;
                    }

                    WriteComments(builder, value.LeadingComments, depth + 1);
                    AppendIndent(builder, depth + 1).Append(EmitKey(key)).Append(':');
                    WriteValue(builder, value, depth + 1);
                }

                continue;
            }

            WriteValue(builder, item, depth);
        }
    }

    /// <summary>Writes what comes after a <c>key:</c> or a <c>-</c>, block or inline as it deserves.</summary>
    static void WriteValue(StringBuilder builder, YamlNode value, int depth) {
        switch (value) {
            case YamlScalar scalar:
                builder.Append(' ').Append(Tagged(value, EmitScalar(scalar, flow: false)));
                WriteTrailing(builder, value);
                builder.Append('\n');
                return;

            case YamlMapping { Count: 0 }:
            case YamlSequence { Count: 0 }:
                builder.Append(' ').Append(Tagged(value, Flow(value)));
                WriteTrailing(builder, value);
                builder.Append('\n');
                return;

            case YamlMapping mapping when IsFlow(mapping, depth + 1):
            case YamlSequence when IsFlow(value, depth + 1):
                builder.Append(' ').Append(Tagged(value, Flow(value)));
                WriteTrailing(builder, value);
                builder.Append('\n');
                return;

            case YamlMapping mapping:
                WriteTagInline(builder, mapping);
                WriteTrailing(builder, value);
                builder.Append('\n');
                WriteBlockMapping(builder, mapping, depth + 1);
                return;

            case YamlSequence sequence:
                WriteTagInline(builder, sequence);
                WriteTrailing(builder, value);
                builder.Append('\n');
                WriteBlockSequence(builder, sequence, depth + 1);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown node kind.");
        }
    }

    static void WriteComments(StringBuilder builder, List<string> comments, int depth) {
        foreach (var comment in comments) {
            AppendIndent(builder, depth).Append(Comment(comment)).Append('\n');
        }
    }

    static void WriteTrailing(StringBuilder builder, YamlNode node) {
        if (node.TrailingComment is { } comment) {
            builder.Append(' ').Append(Comment(comment));
        }
    }

    /// <summary>
    ///     The one place the emitter normalises rather than reproduces: a comment is written
    ///     <c>#&#160;text</c>, one space, whatever it was written with.
    /// </summary>
    /// <remarks>
    ///     Not a choice — YamlDotNet's scanner hands back the comment with its leading whitespace
    ///     already gone, so there is nothing to reproduce. Naming it here rather than pretending
    ///     otherwise: the round trip is byte-identical for everything else, and for comments it is
    ///     idempotent, which is what a migration actually needs. Writing twice changes nothing, so
    ///     the normalisation costs one diff line the first time a file is rewritten and never again.
    /// </remarks>
    static string Comment(string comment) => comment.Length == 0 ? "#" : "# " + comment;

    static void WriteTag(StringBuilder builder, YamlNode node, int depth) {
        if (node.Tag is { } tag) {
            AppendIndent(builder, depth).Append('!').Append(tag).Append('\n');
        }
    }

    static void WriteTagInline(StringBuilder builder, YamlNode node) {
        if (node.Tag is { } tag) {
            builder.Append(" !").Append(tag);
        }
    }

    static string Tagged(YamlNode node, string body) => node.Tag is { } tag ? $"!{tag} {body}" : body;

    static StringBuilder AppendIndent(StringBuilder builder, int depth) => builder.Insert(builder.Length, Indent, depth);

    /// <summary>Whether a collection should be written on one line.</summary>
    static bool IsFlow(YamlNode node, int depth) {
        switch (node) {
            case YamlMapping mapping:
                if (mapping.Style == YamlCollectionStyle.Block) {
                    return false;
                }

                if (mapping.Style == YamlCollectionStyle.Flow) {
                    return true;
                }

                return mapping.Entries.All(entry => entry.Value is YamlScalar)
                    && Fits(mapping, depth);

            case YamlSequence sequence:
                if (sequence.Style == YamlCollectionStyle.Block) {
                    return false;
                }

                if (sequence.Style == YamlCollectionStyle.Flow) {
                    return true;
                }

                return sequence.Items.All(item => item is YamlScalar) && Fits(sequence, depth);

            default:
                return false;
        }
    }

    static bool Fits(YamlNode node, int depth) =>
        Flow(node).Length + (depth * Indent.Length) <= FlowWidthLimit;

    /// <summary>Renders a node on one line.</summary>
    static string Flow(YamlNode node) {
        switch (node) {
            case YamlScalar scalar:
                return EmitScalar(scalar, flow: true);

            case YamlSequence sequence:
                return sequence.Count == 0
                    ? "[]"
                    : "[" + string.Join(", ", sequence.Items.Select(item => Tagged(item, Flow(item)))) + "]";

            case YamlMapping mapping:
                return mapping.Count == 0
                    ? "{}"
                    : "{ "
                    + string.Join(
                        ", ",
                        mapping.Entries.Select(entry => $"{EmitKey(entry.Key)}: {Tagged(entry.Value, Flow(entry.Value))}")
                    )
                    + " }";

            default:
                throw new ArgumentOutOfRangeException(nameof(node), node, "Unknown node kind.");
        }
    }

    static string EmitKey(string key) => IsUnsafeBare(key, flow: true) || ReadsAsAnotherType(key) ? Quote(key) : key;

    static string EmitScalar(YamlScalar scalar, bool flow) =>
        scalar.Style switch {
            // Plain says "this does not need quotes", and for anything the reader produced that is
            // true by construction. It is still checked, because a tree built by hand can say it
            // about a value that would break the document — and honouring that would write a file
            // that no longer parses. What Plain does buy is the type check being skipped: `2048` as
            // a number is exactly what it should look like.
            YamlScalarStyle.Plain => IsUnsafeBare(scalar.Value, flow) ? Quote(scalar.Value) : scalar.Value,
            YamlScalarStyle.SingleQuoted => Quote(scalar.Value),
            YamlScalarStyle.DoubleQuoted => DoubleQuote(scalar.Value),
            YamlScalarStyle.Literal or YamlScalarStyle.Folded => DoubleQuote(scalar.Value),
            _ => IsUnsafeBare(scalar.Value, flow) || ReadsAsAnotherType(scalar.Value)
                ? Quote(scalar.Value)
                : scalar.Value
        };

    /// <summary>Whether writing this bare would break the document's structure.</summary>
    /// <remarks>
    ///     A colon is only a key separator when followed by a space, so <c>vx:9e8a44c9…</c> is a
    ///     perfectly good plain scalar in block context. In flow context it is not, because a reader
    ///     there is looking for keys, so a colon anywhere is enough to quote.
    /// </remarks>
    static bool IsUnsafeBare(string value, bool flow) =>
        value.Length == 0
        || value != value.Trim()
        || value.AsSpan().ContainsAny(LineBreaks)
        || StartsWithAnIndicator(value)
        || value.Contains(": ", StringComparison.Ordinal)
        || value.Contains(" #", StringComparison.Ordinal)
        || (flow && value.AsSpan().ContainsAny(FlowIndicators));

    /// <summary>Whether writing this bare would round-trip as something that is not a string.</summary>
    /// <remarks>
    ///     Only asked of <see cref="YamlScalarStyle.Any" />, which means "a string": the object
    ///     mapper marks numbers, booleans and enums <see cref="YamlScalarStyle.Plain" /> because it
    ///     is the only layer that knows. So a string that happens to read as <c>true</c>, or as
    ///     <c>2048</c>, is quoted — otherwise a version field typed as text comes back a number the
    ///     first time somebody writes <c>1.20</c>.
    /// </remarks>
    static bool ReadsAsAnotherType(string value) {
        foreach (var reserved in Reserved) {
            if (string.Equals(value, reserved, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>Whether the first character makes the whole value mean something structural.</summary>
    /// <remarks>
    ///     <c>-</c>, <c>?</c> and <c>:</c> only mean "sequence entry", "complex key" and "key
    ///     separator" when a space follows them, so <c>-8</c> is a number and not the start of a
    ///     list. Treating them as indicators unconditionally would put every negative number in the
    ///     engine in quotes.
    /// </remarks>
    static bool StartsWithAnIndicator(string value) =>
        value[0] is '-' or '?' or ':'
            ? value.Length == 1 || value[1] == ' '
            : Indicators.Contains(value[0]);

    static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    static string DoubleQuote(string value) {
        var builder = new StringBuilder(value.Length + 2).Append('"');

        foreach (var character in value) {
            _ = character switch {
                '"' => builder.Append("\\\""),
                '\\' => builder.Append("\\\\"),
                '\n' => builder.Append("\\n"),
                '\r' => builder.Append("\\r"),
                '\t' => builder.Append("\\t"),
                _ => builder.Append(character)
            };
        }

        return builder.Append('"').ToString();
    }

    static readonly SearchValues<char> LineBreaks = SearchValues.Create("\n\r\t");

    static readonly SearchValues<char> FlowIndicators = SearchValues.Create(":,[]{}");

    static readonly char[] Indicators = "-?:,[]{}#&*!|>'\"%@`".ToCharArray();

    static readonly string[] Reserved = ["true", "false", "null", "~", "yes", "no", "on", "off"];
}
