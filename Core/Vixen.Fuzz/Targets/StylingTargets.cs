// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Ui.Styling;

namespace Vixen.Fuzz.Targets;

/// <summary>The VCSS readers: text rather than bytes, and the same problem again.</summary>
/// <remarks>
///     <para>
///         <b>The first targets here whose input is not bytes, and that is why they are first.</b> A
///         stylesheet is not a packet and not a file the runtime wrote — it is text an author typed,
///         a tool generated, or a mod shipped, and it reaches these two readers before anything has
///         established that it is VCSS at all. The property under test has not changed: a malformed
///         input is a refused declaration rather than an exception out of a parser.
///     </para>
///     <para>
///         <b>The bytes become text at the target's edge, which is where the real system does it
///         too.</b> A <c>.vcss</c> arrives as a file and is decoded as UTF-8, so decoding the
///         mutator's bytes the same way is the honest model rather than a convenience — and it costs
///         the harness nothing, because the corpus, the mutator and the four oracles never learn that
///         this target reads characters. That is the whole reason these two come before any
///         machinery: they demonstrate that a text grammar needs none.
///     </para>
///     <para>
///         ⚠ The decode is replacing rather than throwing, so an invalid UTF-8 sequence becomes
///         U+FFFD instead of an exception the target would be blamed for. It also means one shape the
///         mutator cannot reach: a <b>lone surrogate</b>, which no UTF-8 decode ever produces and
///         which a C# string literal or a UTF-16 source file can hand these parsers directly.
///     </para>
/// </remarks>
static class StyleText {
    /// <summary>Reads an input as a stylesheet would be read.</summary>
    public static string Decode(ReadOnlySpan<byte> input) => Encoding.UTF8.GetString(input);
}

/// <summary>A declaration's value text, parsed into something interpolable.</summary>
/// <remarks>
///     <para>
///         <b>Reached by every animated property of every element, from text nobody validated.</b>
///         The value arrives from ExCSS already normalised, or from a <c>var()</c> substitution that
///         ExCSS never saw — the second is the interesting one, because it is the author's characters
///         passed through untouched. Numbers, units, hex colours, <c>rgb()</c> arguments and
///         space-separated lists are all scanned by hand here, and every one of them is a length or
///         an index derived from the input.
///     </para>
///     <para>
///         The parser is rebuilt each case rather than shared, and the reason is the
///         <see cref="FuzzFailure.Retained" /> oracle rather than tidiness. Interning is what this
///         parser does with an identifier it does not recognise, and a fuzz run's identifiers are
///         distinct by construction — so a shared table would grow one entry per novel case forever
///         and report the harness's own generator as a leak. Rebuilding moves the question from "how
///         much does a run accumulate", which is unanswerable, to "how much does <i>one</i> parse
///         accumulate", which is <see cref="HeldCap" /> and is a claim worth making.
///     </para>
/// </remarks>
public sealed class StyleValueTarget : IFuzzTarget {
    NameTable values = new();
    NameTable keywords = new();
    StyleValueParser parser;

    /// <summary>Creates the target.</summary>
    public StyleValueTarget() => parser = new(values, keywords);

    /// <inheritdoc />
    public string Name => "stylevalue";

    /// <inheritdoc />
    public string What => "StyleValueParser.Parse, over declaration text a var() substitution produced";

    /// <summary>How many names one parse interned.</summary>
    public long Held => keywords.Count + values.Count;

