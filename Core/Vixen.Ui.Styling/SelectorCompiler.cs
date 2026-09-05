// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using ExCSS;
using Combinator = Vixen.Ui.Styling.Combinator;
using Selector = Vixen.Ui.Styling.Selector;

namespace Vixen.Ui.Styling;

/// <summary>Why a selector could not be compiled.</summary>
/// <param name="Text">The fragment that stopped it, as it was written.</param>
/// <param name="Reason">What stopped it.</param>
/// <param name="Rule">
///     The enclosing rule the fragment was written in, or <see langword="null" /> when
///     <paramref name="Text" /> is already the whole of it. Read through <see cref="Where" />.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b><paramref name="Text" /> is the fragment, and on its own it does not say which rule
///         to go and fix.</b> A refusal names what the compiler choked on — <c>::before</c>,
///         <c>:has(…)</c>, a combinator — because that is what it was looking at when it stopped.
///         That is the right thing to report and the wrong thing to report <i>alone</i>: a sheet
///         with two <c>::before</c> rules produces two lines differing only in a reason, and neither
///         names a rule. There are no line numbers to fall back on, because ExCSS does not carry
///         them through to the nodes this walks.
///     </para>
///     <para>
///         ⚠ <b>It began mattering when the drain landed.</b> Until then these went into a
///         <c>List&lt;SelectorDiagnostic&gt;</c> behind a public property that nothing outside this
///         assembly's tests read, and a test knows which rule it wrote. They now reach the editor's
///         Console panel, the log overlay and the crash dump, where the reader is a person looking
///         at a stylesheet they did not necessarily write.
///     </para>
///     <para>
///         Null rather than a copy of <paramref name="Text" /> when the two would be the same, so
///         that the sink can tell "the rule is the fragment" from "the rule is elsewhere" and log
///         the shorter message for the first. See <see cref="Where" />.
///     </para>
/// </remarks>
public readonly record struct SelectorDiagnostic(string Text, string Reason, string? Rule = null) {
    /// <summary>The rule to go and fix: <see cref="Rule" /> when there is one, else <see cref="Text" />.</summary>
    public string Where => Rule ?? Text;

    /// <summary>Whether <see cref="Rule" /> says something <see cref="Text" /> does not.</summary>
    /// <remarks>
    ///     A rule that is character-for-character its own fragment — <c>@media (min-width: bananas)</c>
    ///     is both — has nothing to add, and repeating it would make every such line read
    ///     "refused 'X' in 'X'".
    /// </remarks>
    public bool NamesAnEnclosingRule => Rule is not null && !string.Equals(Rule, Text, StringComparison.Ordinal);
}

/// <summary>Turns ExCSS's selector tree into the flat form the matcher runs.</summary>
/// <remarks>
///     <para>
///         A visitor rather than a parser, which is the whole of what ADR-009 buys by taking the
///         dependency — and which was verified against the library before this was written
///         ([the spike](../../docs/plan/spikes/vcss-excss/RESULT.md)).
///     </para>
///     <para>
///         A selector using something Vixen does not support is <i>dropped with a diagnostic</i>
///         rather than approximated. A rule that silently matches more than it says is worse than a
///         rule that does not load: the first produces a UI that is subtly wrong everywhere the
///         author did not look, and the second produces a message.
///     </para>
/// </remarks>
public sealed class SelectorCompiler(SelectorTable table, NameTable names) {
    /// <summary>Re-reads one selector that had to be repaired before ExCSS could read it.</summary>
    /// <remarks>See <see cref="CompileRewritten" />. The settings are <c>StyleSheetLoader</c>'s.</remarks>
    static readonly StylesheetParser Reparser = new(true, true, true, true, true);

    readonly NameTable names = names ?? throw new ArgumentNullException(nameof(names));
    readonly SelectorTable table = table ?? throw new ArgumentNullException(nameof(table));
    readonly List<SelectorDiagnostic> diagnostics = [];

    /// <summary>Everything that could not be compiled, and why.</summary>
    public IReadOnlyList<SelectorDiagnostic> Diagnostics => diagnostics;

    /// <summary>
    ///     The complex selector currently being compiled, which every refusal below is attributed to.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A field rather than a parameter on nine signatures.</b> The refusals are raised four
    ///     and five calls down — inside a compound, inside a pseudo-class, inside the argument of a
    ///     <c>:has()</c> — and every one of those frames would have to carry a string it does not
    ///     otherwise use. It is set in exactly one place, <see cref="CompileParts" />, and saved and
    ///     restored around the nested compile so that a <c>:is()</c> argument keeps attributing to
    ///     the rule it was written in rather than to itself.
    /// </remarks>
    string? rule;

