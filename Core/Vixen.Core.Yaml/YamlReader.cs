// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Vixen.Core.Yaml;

/// <summary>Turns YAML text into a <see cref="YamlNode" /> tree.</summary>
/// <remarks>
///     <para>
///         Built on YamlDotNet's <b>event stream</b> — <see cref="Scanner" /> and
///         <see cref="Parser" /> — and on nothing else from that library. Its object model and its
///         reflection-driven deserializer are exactly what a <c>.meta</c> file must not go through:
///         type resolution here is the generated <c>TypeRegistry</c>, so reading an asset works on a
///         trimmed NativeAOT build where a reflective deserializer finds no members at all.
///     </para>
///     <para>
///         Comments are read rather than skipped, which is not the default. A migration that rewrote
///         a hundred thousand <c>.meta</c> files and silently deleted every comment an artist had
///         written would be a diff nobody could review, and that is the failure the byte-fidelity
///         requirement in [08](../../../docs/plan/08-asset-pipeline-and-addressables.md) exists to
///         prevent.
///     </para>
/// </remarks>
public static class YamlReader {
    /// <summary>Reads a document.</summary>
    /// <param name="text">The YAML.</param>
    /// <returns>Its root node.</returns>
    /// <exception cref="YamlParseException">It is not YAML, or not in the dialect.</exception>
    public static YamlNode Read(string text) {
        ArgumentNullException.ThrowIfNull(text);
        using var reader = new StringReader(text);
        return Read(reader);
    }

    /// <summary>Reads a document.</summary>
    /// <param name="input">Where the YAML comes from.</param>
    /// <returns>Its root node.</returns>
    /// <exception cref="YamlParseException">It is not YAML, or not in the dialect.</exception>
    public static YamlNode Read(TextReader input) {
        ArgumentNullException.ThrowIfNull(input);

        try {
            var parser = new Parser(new Scanner(input, skipComments: false));
            var state = new ReadState(parser);
            return state.ReadDocument();
        } catch (YamlParseException) {
            throw;
        } catch (YamlException failure) {
            throw new YamlParseException(failure.Message, failure.Start.Line, failure.Start.Column, failure);
        }
#pragma warning disable CA1031 // The two below are a library's bugs, and this is the only place to hold them.
        // ⚠ YamlDotNet does not always keep to its own exception type, and both escapes were found by
        // fuzzing this reader rather than by reading it. `# rwr1ÿFD` — a comment ending in an
        // invalid byte — comes back an EndOfStreamException from ParserExtensions.Accept, and a
        // plain scalar the scanner walks off the end of comes back an InvalidOperationException.
        //
        // Neither is a caller's mistake and neither is distinguishable from any other malformed
        // file, so translating them here is what makes the documented refusal set true. Letting them
        // through means the editor crashes on a .meta somebody committed instead of quarantining it,
        // which is precisely the failure ContentPipeline's `when` filter was written to prevent —
        // and that filter cannot name types nobody knew were thrown.
        catch (Exception failure) when (failure is InvalidOperationException or EndOfStreamException) {
            throw new YamlParseException($"The document is malformed: {failure.Message}", 0, 0, failure);
        }
#pragma warning restore CA1031
    }

    /// <summary>One read in progress: the parser, and the comments waiting for a node to land on.</summary>
    sealed class ReadState(IParser parser) {
        readonly List<string> pending = [];

        YamlNode? last;

        internal YamlNode ReadDocument() {
            Expect<StreamStart>();
            DrainComments();

            if (parser.Accept<StreamEnd>(out _)) {
                // An empty file is an empty mapping rather than an error: a .meta that has been
                // truncated should re-import, not stop the editor opening.
                return new YamlMapping();
            }

            Expect<DocumentStart>();
            var root = ReadNode();

            // Comments after the last node have nowhere else to go, so they trail the root.
            while (parser.Accept<Comment>(out var comment)) {
                parser.MoveNext();
                root.LeadingComments.Add(comment.Value);
            }

            return root;
        }

        YamlNode ReadNode() {
            DrainComments();
            var leading = pending.ToArray();
            pending.Clear();

            YamlNode node;

            if (parser.Accept<Scalar>(out var scalar)) {
                parser.MoveNext();
                node = new YamlScalar(scalar.Value, StyleOf(scalar.Style)) { Tag = TagOf(scalar.Tag) };
            } else if (parser.Accept<SequenceStart>(out var sequenceStart)) {
                parser.MoveNext();
                node = ReadSequence(sequenceStart);
            } else if (parser.Accept<MappingStart>(out var mappingStart)) {
                parser.MoveNext();
                node = ReadMapping(mappingStart);
            } else {
                var current = parser.Current;

                throw new YamlParseException(
                    $"Expected a value and found {current?.GetType().Name ?? "the end of the document"}. "
                    + "Anchors and aliases are not part of this dialect — an asset reference is a 'vx:' scalar.",
                    current?.Start.Line ?? 0,
                    current?.Start.Column ?? 0
                );
            }

            // Where the comments go depends on what the node turned out to be, and that is not known
            // until it has been read. A comment between a key and a block value —
            //
            //     importer: !TextureImporter
            //       # bumping this re-imports everything
            //       version: 3
            //
            // arrives in the event stream *before* the MappingStart the scanner only produces once
            // it has seen the first key. Left on the mapping it would be written out above
            // `importer:`, one level out and above the line it was explaining. It belongs to the
            // first thing inside instead, which is where it was written.
            switch (node) {
                case YamlMapping { Style: YamlCollectionStyle.Block, Count: > 0 } mapping:
                    mapping.Entries[0].Value.LeadingComments.InsertRange(0, leading);
                    break;

                case YamlSequence { Style: YamlCollectionStyle.Block, Count: > 0 } sequence:
                    sequence.Items[0].LeadingComments.InsertRange(0, leading);
                    break;

                default:
                    node.LeadingComments.AddRange(leading);
                    break;
            }

            last = node;
            TakeInlineComment();
            return node;
        }