    /// <summary>How many it may intern before that is the finding.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A claim about the grammar: a parse interns at most one name per two bytes.</b> Only
    ///         a top-level part that is neither a number, a hex colour nor a function becomes a
    ///         keyword, and a part needs a character and a separator — so the ceiling for
    ///         <see cref="Mutator.MaxLength" /> bytes is about seven hundred, and the margin here is
    ///         threefold.
    ///     </para>
    ///     <para>
    ///         The number is worth having rather than left at no-cap because the failure it names is
    ///         real and silent: a scanner that interned per character instead of per part would parse
    ///         every stylesheet correctly and grow the name table by the size of the file.
    ///     </para>
    /// </remarks>
    public long HeldCap => 2048;

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     Larger than a packet target's, because a parse of <i>n</i> bytes legitimately produces up
    ///     to <i>n</i>/2 parts and each part costs a substring, a dictionary entry, a list slot and
    ///     an array slot. That is around a hundred bytes for every two of input, which is what this
    ///     multiple is — a ratio the grammar sets rather than a number picked to make a run pass.
    /// </remarks>
    public long AllowanceFor(int inputLength) => 8192 + (128L * inputLength);

    /// <summary>Puts a fresh parser and fresh tables behind the next case.</summary>
    /// <remarks>
    ///     Outside the measurement, as the interface requires: the two dictionaries and the cache
    ///     allocate, and charging them to whichever input followed them would report the reset as an
    ///     amplifying declaration.
    /// </remarks>
    public void Maintain() {
        values = new();
        keywords = new();
        parser = new(values, keywords);
    }

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        // ⚠ Every form the parser has a branch for, because the mutator reaches a branch by breaking
        // an input that already got there. A corpus of only `1px` fuzzes the numeric scanner and
        // leaves the rgb() argument split, the hex reader and the list path unvisited — which is the
        // text version of the mistake the heightmap seeds were written to avoid.
        foreach (var text in (string[])
            [
                "1px", "0", "-1.5em", "1e2px", "2e", ".5", "+3vmin", "100%", "250ms", "1.5s", "45deg", "12rem", "80vw",
                "60vh", "9vmax", "#fff", "#ff00ff", "#12345678", "rgb(255, 0, 0)", "rgba(0, 128, 255, 0.5)",
                "rgb(50%, 0%, 100%)", "red", "transparent", "inherit", "some-unknown-keyword", "1px 2px", "0 0 4px #000",
                "1px solid rgb(1, 2, 3)", "rgb(1,2,3) rgb(4,5,6) rgb(7,8,9)"
            ]) {
            corpus.Add(Encoding.UTF8.GetBytes(text));
        }
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var parsed = parser.Parse(StyleText.Decode(input).AsSpan());
        long signature = (17 * 31) + (int)parsed.Kind;

        // The items rather than the count alone: a list of three lengths and a list of three
        // keywords are different behaviours, and folding only the length would call them one.
        var items = parsed.Items;
        signature = (signature * 31) + items.Length;

        // Bounded because the signature is about the case: a list of six hundred parts is the same
        // behaviour as one of sixteen, and folding all of it would make every long input novel.
        for (var i = 0; i < items.Length && i < 16; i++) {
            signature = (signature * 31) + (int)items[i].Kind;
        }

        return signature;
    }
}

/// <summary>An <c>@layer</c> rule, read out of the text ExCSS could not parse.</summary>
/// <remarks>
///     <para>
///         <b>A hand-written brace matcher over text a library already gave up on, which is the
///         combination worth fuzzing.</b> ExCSS 4.3.2 does not know cascade layers and hands the rule
///         back whole, so this reader is the only thing standing between an author's braces and the
///         rest of the cascade — and it has to be right about brace balance inside strings and
///         comments, each of which is a scanner that can run off the end.
///     </para>
///     <para>
///         ⚠ <b>Two invariants come free from the shape of the method, and neither is asserted
///         anywhere else.</b> It returns a <c>bool</c> and has no failure channel, so it must never
///         throw for any string at all; and a <c>Try…</c> that returns false must leave its
///         out-parameter at the default, because a caller that ignores the result and reads the
///         out-param is the bug this pattern exists to prevent. Both are checked here rather than
///         assumed, which is what makes this a target and not a smoke test.
///     </para>
/// </remarks>
public sealed class LayerRuleTarget : IFuzzTarget {
    /// <inheritdoc />
    public string Name => "layerrule";