    /// <summary>Compiles one ExCSS selector, splitting a list into its parts.</summary>
    /// <param name="selector">What ExCSS parsed.</param>
    /// <param name="compiled">Receives one entry per comma-separated selector.</param>
    public void Compile(ISelector selector, List<Selector> compiled) {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(compiled);

        CompileParts(selector, compiled);
    }

    void CompileParts(ISelector selector, List<Selector> compiled) {
        if (selector is ListSelector list) {
            // ⚠ Each part of `a::before, b::before` is attributed to itself and not to the pair.
            // The two are one rule to ExCSS and two rules to the cascade, and the one a reader has
            // to go and change is the part — so the enclosing text is reset per child below rather
            // than taken from the list.
            foreach (var child in list) {
                CompileParts(child, compiled);
            }

            return;
        }

        // ⚠ Before anything else looks at the tree, because for these selectors there is no tree.
        // See `CompileRewritten`.
        if (NeedsRepair(selector.Text) && TryRewrite(selector.Text, out var parts)) {
            CompileRewritten(parts, compiled);
            return;
        }

        // ⚠ Not overwritten when already set. `TryCompileNested` re-enters here for the argument of
        // a `:is()` or `:has()`, and that argument is not a rule anybody can go and find; the rule
        // is the selector the argument sits inside.
        var outer = rule;
        rule ??= selector.Text;

        try {
            if (TryCompileOne(selector, out var result)) {
                compiled.Add(result);
            }
        } finally {
            rule = outer;
        }
    }

    /// <summary>Records a refusal against the rule currently being compiled.</summary>
    void Refuse(string text, string reason) => diagnostics.Add(new SelectorDiagnostic(text, reason, rule));

    /// <summary>The attribute name <c>:user-valid</c> is smuggled through ExCSS as.</summary>
    /// <remarks>
    ///     ⚠ <b>An attribute and not a class or a pseudo-class, and the choice is about the number
    ///     rather than about the syntax.</b> ExCSS charges an attribute selector one class, which is
    ///     what a pseudo-class costs — so the rewrite is specificity-neutral and nothing has to be
    ///     added back afterwards, unlike the <c>:where(</c> → <c>:is(</c> rewrite beside it. A
    ///     pseudo-class marker is not available: an unknown one is exactly what this works around.
    /// </remarks>
    const string UserValidMarker = "_vixen-user-valid";

    /// <summary>The same for <c>:user-invalid</c>.</summary>
    const string UserInvalidMarker = "_vixen-user-invalid";

    /// <summary>Whether ExCSS will have failed on this text for a reason this class can repair.</summary>
    /// <remarks>
    ///     ⚠ <b>Cheap and deliberately over-eager, because the repair itself is the honest test.</b>
    ///     All three words can appear inside a quoted attribute value in text ExCSS reads perfectly
    ///     well, so <see cref="TryRewrite" /> answers false when it changed nothing outside a string
    ///     and this only decides whether to ask.
    /// </remarks>
    static bool NeedsRepair(string text) =>
        text.Contains(":where(", StringComparison.OrdinalIgnoreCase)
        || text.Contains(":user-valid", StringComparison.OrdinalIgnoreCase)
        || text.Contains(":user-invalid", StringComparison.OrdinalIgnoreCase);

    /// <summary>One comma-separated part of a selector that was written with <c>:where()</c>.</summary>
    /// <param name="Written">What the author wrote, for a diagnostic to quote.</param>
    /// <param name="Rewritten">The same part with every <c>:where(</c> turned into <c>:is(</c>.</param>
    /// <param name="Zeroed">How many of those were at the top level, and so must be charged nothing.</param>
    readonly record struct RewrittenPart(string Written, string Rewritten, int Zeroed);

