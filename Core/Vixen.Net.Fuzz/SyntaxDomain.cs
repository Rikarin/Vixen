// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Text;

namespace Vixen.Net.Fuzz;

/// <summary>A language's inputs, mutated a syntax node at a time.</summary>
/// <remarks>
///     <para>
///         <b>One domain for both front ends, because they are the same tree.</b> VXML and Raven each
///         bring a grammar and nothing else — the green/red nodes, the trivia model and the printer
///         all come from <c>Vixen.Core.Syntax</c> — so everything this needs is on
///         <see cref="SyntaxNode" /> and neither language is named here. A third grammar on the same
///         tree costs a parse delegate and a list of fragments.
///     </para>
///     <para>
///         ⚠ <b>The unit of mutation is a node; the edit is carried in the text, and that is a
///         decision rather than a shortcut.</b> Rewriting the tree itself is the obvious design and it
///         does not survive contact with either front end: VXML has no public way to make a
///         <c>SyntaxToken</c> at all, its generated <c>Update</c> returns a <i>detached</i> node with
///         no parent, and its <c>SyntaxRewriter</c> cannot remove a list element — so a tree rewriter
///         outside the assembly would be reduced to grafting whole subtrees anyway. Choosing the edit
///         from the tree and applying it to the source text gets the same population of inputs with
///         none of that, and it has two properties a rewriter would have had to work for: it needs no
///         internals, and it carries trivia exactly, because a node's span already contains the
///         author's whitespace and comments.
///     </para>
///     <para>
///         What matters is that the <i>unit</i> is grammatical. Replacing an element with an element,
///         duplicating an attribute, deleting a statement — each produces something that lexes and
///         mostly parses, which is the whole reason for the seam. Byte havoc gets a share of the
///         budget anyway; see <see cref="FuzzDomain{T}.GarbageIn" />.
///     </para>
/// </remarks>
public sealed class SyntaxDomain : FuzzDomain<SyntaxDomain.Source> {
    /// <summary>One input: its text, and the nodes an edit may be chosen from.</summary>
    /// <param name="Text">The source.</param>
    /// <param name="Nodes">
    ///     Its nodes, in document order. Empty on a value this domain produced rather than read —
    ///     such a value is serialized immediately and read back through <see cref="TryRead" /> on the
    ///     case that follows, so collecting them again here would be a parse nobody uses.
    /// </param>
    public sealed record Source(string Text, IReadOnlyList<SyntaxNode> Nodes);

    /// <summary>How many nodes an edit is chosen from.</summary>
    /// <remarks>
    ///     A fifty-kilobyte shader has tens of thousands of nodes and walking all of them every case
    ///     costs more than the compile does. Past this the tree is sampled at a stride rather than
    ///     truncated, because taking the first few thousand would confine every edit to the top of
    ///     the file — which for a shader is the imports.
    /// </remarks>
    public const int MaxNodes = 2048;

    readonly Func<string, SyntaxNode> parse;
    readonly IReadOnlyList<string> fragments;
    readonly string what;

    /// <summary>Creates a domain over one grammar.</summary>
    /// <param name="what">What it parses, for a report.</param>
    /// <param name="parse">Text to a tree. Must not throw; if it does, the input is mutated as bytes.</param>
    /// <param name="fragments">Well-formed snippets <see cref="Create" /> builds from.</param>
    /// <param name="seed">What to seed the byte-havoc half with.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SyntaxDomain(string what, Func<string, SyntaxNode> parse, IReadOnlyList<string> fragments, ulong seed)
        : base(seed) {
        ArgumentNullException.ThrowIfNull(what);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(fragments);

        this.what = what;
        this.parse = parse;
        this.fragments = fragments;
    }

    /// <inheritdoc />
    public override string What => what;

    /// <inheritdoc />
    protected override bool TryRead(ReadOnlySpan<byte> bytes, out Source value) {
        value = null!;

        if (bytes.IsEmpty) {
            return false;
        }

        var text = Encoding.UTF8.GetString(bytes);
        SyntaxNode root;

        try {
            root = parse(text);
        }
#pragma warning disable CA1031 // A front end that threw is the *target's* finding; here it is only an entry to mutate as bytes.
        catch (Exception) {
            return false;
        }
#pragma warning restore CA1031

        value = new(text, Collect(root));

        return true;
    }

