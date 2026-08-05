// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Text;
using Vixen.Ui.Markup.Syntax;

namespace Vixen.Fuzz.Targets;

/// <summary>A <c>.vxml</c> file, parsed, printed, and reparsed after an edit.</summary>
/// <remarks>
///     <para>
///         <b>The first target here whose input is a language rather than a format, and the first to
///         need <see cref="IFuzzDomain" />.</b> Byte havoc over a markup file produces text that dies
///         in the lexer; what is worth reaching is behind it — the recovery paths that fabricate a
///         missing token, the trivia bookkeeping that carries skipped source, and the incremental
///         reparser that decides which subtrees it may keep. See <c>SyntaxDomain</c> for how an edit
///         is chosen from the tree and carried in the text.
///     </para>
///     <para>
///         ⚠ <b>"Nothing escapes" is the weakest thing this target checks, and on its own it would
///         prove very little.</b> The parser is total by design — every file produces a tree, missing
///         tokens are fabricated zero-width, unusable source travels as trivia — so it has no
///         throwing paths to find and a run that only watched for exceptions would come back clean
///         for ever. What can go wrong is a tree that is <i>wrong</i>: one that no longer reproduces
///         its file, or one an incremental reparse disagrees with. Both are silent, both are checked
///         here, and neither is visible to any of the four oracles.
///     </para>
///     <para>
///         <b>Print-parse-print stability is deliberately not one of them, because it is implied.</b>
///         The round-trip below is byte-exact over <i>arbitrary</i> text, valid or not — so printing
///         a parse is the identity function, and asking whether a second round of it is stable is
///         asking the same question twice. The reparse oracle is the one that is not free.
///     </para>
/// </remarks>
public sealed class VxmlTarget : IFuzzTarget {
    /// <summary>What the parse is told the file is called.</summary>
    /// <remarks>
    ///     Fixed, and it has to be: a diagnostic carries its file path, and the incremental and full
    ///     parses below are compared on their diagnostics. Two different paths would make every
    ///     comparison fail for a reason that has nothing to do with either tree.
    /// </remarks>
    const string Path = "Fuzz.vxml";

    /// <inheritdoc />
    public string Name => "vxml";

    /// <inheritdoc />
    public string What => "VXML parsed, printed, and reparsed incrementally against a full parse";

    /// <inheritdoc />
    public IFuzzDomain Domain { get; } = new SyntaxDomain(
        "VXML documents, one syntax node at a time",
        text => SyntaxTree.ParseText(text, Path).GetRoot(),
        MarkupSeeds.Fragments,
        0x7658_C0DE_2026_0805ul
    );

    /// <summary>Whether novelty is worth keeping an input for.</summary>
    /// <remarks>
    ///     No. A markup document's behaviour is the set of diagnostics it produced and the shape of
    ///     the tree behind them, and there are more of those than the signature table holds — it
    ///     saturates in the first seconds, after which the guidance is worth nothing and the corpus
    ///     would freeze at whatever those seconds happened to leave. The domain reaches the branches
    ///     that guidance was buying, so this is the case <see cref="IFuzzTarget.NoveltyGuides" /> was
    ///     added for.
    /// </remarks>
    public bool NoveltyGuides => false;

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Three parses, not one, and the number says so.</b> This target parses the input,
    ///         then reparses an edited copy twice — incrementally and fully — because that
    ///         disagreement is the defect worth finding. A single parse builds a green node and a red
    ///         overlay per token, which for a markup file whose tokens average a few characters is
    ///         the bulk of this multiple; the rest is the two extra trees and the strings the
    ///         comparison prints.
    ///     </para>
    ///     <para>
    ///         It is still a multiple of the input rather than a constant, which is the property
    ///         worth defending and one a parser can lose: a recovery path that fabricates a token per
    ///         character, or a trivia list that is rebuilt per token rather than shared, turns a
    ///         linear parser into a quadratic one and this is what notices.
    ///     </para>
    /// </remarks>
    public long AllowanceFor(int inputLength) => 65536 + (768L * inputLength);

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        foreach (var text in MarkupSeeds.Documents) {
            corpus.Add(Encoding.UTF8.GetBytes(text));
        }
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var text = Encoding.UTF8.GetString(input);
        var tree = SyntaxTree.ParseText(text, Path);
        var root = tree.GetRoot();