    /// <summary>Compiles the parts of a selector ExCSS could not read because of a <c>:where()</c>.</summary>
    /// <param name="parts">The parts, already rewritten.</param>
    /// <param name="compiled">Receives one entry per part that compiled.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>ExCSS 4.3.2 has no <c>:where()</c>, and it does not fail narrowly.</b> A selector
    ///         containing one comes back as a single <c>UnknownSelector</c> covering the <i>whole</i>
    ///         text, commas and all — so there is no node to charge nothing for, and there never was
    ///         the "three lines in <c>SelectorCompiler</c>" that doc 43 § F9, doc 09 and three
    ///         READMEs all costed this at. The rule was refused entire.
    ///     </para>
    ///     <para>
    ///         The repair is made where the evidence still is. That unknown node carries the author's
    ///         text verbatim, so the text is split on its top-level commas, each <c>:where(</c> is
    ///         rewritten to <c>:is(</c>, and each part is handed back to ExCSS on its own. The two
    ///         selectors match identically — <c>:where()</c> <i>is</i> <c>:is()</c> with no
    ///         specificity — so the only thing the rewrite loses is the number, and the number is put
    ///         back by subtracting one class per top-level occurrence.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Top-level, because a nested one is already free.</b> This compiler never adds a
    ///         nested selector's specificity to the outer one, so a <c>:where()</c> inside an
    ///         <c>:is()</c> or a <c>:not()</c> contributes nothing before the rewrite and nothing
    ///         after it; subtracting for those would take the count below zero and make
    ///         <c>.a:not(:where(.b))</c> less specific than <c>.a</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not a general fallback for anything ExCSS cannot read.</b> The rewrite is
    ///         attempted only when the text actually contains a <c>:where(</c> outside a string, and
    ///         a part that still does not parse is refused with the author's own spelling quoted —
    ///         the rewritten form would name a selector nobody wrote.
    ///     </para>
    /// </remarks>
    void CompileRewritten(List<RewrittenPart> parts, List<Selector> compiled) {
        foreach (var part in parts) {
            var sheet = Reparser.Parse(part.Rewritten + " { color: red }");

            if (sheet.Children.OfType<IStyleRule>().FirstOrDefault() is not { } reparsed) {
                Refuse(part.Written, $"'{part.Written}' is not a selector Vixen supports");
                continue;
            }

            var outer = rule;
            rule ??= part.Written;

            var start = compiled.Count;

            try {
                CompileParts(reparsed.Selector, compiled);
            } finally {
                rule = outer;
            }

            if (part.Zeroed == 0) {
                continue;
            }

            for (var i = start; i < compiled.Count; i++) {
                var selector = compiled[i];

                compiled[i] = selector with {
                    Specificity = selector.Specificity with {
                        Classes = selector.Specificity.Classes - part.Zeroed
                    }
                };
            }
        }
    }

    /// <summary>Splits a selector on its top-level commas, turning every <c>:where(</c> into <c>:is(</c>.</summary>
    /// <param name="text">The selector as written.</param>
    /// <param name="parts">Receives the parts.</param>
    /// <returns>Whether anything was rewritten.</returns>
    /// <remarks>
    ///     ⚠ <b>False when nothing changed, and that is not a formality.</b> The word appears inside
    ///     a quoted attribute value — <c>[data-q=":where(x)"]</c> — in text ExCSS reads perfectly
    ///     well, and answering true there would send a selector that needs no repair down a path that
    ///     re-parses it and refuses it.
    /// </remarks>
    static bool TryRewrite(string text, out List<RewrittenPart> parts) {
        parts = [];

        var rewritten = new System.Text.StringBuilder(text.Length);
        var start = 0;
        var depth = 0;
        var zeroed = 0;
        var found = 0;

        for (var i = 0; i < text.Length; i++) {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length) {
                rewritten.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            if (c is '"' or '\'') {
                var end = i;

                while (++end < text.Length) {
                    if (text[end] == '\\') {
                        end++;
                        continue;
                    }

                    if (text[end] == c) {
                        break;
                    }
                }

                end = Math.Min(end, text.Length - 1);
                rewritten.Append(text, i, end - i + 1);
                i = end;
                continue;
            }

            if (c == ':' && string.Compare(text, i, ":where(", 0, 7, StringComparison.OrdinalIgnoreCase) == 0) {
                if (depth == 0) {
                    zeroed++;
                }

                found++;
                depth++;
                rewritten.Append(":is(");
                i += 6;
                continue;
            }

            // ⚠ The longer name first. `:user-invalid` is not a `:user-valid` with a prefix — they
            // diverge at the seventh character — but testing the shorter one first is the habit that
            // would go wrong the day a third name in this family is not so lucky.
            if (c == ':' && string.Compare(text, i, ":user-invalid", 0, 13, StringComparison.OrdinalIgnoreCase) == 0) {
                found++;
                rewritten.Append('[').Append(UserInvalidMarker).Append(']');
                i += 12;
                continue;
            }

            if (c == ':' && string.Compare(text, i, ":user-valid", 0, 11, StringComparison.OrdinalIgnoreCase) == 0) {
                found++;
                rewritten.Append('[').Append(UserValidMarker).Append(']');
                i += 10;
                continue;
            }

            switch (c) {
                case '(' or '[':
                    depth++;
                    break;

                case ')' or ']':
                    depth--;
                    break;

                case ',' when depth == 0:
                    parts.Add(new RewrittenPart(text[start..i].Trim(), rewritten.ToString().Trim(), zeroed));
                    rewritten.Clear();
                    start = i + 1;
                    zeroed = 0;
                    continue;

                default:
                    break;
            }

            rewritten.Append(c);
        }

        parts.Add(new RewrittenPart(text[start..].Trim(), rewritten.ToString().Trim(), zeroed));

        return found > 0;
    }