    /// <inheritdoc />
    public string What => "LayerRuleParser.TryParse, over rule text ExCSS handed back unparsed";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     A prelude of <i>n</i> bytes can be <i>n</i>/2 comma-separated names, each a string and a
    ///     list slot, and the body is one more copy of the input. The idempotence check below parses
    ///     a second time and rebuilds the text, so this covers roughly three passes rather than one.
    /// </remarks>
    public long AllowanceFor(int inputLength) => 8192 + (128L * inputLength);

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        // Both forms, the anonymous block, the nesting, and the two shapes the brace matcher exists
        // for — a brace inside a string and a brace inside a comment.
        foreach (var text in (string[])
            [
                "@layer base;", "@layer base, components, utilities;", "@layer a.b.c;", "@layer { color: red }",
                "@layer base { color: red }", "@layer a, b { .x { color: red } }",
                "@layer q { .x::after { content: \"}\" } }", "@layer q { /* } */ .x { color: red } }",
                "@layer q { .x::after { content: '{' } }", "@layer base", "@layer", "@layers-are-nice { }",
                "@media screen { }", "@layer a { @layer b { .x { color: red } } }"
            ]) {
            corpus.Add(Encoding.UTF8.GetBytes(text));
        }
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var text = StyleText.Decode(input);
        var read = LayerRuleParser.TryParse(text, out var rule);

        if (!read) {
            // The out-parameter contract, checked rather than trusted. Throwing is how a target
            // reports a broken promise, so this is a finding by the same route an exception out of
            // the parser would be.
            if (rule.Names is not null || rule.Body is not null) {
                throw new InvalidOperationException(
                    $"LayerRuleParser.TryParse returned false but left the rule at {rule}, not at its default."
                );
            }

            // Refusal is a behaviour worth distinguishing from the shapes below, and the reason it
            // refused is the only thing about it worth folding.
            return (17 * 31) + (LayerRuleParser.IsLayerRule(text) ? 1 : 2);
        }

        // A rule that read must have said something: a statement declares names, a block has a body.
        if (rule.Names!.Count == 0 && rule.Body is null) {
            throw new InvalidOperationException("LayerRuleParser.TryParse read a rule that declares nothing.");
        }

        long signature = (17 * 31) + rule.Names.Count;
        signature = (signature * 31) + (rule.Body?.Length ?? -1);

        for (var i = 0; i < rule.Names.Count && i < 8; i++) {
            signature = (signature * 31) + rule.Names[i].Length;
        }

        // ⚠ Idempotence rather than a byte round-trip, and the difference matters. The reader
        // normalises — it trims each name and drops the empty ones — so `@layer a,,b;` cannot come
        // back as itself and asking it to would be a false finding on case one. What it must do is
        // reach a fixed point: whatever it read, printing that and reading it again gives the same
        // rule. A brace matcher that mis-counted a string would be caught here even when the first
        // parse threw nothing at all, which is exactly the failure the oracles above cannot see.
        var printed = rule.Body is null
            ? $"@layer {string.Join(", ", rule.Names)};"
            : $"@layer {string.Join(", ", rule.Names)}{{{rule.Body}}}";

        if (!LayerRuleParser.TryParse(printed, out var again)) {
            throw new InvalidOperationException($"LayerRuleParser.TryParse refused its own output: {printed}");
        }

        if (!string.Equals(again.Body, rule.Body, StringComparison.Ordinal)
            || again.Names!.Count != rule.Names.Count) {
            throw new InvalidOperationException(
                $"LayerRuleParser.TryParse is not idempotent: {rule} became {again} through {printed}."
            );
        }

        for (var i = 0; i < rule.Names.Count; i++) {
            if (!string.Equals(again.Names[i], rule.Names[i], StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    $"LayerRuleParser.TryParse renamed layer {i}: '{rule.Names[i]}' became '{again.Names[i]}'."
                );
            }
        }

        return signature;
    }
}