        // ⚠ The property everything else rests on, and it holds for malformed input too — that is the
        // design, not a happy accident. A tree that cannot reproduce its file cannot be formatted,
        // cannot be edited incrementally, and cannot map a span back to what the author wrote. It is
        // asserted on 28 sources next to the parser; here it is asserted on everything the domain
        // can build out of them.
        var printed = root.ToFullString();

        if (!string.Equals(printed, text, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"The parse does not reproduce its source: {Where(text, printed)}."
            );
        }

        long signature = (17 * 31) + tree.Diagnostics.Count;

        foreach (var diagnostic in tree.Diagnostics) {
            var id = diagnostic.Descriptor.Id;

            // Every id a VXML parse can report is VXML1xxx for syntax or VXML2xxx for binding, and
            // the shape is worth checking rather than assuming: an id built by string concatenation
            // somewhere, or a descriptor left with a placeholder, is a diagnostic no suppression file
            // and no documentation page can name.
            if (!IsMarkupId(id)) {
                throw new InvalidOperationException($"A VXML parse reported '{id}', which is not a VXML syntax id.");
            }

            signature = (signature * 31) + Fold(id);
        }

        signature = (signature * 31) + Shape(root, 0);

        return (signature * 31) + Reparse(tree, text);
    }

    /// <summary>Edits the file and requires the incremental reparse to equal a full one.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Incremental-versus-full is a classic bug farm and this is the cheapest possible
    ///         assay of it.</b> The reparser keeps a subtree when the new token stream reads the old
    ///         characters the same way; every condition on that is a chance to keep one it should
    ///         have thrown away, and the result is a tree that is subtly not the file — which nothing
    ///         downstream can detect and which only shows up as a binding error against a line the
    ///         author did not write.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The edit is derived from the input's own bytes rather than from the run's
    ///         generator, and that is a requirement rather than a style.</b> A finding carries the
    ///         input and nothing else, so an oracle that depended on where the run had got to would
    ///         hand somebody a corpus file that does not reproduce it.
    ///     </para>
    /// </remarks>
    static long Reparse(SyntaxTree tree, string text) {
        if (tree.Text is not { } source) {
            return 0;
        }

        var choice = Corpus.Fingerprint(Encoding.UTF8.GetBytes(text));
        var start = (int)(choice % (ulong)(text.Length + 1));
        var length = (int)((choice >> 21) % (ulong)(text.Length - start + 1));
        var inserted = Insertions[(int)((choice >> 42) % (ulong)Insertions.Length)];

        var edited = source.WithChanges(new TextChange(new(start, length), inserted));
        var incremental = tree.WithChangedText(edited);
        var full = SyntaxTree.ParseText(edited.ToString(), Path);

        var one = incremental.GetRoot();
        var two = full.GetRoot();

        if (!string.Equals(one.ToFullString(), two.ToFullString(), StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"An incremental reparse printed something a full parse did not, "
                + $"after replacing [{start}, {start + length}) with '{inserted}'."
            );
        }

        if (Shape(one, 0) != Shape(two, 0)) {
            throw new InvalidOperationException(
                $"An incremental reparse built a different tree than a full parse, "
                + $"after replacing [{start}, {start + length}) with '{inserted}'."
            );
        }

        if (incremental.Diagnostics.Count != full.Diagnostics.Count) {
            throw new InvalidOperationException(
                $"An incremental reparse reported {incremental.Diagnostics.Count} diagnostics where a full parse "
                + $"reported {full.Diagnostics.Count}, after replacing [{start}, {start + length}) with '{inserted}'."
            );
        }

        // Folded so that a case which reused nodes is a different behaviour from one that reparsed
        // everything — bucketed, because the exact count is a number and not a shape.
        return incremental.ReusedNodes == 0 ? 1 : 2;
    }

    /// <summary>What the edit puts in, chosen to break a token rather than to be valid.</summary>
    /// <remarks>
    ///     Each is a character the lexer changes state on. An edit that inserts a letter almost
    ///     always leaves the token stream reading the same way and the reparser keeps everything,
    ///     which exercises the fast path and nothing else; a quote, a brace or an <c>@</c> is what
    ///     makes the tokens after it read differently and forces the decision this is checking.
    /// </remarks>
    static readonly string[] Insertions = ["", "<", ">", "\"", "{", "}", "@", "</", "-->", "@code {", " ", "\n"];

    static bool IsMarkupId(string id) =>
        id.Length == 8
        && id.StartsWith("VXML", StringComparison.Ordinal)
        && id[4] is '1' or '2'
        && char.IsAsciiDigit(id[5])
        && char.IsAsciiDigit(id[6])
        && char.IsAsciiDigit(id[7]);

    /// <summary>A number standing for the tree's shape, folded over the slots.</summary>
    /// <remarks>
    ///     <para>
    ///         The comparison an incremental reparse is held to, and the reason it is written here
    ///         rather than taken from the tests: their <c>Dump</c> builds a string per node, which is
    ///         the right thing for a golden file and the wrong thing to allocate a hundred thousand
    ///         times a second.
    ///     </para>
    ///     <para>
    ///         Token text is folded as well as kind. Two trees of identical shape whose tokens differ
    ///         is exactly the failure a reparser produces when it keeps a subtree it should have
    ///         rebuilt, so a fingerprint over kinds alone would miss the thing it is here to catch.
    ///     </para>
    /// </remarks>
    static long Shape(SyntaxNode node, int depth) {
        // Bounded because a mutant can nest arbitrarily and this is called on the frame's own stack.
        // A tree that differs only below this depth is a comparison this misses; a stack overflow in
        // the fuzzer is a run that reports nothing at all.
        if (depth > 64) {
            return 1;
        }

        long shape = node.RawKind;

        if (node is SyntaxToken token) {
            shape = (shape * 31) + (token.IsMissing ? 1 : 2);

            return (shape * 31) + Fold(token.Text);
        }

        shape = (shape * 31) + node.SlotCount;

        for (var i = 0; i < node.SlotCount; i++) {
            shape = (shape * 31) + (node.GetSlot(i) is { } child ? Shape(child, depth + 1) : 0);
        }

        return shape;
    }

    /// <summary>A string as a number, without <c>GetHashCode</c>.</summary>
    /// <remarks>
    ///     ⚠ String hash codes are randomised per process, so a signature folded from one would
    ///     differ between two runs of the same seed — and a fuzz finding that cannot be replayed is a
    ///     rumour. Same argument as <c>Corpus.Fingerprint</c>'s.
    /// </remarks>
    static long Fold(string text) {
        long folded = 17;

        foreach (var character in text) {
            folded = (folded * 31) + character;
        }

        return folded;
    }

    /// <summary>Where two strings first differ, so a report names a position rather than a length.</summary>
    static string Where(string expected, string actual) {
        var shared = Math.Min(expected.Length, actual.Length);

        for (var i = 0; i < shared; i++) {
            if (expected[i] != actual[i]) {
                return $"first difference at {i}, expected '{expected[i]}' and printed '{actual[i]}'";
            }
        }

        return $"identical for {shared} characters, then {expected.Length} against {actual.Length}";
    }
}