    bool TryCompileOne(ISelector selector, out Selector compiled) {
        // Compounds are buffered and written in one run at the end. A `:not()` or `:is()` inside
        // this selector compiles *its* compounds into the same table on the way past, and a range
        // reserved up front would have those nested compounds land in the middle of it. Four tests
        // disagreed about that before the buffering was here.
        var buffered = new List<CompoundSelector>();
        var specificity = new Specificity();
        var count = 0;

        // ExCSS records a combinator alongside the compound it *follows*, and the last one carries
        // an empty delimiter. Vixen records it on the compound it *precedes*, because the matcher
        // walks right to left and wants to know how to get from here to the previous one.
        var pending = Combinator.None;

        foreach (var (part, delimiter) in Parts(selector)) {
            if (!TryCompileCompound(part, pending, ref specificity, out var compound)) {
                compiled = default;
                return false;
            }

            buffered.Add(compound);
            count++;

            if (delimiter.Length == 0) {
                continue;
            }

            var next = delimiter switch {
                ">" => Combinator.Child,
                "+" => Combinator.NextSibling,
                "~" => Combinator.SubsequentSibling,
                " " => Combinator.Descendant,
                _ => Combinator.None
            };

            if (next == Combinator.None) {
                Refuse(selector.Text, $"the combinator '{delimiter}' is not supported");
                compiled = default;
                return false;
            }

            pending = next;
        }

        if (count == 0) {
            Refuse(selector.Text, "the selector is empty");
            compiled = default;
            return false;
        }

        var start = table.CompoundCount;
        foreach (var compound in buffered) {
            table.AddCompound(compound);
        }

        compiled = new Selector(start, count, specificity);
        return true;
    }

    static IEnumerable<(ISelector Part, string Delimiter)> Parts(ISelector selector) {
        if (selector is not ComplexSelector complex) {
            return [(selector, string.Empty)];
        }

        return complex.Select(part => (part.Selector, part.Delimiter ?? string.Empty));
    }

    bool TryCompileCompound(
        ISelector part,
        Combinator combinator,
        ref Specificity specificity,
        out CompoundSelector compound
    ) {
        compound = default;

        // Buffered for the same reason the compounds are: a nested selector writes its own simples
        // into this table while this compound is still being built.
        var buffered = new List<SimpleSelector>();

        foreach (var simple in Flatten(part)) {
            if (!TryCompileSimple(simple, ref specificity, out var compiled)) {
                return false;
            }

            buffered.Add(compiled);
        }

        if (buffered.Count == 0) {
            // `div > *` — a compound of nothing survives as the universal selector rather than as a
            // compound that matches nothing.
            buffered.Add(new SimpleSelector(SimpleSelectorKind.Universal));
        }

        var start = table.SimpleCount;
        foreach (var simple in buffered) {
            table.AddSimple(simple);
        }

        compound = new CompoundSelector(combinator, start, buffered.Count);
        return true;
    }

    // Fully qualified: ExCSS has a CompoundSelector and so does Vixen, and the unqualified name
    // here resolves to ours, which is a struct and can never be what ExCSS handed us. A list rather
    // than an iterator because compilation happens once per stylesheet and the analyzers are right
    // that an interface return on a hot path would be worth avoiding — this one simply is not hot.
    static List<ISelector> Flatten(ISelector selector) =>
        selector is ExCSS.CompoundSelector compound ? [.. compound] : [selector];