        YamlSequence ReadSequence(SequenceStart start) {
            var sequence = new YamlSequence {
                Tag = TagOf(start.Tag),
                Style = start.Style == SequenceStyle.Flow ? YamlCollectionStyle.Flow : YamlCollectionStyle.Block
            };

            while (!parser.Accept<SequenceEnd>(out _)) {
                DrainComments();

                if (parser.Accept<SequenceEnd>(out _)) {
                    break;
                }

                sequence.Items.Add(ReadNode());
            }

            parser.MoveNext();
            last = sequence;
            TakeInlineComment();
            return sequence;
        }

        YamlMapping ReadMapping(MappingStart start) {
            var mapping = new YamlMapping {
                Tag = TagOf(start.Tag),
                Style = start.Style == MappingStyle.Flow ? YamlCollectionStyle.Flow : YamlCollectionStyle.Block
            };

            while (!parser.Accept<MappingEnd>(out _)) {
                DrainComments();

                if (parser.Accept<MappingEnd>(out _)) {
                    break;
                }

                // Comments already gathered belong *above the key line*; everything the value's own
                // read picks up came after the key and belongs to the value. Separating the two here
                // is what stops a file header being pushed inside the first block it precedes.
                var beforeKey = pending.ToArray();
                pending.Clear();

                if (!parser.Accept<Scalar>(out var key)) {
                    var current = parser.Current;

                    throw new YamlParseException(
                        "A mapping key must be a plain value. Complex keys — a mapping or a sequence used as a "
                        + "key — are not part of this dialect.",
                        current?.Start.Line ?? 0,
                        current?.Start.Column ?? 0
                    );
                }

                // ⚠ YAML allows an empty key and this dialect does not, so it is refused *here*
                // rather than by the guard on Set. That guard states a caller's contract — a
                // migration that computed a key and got nothing back is a bug in the migration — and
                // a key read out of a file is not a caller, so leaving it to fire meant a one-byte
                // document consisting of `:` came out of this reader as an ArgumentException naming
                // a parameter the caller never passed. Found by fuzzing; the shortest input in the
                // corpus.
                if (key.Value.Length == 0) {
                    throw new YamlParseException(
                        "A mapping key must have a name. An empty key is legal YAML and is not part of this dialect.",
                        key.Start.Line,
                        key.Start.Column
                    );
                }

                parser.MoveNext();

                var value = ReadNode();
                value.LeadingComments.InsertRange(0, beforeKey);
                mapping.Set(key.Value, value);
            }

            parser.MoveNext();
            last = mapping;
            TakeInlineComment();
            return mapping;
        }

        void DrainComments() {
            while (parser.Accept<Comment>(out var comment)) {
                parser.MoveNext();

                if (comment.IsInline && last is not null) {
                    last.TrailingComment = comment.Value;
                } else {
                    pending.Add(comment.Value);
                }
            }
        }

        void TakeInlineComment() {
            if (parser.Accept<Comment>(out var comment) && comment.IsInline) {
                parser.MoveNext();
                last!.TrailingComment = comment.Value;
            }
        }

        void Expect<T>() where T : ParsingEvent {
            if (!parser.Accept<T>(out _)) {
                var current = parser.Current;

                throw new YamlParseException(
                    $"Expected {typeof(T).Name} and found {current?.GetType().Name ?? "nothing"}.",
                    current?.Start.Line ?? 0,
                    current?.Start.Column ?? 0
                );
            }

            parser.MoveNext();
        }

        static YamlScalarStyle StyleOf(ScalarStyle style) =>
            style switch {
                ScalarStyle.SingleQuoted => YamlScalarStyle.SingleQuoted,
                ScalarStyle.DoubleQuoted => YamlScalarStyle.DoubleQuoted,
                ScalarStyle.Literal => YamlScalarStyle.Literal,
                ScalarStyle.Folded => YamlScalarStyle.Folded,
                _ => YamlScalarStyle.Plain
            };

        // A tag arrives as '!Name' for a local tag; the '!' is syntax, and everything above this
        // layer wants the name.
        static string? TagOf(TagName tag) =>
            tag.IsEmpty || tag.IsNonSpecific ? null : tag.Value.TrimStart('!');
    }
}