/// <summary>Well-formed VXML for the corpus and for the domain to build from.</summary>
/// <remarks>
///     ⚠ <b>Every one of these is checked to parse without a diagnostic</b>, by
///     <c>EveryGrammarSeedIsWellFormed</c> next to the gate. A seed that does not parse is a seed
///     that seeds nothing — the lesson the heightmap's seeds record, where a PNG the decoder refused
///     three branches in left the interesting paths unvisited for as long as nobody measured it. A
///     grammar makes that failure quieter still, because a malformed seed still <i>produces a
///     tree</i> and looks for all the world like it is working.
/// </remarks>
static class MarkupSeeds {
    /// <summary>Whole documents, covering every construct the grammar has.</summary>
    public static IReadOnlyList<string> Documents { get; } = [
        "@component Empty\n",
        "@component Head\n@namespace Vixen.Ui.Demo\n@tag demo-panel\n@using Vixen.Ui.Controls\n",
        "@component Text\n<Label>hello</Label>\n",
        "@component Nest\n<Panel>\n    <Row>\n        <Label>deep</Label>\n    </Row>\n</Panel>\n",
        "@component Attrs\n<Button Variant=\"Subtle\" Label=\"Go\" Disabled=\"@(Count == 0)\" on:click=\"@Press\" />\n",
        "@component Interp\n<Label>@Title and @(Count + 1)</Label>\n",
        "@component Loop\n@for (var item in Items) {\n    <Row key=\"@item\">@item.Name</Row>\n}\n",
        "@component Branch\n@if (Ready) {\n    <Label>yes</Label>\n} else {\n    <Label>no</Label>\n}\n",
        "@component Chain\n@if (a) {\n    <A />\n} else if (b) {\n    <B />\n} else {\n    <C />\n}\n",
        "@component Pick\n@switch (Mode) {\n    case 1:\n        <One />\n    default:\n        <Other />\n}\n",
        "@component Styled\n<style>\n    .row { color: red }\n</style>\n<Row class=\"row\" />\n",
        "@component Coded\n<Label>@Name</Label>\n@code {\n    public string Name => \"x\";\n}\n",
        "<!-- a comment -->\n@component Commented\n<!-- another -->\n<Label>t</Label>\n",
        "@component Quotes\n<Label title='single' other=\"double\" />\n",

        // The whole grammar in one file, which is the shape a tree mutator draws the most from: every
        // kind it might want to graft is present in the same document.
        "@component Everything\n@namespace Vixen.Ui.Demo\n@tag everything-view\n@using Vixen.Ui.Controls\n\n"
        + "<!-- header -->\n<Panel class=\"root\" Width=\"@(W * 2)\">\n    <Label>@Title</Label>\n"
        + "    @if (Show) {\n        <Row>@for (var x in Xs) {\n            <Cell key=\"@x\">@x</Cell>\n        }</Row>\n"
        + "    } else {\n        <Empty />\n    }\n    @switch (Mode) {\n        case 0:\n            <Zero />\n"
        + "        default:\n            <Rest />\n    }\n</Panel>\n<style>\n    .root { padding: 4px }\n</style>\n"
        + "@code {\n    int W => 8;\n    string Title => \"t\";\n}\n"
    ];

    /// <summary>Snippets the domain concatenates when it builds a document from nothing.</summary>
    /// <remarks>
    ///     Fragments rather than documents, because a document from nothing should be a combination
    ///     nothing in the corpus is — two <c>@component</c> directives, a <c>@code</c> block before
    ///     the markup, an <c>else</c> with no <c>if</c>. Each of those is legal text and illegal
    ///     VXML, which is the population a binder is least likely to have been written for.
    /// </remarks>
    public static IReadOnlyList<string> Fragments { get; } = [
        "@component C\n", "@namespace N\n", "@tag t-x\n", "@using U\n", "<Label>t</Label>\n", "<A />\n",
        "<A><B /></A>\n", "<A x=\"1\" y=\"@e\" />\n", "@Value\n", "@(a + b)\n", "@if (c) {\n<A />\n}\n",
        "@if (c) {\n<A />\n} else {\n<B />\n}\n", "@for (var i in xs) {\n<A />\n}\n",
        "@switch (m) {\ncase 1:\n<A />\n}\n", "<style>\n.a { b: c }\n</style>\n", "@code {\nint X => 1;\n}\n",
        "<!-- c -->\n", "  \n", "text\n"
    ];
}