    bool TryCompileSimple(ISelector selector, ref Specificity specificity, out SimpleSelector compiled) {
        compiled = default;

        switch (selector) {
            case AllSelector:
                compiled = new SimpleSelector(SimpleSelectorKind.Universal);
                return true;

            case TypeSelector type:
                specificity = specificity with { Types = specificity.Types + 1 };
                compiled = new SimpleSelector(SimpleSelectorKind.Type, names.Intern(type.Name));
                return true;

            case IdSelector id:
                specificity = specificity with { Ids = specificity.Ids + 1 };
                compiled = new SimpleSelector(SimpleSelectorKind.Id, names.Intern(Trim(id.Text, '#')));
                return true;

            case ClassSelector cls:
                specificity = specificity with { Classes = specificity.Classes + 1 };
                compiled = new SimpleSelector(SimpleSelectorKind.Class, names.Intern(Trim(cls.Text, '.')));
                return true;

            case AttrAvailableSelector attribute:
                specificity = specificity with { Classes = specificity.Classes + 1 };

                // ⚠ The two names this compiler does not read as attributes, because they never
                // came from an author — `TryRewrite` puts them there so that a pseudo-class ExCSS
                // has no literal for can reach this switch at all. See `UserValidMarker`.
                compiled = attribute.Attribute switch {
                    UserValidMarker => new SimpleSelector(
                        SimpleSelectorKind.State,
                        State: ElementState.Valid | ElementState.UserInteracted
                    ),
                    UserInvalidMarker => new SimpleSelector(
                        SimpleSelectorKind.State,
                        State: ElementState.Invalid | ElementState.UserInteracted
                    ),
                    _ => new SimpleSelector(SimpleSelectorKind.Attribute, names.Intern(attribute.Attribute))
                };

                return true;

            case AttrMatchSelector match:
                specificity = specificity with { Classes = specificity.Classes + 1 };
                compiled = Attribute(match.Attribute, match.Value, AttributeOperator.Equals);
                return true;

            case AttrListSelector list:
                specificity = specificity with { Classes = specificity.Classes + 1 };
                compiled = Attribute(list.Attribute, list.Value, AttributeOperator.Includes);
                return true;

            case AttrHyphenSelector hyphen:
                specificity = specificity with { Classes = specificity.Classes + 1 };
                compiled = Attribute(hyphen.Attribute, hyphen.Value, AttributeOperator.DashMatch);
                return true;

            case AttrBeginsSelector begins:
                specificity = specificity with { Classes = specificity.Classes + 1 };
                compiled = Attribute(begins.Attribute, begins.Value, AttributeOperator.Prefix);
                return true;

            case AttrEndsSelector ends:
                specificity = specificity with { Classes = specificity.Classes + 1 };
                compiled = Attribute(ends.Attribute, ends.Value, AttributeOperator.Suffix);
                return true;

            case AttrContainsSelector contains:
                specificity = specificity with { Classes = specificity.Classes + 1 };
                compiled = Attribute(contains.Attribute, contains.Value, AttributeOperator.Substring);
                return true;

            // ⚠ Before the two child cases, and deliberately so. ExCSS spells `:nth-of-type` with a
            // type of its own, but a pattern match is a runtime test and an of-type node that ever
            // derived from a child node would be silently answered by the case below — which is the
            // failure this pair is hardest to see: `:nth-of-type(2)` and `:nth-child(2)` agree on
            // every document whose children all carry one tag, so a fixture would have to mix tags
            // before the difference showed. `SelectorMatchingTests` mixes them for that reason.
            case FirstTypeSelector nthType:
                specificity = specificity with { Classes = specificity.Classes + 1 };
                compiled = new SimpleSelector(
                    SimpleSelectorKind.Position,
                    Position: PositionTest.NthOfType,
                    Step: nthType.Step,
                    Offset: nthType.Offset
                );

                return true;

            case LastTypeSelector nthLastType:
                specificity = specificity with { Classes = specificity.Classes + 1 };
                compiled = new SimpleSelector(
                    SimpleSelectorKind.Position,
                    Position: PositionTest.NthLastOfType,
                    Step: nthLastType.Step,
                    Offset: nthLastType.Offset
                );

                return true;

            case FirstChildSelector nth:
                specificity = specificity with { Classes = specificity.Classes + 1 };
                compiled = new SimpleSelector(
                    SimpleSelectorKind.Position,
                    Position: PositionTest.Nth,
                    Step: nth.Step,
                    Offset: nth.Offset
                );

                return true;

            case LastChildSelector nthLast:
                specificity = specificity with { Classes = specificity.Classes + 1 };
                compiled = new SimpleSelector(
                    SimpleSelectorKind.Position,
                    Position: PositionTest.NthLast,
                    Step: nthLast.Step,
                    Offset: nthLast.Offset
                );

                return true;

            case NotSelector not:
                specificity = specificity with { Classes = specificity.Classes + 1 };
                return TryCompileNested(not.Inner, SimpleSelectorKind.Not, not.Text, out compiled);

            case MatchesSelector matches:
                specificity = specificity with { Classes = specificity.Classes + 1 };
                return TryCompileNested(matches.Inner, SimpleSelectorKind.Is, matches.Text, out compiled);

            // ⚠ Compiled, and its argument is restricted to a single compound rather than merely
            // being compiled and hoped for. `:has(.a .b)` does not mean "some descendant matches
            // `.a .b`": CSS anchors the argument at the element, so the `.a` has to be inside the
            // subtree too — and the obvious implementation, matching the nested selector against
            // every descendant, would also say yes when the `.a` is an *ancestor* of the element.
            // That is a rule matching more than it says, which is what this compiler refuses rather
            // than approximates. Every Tailwind `has-*` is a single compound.
            case HasSelector has: {
                specificity = specificity with { Classes = specificity.Classes + 1 };

                if (!TryCompileNested(has.Inner, SimpleSelectorKind.Has, has.Text, out compiled)) {
                    return false;
                }

                for (var n = 0; n < compiled.NestedCount; n++) {
                    if (table.Nested(compiled.NestedStart + n).Count == 1) {
                        continue;
                    }

                    Refuse(
                        has.Text,
                        $"'{has.Text}' has a combinator in its argument, and such an argument is anchored at the element — Vixen matches a single compound only"
                    );

                    compiled = default;
                    return false;
                }

                return true;
            }

            // ⚠ Refused, and the refusal is the whole of doc 43's F6. `::before` used to compile:
            // the name was interned onto `Selector.PseudoElement`, the compound carried on without
            // it, and the rule then matched — and applied — to the ORIGINATING element. So
            // `p::before { color: red }` turned the paragraph red. Nothing anywhere read the field,
            // so that was not a partial implementation; it was the rule quietly meaning something
            // else. Generating the box it asks for needs a box with no node behind it.
            // ⚠ Both of the things that used to be named here as sharing that blocker have since
            // landed and neither unblocked this: inline fragmentation gave a node MORE boxes, and an
            // anonymous block box needs no box stored at all. `InlineKnownGaps.txt` now says what is
            // actually left, and it is the half that was always this feature's own — a generated box
            // carries a STYLE of its own, so it needs a second style slot rather than a second
            // rectangle. Until doc 43's A12, the author gets a message instead of a surprise.
            //
            // ⚠ <b>Four ledger rows rest on this one refusal and until 2026-09-05 not one of them
            // declared it, which is the exact shape `RefusalExpiry.txt`'s header warns about.</b>
            // `list-*`, `list-image-*`, `list-style-position` and `placeholder-*` are all `absent`
            // and all say "blocked on F6" in English; the day a generated box exists they rot
            // together and nothing would have said so. The condition is written here, where the
            // refusal is, and on the two rows that do not sit behind another one. The anchor is the
            // field this compiler deleted: a rule that really generates a box has to carry WHICH
            // pseudo-element it names, and `Selector` is where that lived. ⚠ It is walked around by
            // anyone who spells the returning thing differently — move the clause with it.
            // [expires-on Vixen.Ui.Styling.Selector.PseudoElement]
            case PseudoElementSelector:
                Refuse(
                    selector.Text,
                    "a pseudo-element generates a box of its own, and Vixen has no box without an element behind it"
                );

                return false;

            case PseudoClassSelector pseudo:
                return TryCompilePseudoClass(pseudo, ref specificity, out compiled);

            default:
                Refuse(selector.Text, $"'{selector.Text}' is not a selector Vixen supports");
                return false;
        }

        SimpleSelector Attribute(string attribute, string value, AttributeOperator op) =>
            new(SimpleSelectorKind.Attribute, names.Intern(attribute), names.Intern(value), op);
    }