    /// <inheritdoc />
    protected override byte[] Write(Source value) {
        ArgumentNullException.ThrowIfNull(value);

        return Encoding.UTF8.GetBytes(value.Text);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>One edit per case, where the byte mutator applies up to six.</b> A node's span is an
    ///     offset into the text it came from, and the first splice invalidates every span after it —
    ///     so a second edit from the same collection would land in the wrong place and produce
    ///     exactly the sort of accidental garbage this domain exists to stop generating. Reparsing
    ///     between edits would fix that and cost a parse each; it is not needed, because a mutant
    ///     that is kept is mutated again on a later case, which composes edits at no extra price.
    /// </remarks>
    protected override Source Mutate(Source value, Source other, FuzzRandom random) {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(random);

        if (value.Nodes.Count == 0) {
            return value;
        }

        var target = value.Nodes[random.Below(value.Nodes.Count)];

        return new(
            random.Below(5) switch {
                // Grafting a node of the same kind is the operation this whole design is for: an
                // element where an element was, an expression where an expression was. The result is
                // usually well-formed and almost always reaches the binder, which is where a
                // compiler's interesting defects are and where byte havoc never arrives.
                0 or 1 => Graft(value, target, other, random, full: false),

                // The same with trivia, which moves comments and indentation around and is how the
                // trivia bookkeeping — the half of a printer that round-trip tests exercise least —
                // gets pushed on.
                2 => Graft(value, target, other, random, full: true),

                // Duplicating carries the node's leading trivia with it, so the copy arrives
                // separated from the original rather than welded to it. Two attributes with one
                // name, two declarations of one symbol, a second end tag.
                3 => Splice(value.Text, target.FullSpan, Slice(value.Text, target.FullSpan)),

                // And deleting, which is how a required child goes missing without the text around
                // it becoming nonsense — the shape a recovering parser is most likely to mishandle.
                _ => Splice(value.Text, target.FullSpan, "")
            },
            []
        );
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Concatenated fragments rather than a generated tree. A grammar's real generator would be
    ///     driven off its <c>Syntax.xml</c> and is a larger thing than this; a handful of well-formed
    ///     snippets in random combinations already produces documents no seed is near, which is all
    ///     "from nothing" is here to do.
    /// </remarks>
    protected override Source Create(FuzzRandom random) {
        ArgumentNullException.ThrowIfNull(random);

        var builder = new StringBuilder();

        for (var i = 0; i <= random.Below(4); i++) {
            builder.Append(fragments[random.Below(fragments.Count)]);
        }

        return new(builder.ToString(), []);
    }

    static string Graft(Source value, SyntaxNode target, Source other, FuzzRandom random, bool full) {
        // Preferring the same kind is what makes a graft grammatical. Falling back to any node when
        // there is none is deliberate: an expression where a statement belongs is a shape the binder
        // has to have an answer for, and it is not one the parser will hand it by accident.
        var source = OfKind(other.Nodes, target.RawKind, random)
            ?? (other.Nodes.Count == 0 ? target : other.Nodes[random.Below(other.Nodes.Count)]);

        var from = full ? source.FullSpan : source.Span;
        var into = full ? target.FullSpan : target.Span;
        var text = source == target || other.Nodes.Count == 0 ? value.Text : other.Text;

        return Splice(value.Text, into, Slice(text, from));
    }

    static SyntaxNode? OfKind(IReadOnlyList<SyntaxNode> nodes, int kind, FuzzRandom random) {
        if (nodes.Count == 0) {
            return null;
        }

        // From a random start rather than the first match, so that a graft is not always the same
        // node of its kind — a document has forty identifiers and taking the first one every time
        // would make this one mutation rather than forty.
        var start = random.Below(nodes.Count);

        for (var i = 0; i < nodes.Count; i++) {
            var node = nodes[(start + i) % nodes.Count];

            if (node.RawKind == kind) {
                return node;
            }
        }

        return null;
    }

    /// <summary>The nodes an edit may be chosen from, sampled if there are too many.</summary>
    static List<SyntaxNode> Collect(SyntaxNode root) {
        var all = new List<SyntaxNode>();

        foreach (var node in root.DescendantNodesAndSelf()) {
            all.Add(node);
        }

        if (all.Count <= MaxNodes) {
            return all;
        }

        var stride = (all.Count + MaxNodes - 1) / MaxNodes;
        var sampled = new List<SyntaxNode>(MaxNodes);

        for (var i = 0; i < all.Count; i += stride) {
            sampled.Add(all[i]);
        }

        return sampled;
    }

    static string Slice(string text, TextSpan span) =>
        span.Start < 0 || span.End > text.Length ? "" : text.Substring(span.Start, span.Length);

    static string Splice(string text, TextSpan span, string replacement) =>
        span.Start < 0 || span.End > text.Length
            ? text
            : string.Concat(text.AsSpan(0, span.Start), replacement, text.AsSpan(span.End));
}
