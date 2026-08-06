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
            var parser = new GuardedParser(new Parser(new Scanner(input, skipComments: false)));
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

    /// <summary>YamlDotNet's parser, with what it throws that is not an exception of its own translated.</summary>
    /// <param name="inner">The parser being driven.</param>
    /// <remarks>
    ///     <para>
    ///         <b>A third escape, and the first one the catch above could not be widened to take.</b>
    ///         A malformed tag — <c>!!Te]V</c>, where a shorthand expands to something that is not a
    ///         URI — reaches <c>TagName</c>'s constructor inside <c>Parser.ParseNode</c>, and that
    ///         constructor validates its argument and throws <see cref="ArgumentException" />. So does
    ///         <c>!&lt;&gt;</c>, whose expansion is empty, and so does a <c>%TAG</c> directive
    ///         declaring a prefix that is not a URI. All three are documents somebody committed, not
    ///         calls somebody made.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is why this is a decorator rather than one more type on the filter above.</b>
    ///         <see cref="ArgumentException" /> is the one type on this seam that is genuinely
    ///         ambiguous: <see cref="YamlMapping.Set" /> throws it for an empty key and
    ///         <see cref="YamlScalar" /> throws its null-argument subclass, so a filter around the
    ///         whole read would turn a defect in <see cref="ReadState" /> into "the file is bad" — and
    ///         the empty-key case is a real finding this reader already has, fixed by refusing the
    ///         document rather than by swallowing the exception. An <see cref="ArgumentException" />
    ///         out of <see cref="MoveNext" /> has no such ambiguity: nothing of this assembly's runs
    ///         inside that call, so it came from the library and it is about the document. Narrowing
    ///         it by <c>TargetSite</c> instead would be the same statement made reflectively, and
    ///         would stop being true on a trimmed NativeAOT build — which is the one build this
    ///         reader exists to work on.
    ///     </para>
    ///     <para>
    ///         Only <see cref="MoveNext" /> is guarded, because only it runs the library: <c>Current</c>
    ///         returns a field, and <c>ParserExtensions.Accept</c> — which is all
    ///         <see cref="ReadState" /> calls — reaches the parser through those two members alone.
    ///     </para>
    /// </remarks>
    sealed class GuardedParser(IParser inner) : IParser {
        long line;
        long column;

        public ParsingEvent? Current => inner.Current;

        public bool MoveNext() {
            try {
                var moved = inner.MoveNext();

                // The position of the last event that was read, so that a failure on the *next* one
                // points at somewhere in the file rather than at (0,0). The failing token's own mark
                // is not available: the parser threw before it produced an event carrying it.
                if (inner.Current is { } current) {
                    line = current.End.Line;
                    column = current.End.Column;
                }

                return moved;
            } catch (ArgumentException failure) {
                throw new YamlParseException($"The document is malformed: {failure.Message}", line, column, failure);
            }
        }
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

                // ⚠ A key the document states twice is refused rather than resolved, and the reason is
                // that the format had never said which one wins. YAML requires keys to be unique;
                // nothing here enforced it, so the second entry reached YamlMapping.Set — whose
                // replace-in-place behaviour states a *caller's* contract, for a migration rewriting a
                // value it computed — and quietly became last-wins. MetaScanner reads the same file
                // top-down and stops at the first match, so it is first-wins, and the two answered
                // differently about `metaVersion` on a 142-byte sidecar: 11 against 1
                // (Vixen.Fuzz `meta`, Corpus/meta/4934f8ea81bae860.bin). Neither reader was wrong,
                // because the format defined nothing for either to be wrong about.
                //
                // A duplicate key is what a hand-merged .meta looks like — these are committed text
                // that people resolve conflicts in — and choosing one silently is two compilations of
                // one asset depending on which code path looked. Refused here, alongside the empty key
                // and the complex key, so that it is a parse error naming the file
                // ([08](../../../docs/plan/08-asset-pipeline-and-addressables.md)) rather than a
                // coin toss. Ordinal, because it is a statement about the document's own text.
                if (mapping.TryGet(key.Value, out _)) {
                    throw new YamlParseException(
                        $"The key '{key.Value}' appears more than once in this mapping. YAML requires a mapping's "
                        + "keys to be unique, and this dialect refuses a repeat rather than choosing one of the "
                        + "values — a merge that left both is a file to fix, not a file to guess at.",
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