    bool TryCompilePseudoClass(PseudoClassSelector pseudo, ref Specificity specificity, out SimpleSelector compiled) {
        compiled = default;
        var name = Trim(pseudo.Text, ':');

        var state = name switch {
            "hover" => ElementState.Hover,
            "active" => ElementState.Active,
            "focus" => ElementState.Focus,
            "focus-visible" => ElementState.FocusVisible,
            "focus-within" => ElementState.FocusWithin,
            "disabled" => ElementState.Disabled,
            "checked" => ElementState.Checked,

            // ⚠ The three that need a bit somebody sets rather than a bit the input system sets, which
            // is what made them the expensive third of doc 43's A13 and not the cheap two-thirds. A
            // compiler arm here is worth nothing on its own: `:read-only` compiled against a bit no
            // control ever writes resolves, indexes and matches nothing, which is the failure that
            // document exists to refuse. See `TextField` and `CheckBox`, which are the writers.
            "read-only" => ElementState.ReadOnly,
            "placeholder-shown" => ElementState.PlaceholderShown,
            "indeterminate" => ElementState.Indeterminate,

            // ⚠ The form-validity family, which arrived once `TextField` grew a validation model —
            // the comment above these three used to say this framework had none, and it was true
            // when it was written. `:valid` and `:invalid` are two bits rather than one and its
            // negation, because Selectors 4 § 10.6 gives neither to an element that does not take
            // part in constraint validation and a negation would have made every `div` valid.
            //
            // ⚠ `:user-valid` and `:user-invalid` never reach this switch, and their absence here is
            // not a refusal. ExCSS 4.3.2 has no literal for either name — measured, the UTF-16 bytes
            // are not in the assembly — so the whole compound arrives as an `UnknownSelector` and
            // never becomes a `PseudoClassSelector` at all. `TryRewrite` turns them into the two
            // markers the attribute arm above reads, and the mask is a conjunction because the state
            // test below already is one.
            "required" => ElementState.Required,
            "valid" => ElementState.Valid,
            "invalid" => ElementState.Invalid,

            // ⚠ `:out-of-range` is the positive of the pair and `:in-range` is its negation below,
            // which is the other way round from `:valid`/`:invalid` two lines up. A range is
            // declared rather than computed, so a control that has one always answers and the two
            // are true complements; validity is a verdict, and an element that reaches no verdict
            // has to be able to be neither.
            "out-of-range" => ElementState.OutOfRange,
            _ => ElementState.None
        };

        if (state != ElementState.None) {
            specificity = specificity with { Classes = specificity.Classes + 1 };
            compiled = new SimpleSelector(SimpleSelectorKind.State, State: state);
            return true;
        }

        var position = name switch {
            "first-child" => PositionTest.First,
            "last-child" => PositionTest.Last,
            "only-child" => PositionTest.Only,

            // ⚠ These three arrive as a plain pseudo-class and their `:nth-of-type(…)` siblings do
            // not — ExCSS gives the functional forms nodes of their own and leaves the keyword forms
            // here, exactly as it does for `:first-child` against `:nth-child(…)`. Verified against
            // the parser rather than assumed, because a name this switch does not know is refused
            // and a refused variant is one nobody notices is missing.
            "first-of-type" => PositionTest.FirstOfType,
            "last-of-type" => PositionTest.LastOfType,
            "only-of-type" => PositionTest.OnlyOfType,
            _ => (PositionTest?) null
        };

        if (position is not null) {
            specificity = specificity with { Classes = specificity.Classes + 1 };
            compiled = new SimpleSelector(SimpleSelectorKind.Position, Position: position.Value);
            return true;
        }

        if (name == "empty") {
            specificity = specificity with { Classes = specificity.Classes + 1 };
            compiled = new SimpleSelector(SimpleSelectorKind.Empty);
            return true;
        }

        if (name.StartsWith("lang(", StringComparison.Ordinal) && name.EndsWith(')')) {
            return TryCompileLang(pseudo.Text, name[5..^1].Trim(), ref specificity, out compiled);
        }

        if (name == "enabled") {
            // The absence of a state rather than a state of its own, which is what CSS means by it.
            specificity = specificity with { Classes = specificity.Classes + 1 };
            var nested = table.AddNested(NegatedState(ElementState.Disabled));
            compiled = new SimpleSelector(SimpleSelectorKind.Not, NestedStart: nested, NestedCount: 1);
            return true;
        }

        if (name == "read-write") {
            // ⚠ `:enabled`'s arrangement, and the reason is the same sentence in a different
            // specification: Selectors 4 § 10.2 defines `:read-write` as the elements `:read-only`
            // does not match, so a bit of its own would be a second thing to keep in step with the
            // first — and the two would disagree the first time a control set one and forgot the
            // other. ⚠ It differs from CSS in what it says about a plain `div`: a browser calls a
            // non-editable element read-only, and here everything that never said otherwise is
            // read-write. That is stated rather than smuggled, and it is the same divergence
            // `:enabled` already carries for an element that is not a control.
            specificity = specificity with { Classes = specificity.Classes + 1 };
            var nested = table.AddNested(NegatedState(ElementState.ReadOnly));
            compiled = new SimpleSelector(SimpleSelectorKind.Not, NestedStart: nested, NestedCount: 1);

            return true;
        }

        if (name == "optional") {
            // ⚠ `:read-write`'s arrangement, and unlike `:valid` this one is a true complement.
            // Selectors 4 § 10.5 defines `:optional` as the form controls `:required` does not match,
            // and a bit of its own would be a second thing to keep in step. It carries the same
            // divergence the two above already carry: a browser only calls a *form control* optional,
            // and here everything that never said it was required is.
            specificity = specificity with { Classes = specificity.Classes + 1 };
            var nested = table.AddNested(NegatedState(ElementState.Required));
            compiled = new SimpleSelector(SimpleSelectorKind.Not, NestedStart: nested, NestedCount: 1);

            return true;
        }

        if (name == "in-range") {
            // ⚠ `:optional`'s arrangement, and it carries the divergence those two already carry:
            // a browser gives neither pseudo-class to an element with no range, and here everything
            // that never declared bounds is in range. Stated rather than smuggled — and unlike
            // `:valid` there is nothing to lose by it, since a control with no bounds cannot be
            // outside them.
            specificity = specificity with { Classes = specificity.Classes + 1 };
            var nested = table.AddNested(NegatedState(ElementState.OutOfRange));
            compiled = new SimpleSelector(SimpleSelectorKind.Not, NestedStart: nested, NestedCount: 1);

            return true;
        }

        Refuse(pseudo.Text, $":{name} is not supported");
        return false;
    }

    /// <summary>Compiles <c>:lang(&lt;tag&gt;)</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Selectors 4 allows more here than can ever arrive, and the reason is one
    ///         dependency out.</b> The specification takes a comma-separated list and allows <c>*</c>
    ///         as a subtag wildcard — <c>:lang(de, fr)</c>, <c>:lang("*-CH")</c>. ExCSS 4.3.2 parses
    ///         neither: measured, both come back as an <c>UnknownSelector</c> carrying the whole
    ///         compound, along with <c>:lang()</c> with an empty argument. So they are refused with a
    ///         diagnostic by the <c>default</c> arm of <see cref="TryCompileSimple" /> before this is
    ///         ever called, and no branch is written here to handle a wildcard that cannot be
    ///         delivered. A parser feature is what those forms wait on, not a matcher one.
    ///     </para>
    ///     <para>
    ///         The tag is kept verbatim rather than folded to lower case. BCP-47 is
    ///         case-insensitive, and the matcher compares that way; folding here would only move the
    ///         work and would lose the author's spelling from a diagnostic.
    ///     </para>
    /// </remarks>
    /// <param name="text">The whole pseudo-class, for a diagnostic.</param>
    /// <param name="tag">Its argument.</param>
    /// <param name="specificity">The specificity being accumulated.</param>
    /// <param name="compiled">The compiled selector.</param>
    /// <returns>Whether it compiled.</returns>
    bool TryCompileLang(string text, string tag, ref Specificity specificity, out SimpleSelector compiled) {
        compiled = default;

        if (tag.Length == 0 || !tag.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')) {
            Refuse(text, $"'{tag}' is not a language range Vixen supports");
            return false;
        }

        specificity = specificity with { Classes = specificity.Classes + 1 };

        // The attribute name is interned here so that matching never has to look it up: `:lang()`
        // reads the same `lang` attribute the tree already stores, and the walk is over ancestors,
        // so a dictionary probe per level would be paid on the hottest half of this selector.
        compiled = new SimpleSelector(SimpleSelectorKind.Lang, names.Intern("lang"), names.Intern(tag));
        return true;
    }

    Selector NegatedState(ElementState state) {
        var simpleStart = table.SimpleCount;
        table.AddSimple(new SimpleSelector(SimpleSelectorKind.State, State: state));
        var compoundStart = table.CompoundCount;
        table.AddCompound(new CompoundSelector(Combinator.None, simpleStart, 1));
        return new Selector(compoundStart, 1, new Specificity(0, 1, 0));
    }

    bool TryCompileNested(ISelector inner, SimpleSelectorKind kind, string text, out SimpleSelector compiled) {
        compiled = default;

        var parts = new List<Selector>();
        var before = diagnostics.Count;
        Compile(inner, parts);

        if (parts.Count == 0 || diagnostics.Count != before) {
            Refuse(text, "its argument could not be compiled");
            return false;
        }

        var start = table.NestedCount;
        foreach (var part in parts) {
            table.AddNested(part);
        }

        compiled = new SimpleSelector(kind, NestedStart: start, NestedCount: parts.Count);
        return true;
    }

    static string Trim(string text, char prefix) =>
        text.Length > 0 && text[0] == prefix ? text[1..] : text;
}
