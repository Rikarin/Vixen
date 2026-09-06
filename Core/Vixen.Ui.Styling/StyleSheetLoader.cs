// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text;
using ExCSS;
using Selector = Vixen.Ui.Styling.Selector;

namespace Vixen.Ui.Styling;

/// <summary>Turns stylesheet text into rules the cascade can run.</summary>
/// <remarks>
///     <para>
///         ExCSS parses; this walks what it produced and decides what each piece <i>is</i>. Three
///         things it has to handle that ExCSS does not:
///     </para>
///     <para>
///         <b><c>@layer</c>.</b> ExCSS 4.3.2 predates cascade layers and hands the rule back
///         unparsed, text intact — which
///         [the spike](../../docs/plan/spikes/vcss-excss/RESULT.md) established deliberately, before
///         this depended on it. The prelude is read here and the body is handed straight back to
///         ExCSS, so the gap stops at this file.
///     </para>
///     <para>
///         <b><c>@media</c>.</b> A block's rules are loaded whether or not it applies, each tagged
///         with the <see cref="MediaConditions" /> group it came from, and the verdict is a surface's
///         rather than the rule set's — which is what lets two windows of one document answer
///         <c>max-width</c> differently. It was decided at load and thrown away until then, and the
///         cost of that was not the trade it looked like: a resize that crossed a breakpoint had to
///         re-parse every sheet with ExCSS (42 ms for the editor's twelve), and it restarted every
///         fade in the window while doing it.
///     </para>
///     <para>
///         ⚠ <b>A sheet loaded with a <i>fixed</i> context keeps the old behaviour, and that is not
///         an inconsistency.</b> <c>StyleEngine.Load</c>'s <c>media</c> argument means "this sheet is
///         about a 320-pixel surface", which is a fixed question, so it gets a fixed answer decided
///         here and no group. A sheet that named no context is about whatever surface it is being
///         shown on, and there is more than one of those.
///     </para>
///     <para>
///         ⚠ <b>Only style rules are conditional. <c>@keyframes</c> and <c>@layer</c> inside a
///         <c>@media</c> load unconditionally</b>, because both are document-global by construction —
///         one <see cref="KeyframesTable" /> and one layer order per rule set, shared by every
///         surface exactly as the rules are. Neither does anything on its own: a keyframes definition
///         is inert until an <c>animation-name</c> names it, and that declaration is in a rule and is
///         gated; a layer with no rules in it only fixes an order. So loading them regardless is
///         invisible rather than merely convenient, and the alternative — a keyframes table per
///         surface — would be a much larger change for no case anybody has.
///     </para>
///     <para>
///         <b>Anything unsupported.</b> Dropped with a diagnostic naming what was written, never
///         approximated — the same rule the selector compiler follows and for the same reason.
///     </para>
/// </remarks>
public sealed class StyleSheetLoader {
    /// <summary>What a refusal from an inline <c>style="…"</c> names instead of a selector.</summary>
    const string InlineStyleAttribute = "a style=\"…\" attribute";

    static readonly StylesheetParser Parser = new(true, true, true, true, true);

    readonly StyleRuleSet rules;
    readonly KeyframesTable keyframes;
    readonly SelectorCompiler compiler;
    readonly List<SelectorDiagnostic> diagnostics = [];
    readonly List<Selector> selectorScratch = [];
    readonly List<Declaration> declarationScratch = [];
    readonly List<KeyValuePair<string, string>> expansionScratch = [];

    /// <summary>Creates a loader.</summary>
    /// <param name="rules">The set to load into.</param>
    /// <param name="keyframes">The table <c>@keyframes</c> rules load into.</param>
    /// <param name="compiler">The selector compiler.</param>
    /// <param name="conditions">The table <c>@media</c> groups are registered in.</param>
    /// <param name="containers">The table <c>@container</c> groups are registered in.</param>
    public StyleSheetLoader(
        StyleRuleSet rules,
        KeyframesTable keyframes,
        SelectorCompiler compiler,
        MediaConditions conditions,
        ContainerConditions containers
    ) {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(keyframes);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(containers);

        this.rules = rules;
        this.keyframes = keyframes;
        this.compiler = compiler;
        Conditions = conditions;
        Containers = containers;
    }

    /// <summary>Everything that could not be loaded, and why.</summary>
    /// <remarks>The selector compiler's own diagnostics are separate and equally worth reading.</remarks>
    public IReadOnlyList<SelectorDiagnostic> Diagnostics => diagnostics;

    /// <summary>The conditional groups this loader has registered.</summary>
    /// <remarks>
    ///     Owned by <see cref="StyleEngine" /> rather than by this, because a group id is carried by
    ///     a rule and read by a surface, and both of those outlive the loader — <c>Build</c> replaces
    ///     it on every reload.
    /// </remarks>
    public MediaConditions Conditions { get; }

    /// <summary>The <c>@container</c> groups this loader has registered.</summary>
    /// <remarks>Owned by <see cref="StyleEngine" /> for the reason <see cref="Conditions" /> is.</remarks>
    public ContainerConditions Containers { get; }

    /// <summary>Loads a stylesheet.</summary>
    /// <param name="css">Its text.</param>
    /// <param name="origin">Who it came from.</param>
    /// <param name="media">
    ///     A fixed context to decide <c>@media</c> against, or <c>null</c> to register a conditional
    ///     group per block and let each surface decide.
    ///     <para>
    ///         ⚠ <b>Nullable, and the two states are "this sheet is about a 320-pixel surface" and
    ///         "this sheet is about whatever surface it is shown on".</b> Only the second can have
    ///         more than one answer at a time, so only the second registers groups.
    ///     </para>
    /// </param>
    public void Load(string css, StyleOrigin origin, MediaContext? media) {
        ArgumentNullException.ThrowIfNull(css);
        LoadInto(
            Parser.Parse(CarrySystemColours(RefuseRelativeHas(css))),
            origin,
            media,
            CascadeLayers.Unlayered,
            MediaConditions.Unconditional,
            ContainerConditions.Unconditional
        );
    }

    /// <summary>Renames every CSS system colour keyword in a value so that ExCSS leaves it alone.</summary>
    /// <param name="css">The stylesheet text.</param>
    /// <returns>The same text with the keywords carried, or the same instance when there are none.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Before the parser, for <see cref="RefuseRelativeHas" />'s reason: the parser is
    ///         where the evidence is destroyed.</b> ExCSS normalises the CSS2 system colours it
    ///         knows into fixed <c>rgb()</c> as it parses, so <c>background-color: Highlight</c>
    ///         reached <c>StyleValueParser</c> as <c>rgb(181, 213, 255)</c> and
    ///         <see cref="SystemPalette" /> was never asked. ⚠ <b>The five it froze are the five a
    ///         control theme actually names</b> — <c>ButtonFace</c>, <c>ButtonText</c>,
    ///         <c>Highlight</c>, <c>HighlightText</c>, <c>GrayText</c> — so the forced-colours mode
    ///         and the platform palette were both a third smaller than they read, and a
    ///         high-contrast user got a light grey button face on a black window with nothing
    ///         anywhere to say so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Undoing the normalisation afterwards is not the alternative, it is the wrong
    ///         answer.</b> <c>rgb(221, 221, 221)</c> is also a colour an author may have written on
    ///         purpose, and nothing downstream can tell the two apart. A rename before the parse
    ///         guesses at nothing: this method is looking at the source.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Bounded to value positions, which is what keeps it from being a find-and-replace
    ///         over the sheet.</b> A keyword is renamed only inside a block, after a <c>:</c> and
    ///         before the <c>;</c> or <c>}</c> that ends the declaration — so a <c>Highlight</c> tag
    ///         selector, a <c>.Highlight</c> class, a <c>--highlight</c> custom property <i>name</i>
    ///         and a <c>content: "Highlight"</c> are all left as they are. Comments, quoted strings
    ///         and <c>url(…)</c> are skipped for the same reason, and an identifier is taken whole,
    ///         so <c>var(--canvas)</c> and <c>-vx-canvas</c> are one token each and match nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And bounded by property as well as by position</b>, because a value position is
    ///         not enough: <c>animation-name: mark</c> and <c>grid-area: field</c> are names the
    ///         author chose, and renaming one is an animation that never finds its
    ///         <c>@keyframes</c> — see <see cref="NamesRatherThanPaints" />.
    ///     </para>
    ///     <para>
    ///         The one text this cannot reach is an <c>@layer</c> body ExCSS hands back without a
    ///         <c>StylesheetText</c>, where <see cref="LoadUnknown" /> falls back to <c>ToCss()</c> —
    ///         serialised text, normalised already. That fallback is a last resort in a rule form
    ///         ExCSS does not model, and it loses more than this.
    ///     </para>
    /// </remarks>
    static string CarrySystemColours(string css) {
        StringBuilder? carried = null;
        var copied = 0;
        var depth = 0;
        var value = false;
        var property = default(Range);

        for (var i = 0; i < css.Length;) {
            var c = css[i];

            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*') {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 2;
                continue;
            }

            if (c is '"' or '\'') {
                i = SkipQuoted(css, i);
                continue;
            }

            switch (c) {
                case '{':
                    depth++;
                    value = false;
                    i++;
                    continue;
                case '}':
                    depth = Math.Max(0, depth - 1);
                    value = false;
                    i++;
                    continue;
                case ';':
                    value = false;
                    i++;
                    continue;
                case ':':
                    // ⚠ At depth zero this is a pseudo-class, not a declaration — `a:hover` is a
                    // prelude. Only a block has declarations in it.
                    value = depth > 0 && !NamesRatherThanPaints(css.AsSpan(property));
                    i++;
                    continue;
            }

            if (!IsIdentStart(c)) {
                i++;
                continue;
            }

            var start = i;

            while (i < css.Length && IsIdentPart(css[i])) {
                i++;
            }

            // The last identifier before a `:` is the property it declares — see the `:` arm.
            property = start..i;

            if (i < css.Length && css[i] == '(') {
                // A function name is not a keyword. `url()` alone takes an unquoted argument that is
                // not a token stream, so its contents are stepped over rather than walked.
                if (css.AsSpan(start, i - start).Equals("url", StringComparison.OrdinalIgnoreCase)) {
                    var close = css.IndexOf(')', i);
                    i = close < 0 ? css.Length : close + 1;
                }

                continue;
            }

            if (!value || !SystemPalette.TryParse(css.AsSpan(start, i - start), out _)) {
                continue;
            }

            carried ??= new StringBuilder(css.Length + 64);
            carried.Append(css, copied, start - copied)
                .Append(SystemPalette.Carrier)
                .Append(css, start, i - start);

            copied = i;
        }

        return carried is null ? css : carried.Append(css, copied, css.Length - copied).ToString();
    }

    /// <summary>Steps over a quoted string, escapes included.</summary>
    /// <param name="css">The text.</param>
    /// <param name="index">The opening quote.</param>
    /// <returns>Just past the closing quote, or the end of the text.</returns>
    static int SkipQuoted(string css, int index) {
        var quote = css[index];

        for (var i = index + 1; i < css.Length; i++) {
            if (css[i] == '\\') {
                i++;
                continue;
            }

            if (css[i] == quote) {
                return i + 1;
            }
        }

        return css.Length;
    }

    static bool IsIdentStart(char c) => char.IsLetter(c) || c is '-' or '_' || c >= 0x80;

    static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' || c >= 0x80;

    /// <summary>Whether a property's value is a name the author chose rather than anything paintable.</summary>
    /// <param name="property">The property, as it was written.</param>
    /// <remarks>
    ///     ⚠ <b>The one way the rename can break a sheet that has nothing to do with colour.</b>
    ///     <c>Mark</c>, <c>Field</c> and <c>Highlight</c> are perfectly ordinary names for an
    ///     animation, a grid area or a font, and <c>animation-name: mark</c> renamed is an animation
    ///     that no longer finds its <c>@keyframes</c> — a rule quietly doing nothing, which is worse
    ///     than a wrong colour because nothing is left to look at.
    ///     <para>
    ///         Both shorthands are here as well as their longhands, because neither <c>font</c> nor
    ///         <c>animation</c> can carry a colour and both can carry a name. ⚠ It is a list of
    ///         properties whose value is <em>never</em> a colour, and not a list of properties that
    ///         might take a name — the second would have to include <c>background</c>, which takes
    ///         both, and excluding that would put the defect back.
    ///     </para>
    /// </remarks>
    static bool NamesRatherThanPaints(ReadOnlySpan<char> property) {
        // Lowered rather than compared case-insensitively because a span pattern is ordinal, and a
        // CSS property name is ASCII case-insensitive like every other CSS keyword. Anything longer
        // than the buffer is longer than every name below.
        Span<char> lowered = stackalloc char[32];

        if (property.Length is 0 or > 32) {
            return false;
        }

        property.ToLowerInvariant(lowered);

        return lowered[..property.Length] switch {
            "animation" or "animation-name" => true,
            "font" or "font-family" => true,
            "container" or "container-name" => true,
            "counter-increment" or "counter-reset" or "counter-set" => true,
            "grid-area" or "grid-row" or "grid-column" => true,
            "grid-row-start" or "grid-row-end" or "grid-column-start" or "grid-column-end" => true,
            "transition-property" or "will-change" or "view-transition-name" => true,
            _ => false
        };
    }

    /// <summary>Drops every rule whose selector uses a relative <c>:has()</c> argument.</summary>
    /// <param name="css">The stylesheet text.</param>
    /// <returns>The same text with those rules cut out, or the same instance when there are none.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Before the parser, because the parser is where the evidence is destroyed.</b>
    ///         ExCSS 4.3.2 parses <c>:has(&gt; .x)</c> into the node it parses <c>:has(.x)</c> into,
    ///         combinator gone from the tree <i>and</i> from <c>ISelector.Text</c>. So
    ///         <c>.card:has(&gt; .error)</c> compiled, matched, and meant "a <c>.error</c> anywhere
    ///         below" — a rule quietly meaning something wider than it says, which is the one thing
    ///         this loader and <see cref="SelectorCompiler" /> exist to refuse. There was nothing
    ///         left for the compiler to refuse it <i>with</i>: a leading combinator does not leave a
    ///         second compound behind, it leaves nothing at all.
    ///     </para>
    ///     <para>
    ///         The same is true of <c>:has(+ .x)</c> and <c>:has(~ .x)</c>, and those disagree more
    ///         loudly still — a relative <i>sibling</i> selector answered as a descendant one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The whole rule goes, not the one selector in its list.</b> That is CSS's own
    ///         rule for an invalid selector in a group, and it is also the only one available here:
    ///         what is being cut is source text, before anything has split the list.
    ///     </para>
    ///     <para>
    ///         A text scan is inelegant and is not a parser. It is bounded to preludes — the span
    ///         between the last <c>;</c>, <c>{</c> or <c>}</c> and the next <c>{</c> — with comments
    ///         and quoted strings skipped, so a <c>content: ":has(&gt; x)"</c> is not a rule and is
    ///         not read as one. It answers "is there a combinator directly inside <c>:has(</c>",
    ///         which is the whole of what the parser will shortly throw away, and the far more
    ///         expensive alternative is to stop using ExCSS's selector tree.
    ///     </para>
    /// </remarks>
    string RefuseRelativeHas(string css) {
        // The scan below is a character walk over the whole sheet, and nearly every sheet in the
        // tree contains no `:has()` at all.
        if (css.IndexOf(":has(", StringComparison.OrdinalIgnoreCase) < 0) {
            return css;
        }

        StringBuilder? kept = null;
        var copied = 0;
        var prelude = 0;
        var relative = -1;

        for (var i = 0; i < css.Length; i++) {
            var c = css[i];

            // An escaped character is one character of an identifier and never a delimiter — the
            // same rule `LayerRuleParser` learned the hard way, and for the same reason: a generated
            // utility selector is `.f-\[\"onum\"_1\]`, and reading that first `\"` as a quote opens
            // a string that swallows the next brace.
            if (c == '\\') {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*') {
                i = EndOfComment(css, i);
                continue;
            }

            if (c is '"' or '\'') {
                i = EndOfString(css, i);
                continue;
            }

            // A declaration ends a prelude just as a block does, which is what keeps a `:has(` seen
            // in a property value from being attributed to the rule after it.
            if (c is ';' or '}') {
                prelude = i + 1;
                relative = -1;
                continue;
            }

            if (c == '{') {
                if (relative < 0) {
                    prelude = i + 1;
                    continue;
                }

                var end = EndOfBlock(css, i);

                kept ??= new StringBuilder(css.Length);
                kept.Append(css, copied, prelude - copied);
                copied = end;

                diagnostics.Add(
                    new SelectorDiagnostic(
                        css[relative..EndOfArgument(css, relative)],
                        "a relative ':has()' argument is not supported — the combinator is lost before Vixen sees "
                        + "it, so the rule would silently mean 'anywhere in the subtree'",
                        css[prelude..i].Trim()
                    )
                );

                i = end - 1;
                prelude = end;
                relative = -1;
                continue;
            }

            if (c == ':'
                && relative < 0
                && string.Compare(css, i, ":has(", 0, 5, StringComparison.OrdinalIgnoreCase) == 0
                && IsCombinator(css, i + 5)) {
                relative = i;
            }
        }

        if (kept is null) {
            return css;
        }

        kept.Append(css, copied, css.Length - copied);

        return kept.ToString();
    }

    /// <summary>Whether the first thing after an offset is a combinator rather than a compound.</summary>
    static bool IsCombinator(string css, int from) {
        for (var i = from; i < css.Length; i++) {
            if (!char.IsWhiteSpace(css[i])) {
                return css[i] is '>' or '+' or '~';
            }
        }

        return false;
    }

    /// <summary>The index one past the <c>)</c> closing a <c>:has(</c> that starts at an offset.</summary>
    static int EndOfArgument(string css, int from) {
        var depth = 0;

        for (var i = from; i < css.Length; i++) {
            switch (css[i]) {
                case '(':
                    depth++;
                    break;

                case ')' when --depth == 0:
                    return i + 1;
            }
        }

        return css.Length;
    }

    /// <summary>The index one past the <c>}</c> closing a block that opens at an offset.</summary>
    static int EndOfBlock(string css, int from) {
        var depth = 0;

        for (var i = from; i < css.Length; i++) {
            var c = css[i];

            if (c == '\\') {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*') {
                i = EndOfComment(css, i);
                continue;
            }

            if (c is '"' or '\'') {
                i = EndOfString(css, i);
                continue;
            }

            switch (c) {
                case '{':
                    depth++;
                    break;

                case '}' when --depth == 0:
                    return i + 1;
            }
        }

        // Unterminated. Everything from here on was going to be part of the refused rule anyway.
        return css.Length;
    }

    /// <summary>The index of the <c>/</c> ending a comment that opens at an offset.</summary>
    static int EndOfComment(string css, int from) {
        var end = css.IndexOf("*/", from + 2, StringComparison.Ordinal);
        return end < 0 ? css.Length - 1 : end + 1;
    }

    /// <summary>The index of the quote ending a string that opens at an offset.</summary>
    static int EndOfString(string css, int from) {
        for (var i = from + 1; i < css.Length; i++) {
            if (css[i] == '\\') {
                i++;
                continue;
            }

            if (css[i] == css[from]) {
                return i;
            }
        }

        return css.Length - 1;
    }

    void LoadInto(
        IStylesheetNode node,
        StyleOrigin origin,
        MediaContext? media,
        int layer,
        int conditions,
        int containers
    ) {
        foreach (var child in node.Children) {
            switch (child) {
                case IStyleRule style:
                    AddRule(style, origin, layer, conditions, containers);
                    break;

                case IMediaRule query:
                    LoadMedia(query, origin, media, layer, conditions, containers);
                    break;

                case IKeyframesRule frames:
                    LoadKeyframes(frames);
                    break;

                // ⚠ Before the `Unknown` arm and by rule type rather than by text, because ExCSS
                // 4.3.2 does know this one. `@container` arrives as a first-class `ContainerRule`
                // with its name and condition already split out — so it never reached `LoadUnknown`,
                // never produced the "Vixen does not understand this rule" diagnostic that
                // `StyleDiagnosticDrainTests` and the stylesheet-diagnostics guide both said it
                // produced, and fell through this switch's `default` arm in complete silence.
                case IContainerRule container:
                    LoadContainer(container, origin, media, layer, conditions, containers);
                    break;

                case IRule rule when rule.Type == RuleType.Unknown:
                    LoadUnknown(rule, origin, media, layer, conditions, containers);
                    break;

                default:
                    // Selectors, declarations and the rest of ExCSS's node tree hang off the rules
                    // above; only the top level of each block is a rule at all.
                    break;
            }
        }
    }

    void LoadKeyframes(IKeyframesRule rule) {
        // A redefinition replaces rather than merges, so a stop from a discarded definition cannot
        // survive into the one that replaced it.
        keyframes.Remove(rule.Name);

        foreach (var stop in rule.Children.OfType<IKeyframeRule>()) {
            if (!KeyframesTable.TryParseOffset(stop.KeyText, out var offset)) {
                diagnostics.Add(
                    new SelectorDiagnostic($"@keyframes {rule.Name}", $"'{stop.KeyText}' is not a keyframe offset")
                );

                continue;
            }

            declarationScratch.Clear();
            foreach (var declaration in stop.Style) {
                // The stop rather than the animation: `@keyframes fade` may have six of them, and
                // the one to go and fix is the offset the declaration is written under.
                var where = $"@keyframes {rule.Name} {{ {stop.KeyText} }}";

                if (!TryReadValue(declaration, where, out var value)) {
                    continue;
                }

                Collect(declaration.Name, value, declaration.IsImportant, where);
            }

            keyframes.Add(rule.Name, offset, CollectionsMarshal.AsSpan(declarationScratch));
        }
    }

    void LoadMedia(
        IMediaRule query,
        StyleOrigin origin,
        MediaContext? media,
        int layer,
        int conditions,
        int containers
    ) {
        // ⚠ Readability is decided here whichever mode this is in, and it is decided against the
        // *text*. Every refusal `MediaQuery.TryEvaluate` can produce names a feature, a value or a
        // length it could not parse; not one of them reads the context it was handed. So a condition
        // that cannot be read cannot be read on any surface, and the diagnostic belongs at load —
        // where `UiDocument` already drains it — rather than once per surface per evaluation, in a
        // list nothing reads.
        if (!MediaQuery.TryEvaluate(query.ConditionText, media ?? default, out var applies, out var reason)) {
            diagnostics.Add(new SelectorDiagnostic($"@media {query.ConditionText}", reason!));
            return;
        }

        if (media is not null) {
            // The fixed-context form: one surface was named, so the question has one answer and the
            // block is kept or dropped here exactly as it always was.
            if (applies) {
                LoadInto(query, origin, media, layer, conditions, containers);
            }

            return;
        }

        // Straight through whether or not it currently applies, carrying both the layer and the
        // group: `@layer x { @media … { … } }` and `@media … { @layer x { … } }` mean the same thing
        // and both have to reach the same place, and a nested `@media` conjoins with this one by
        // registering inside it.
        LoadInto(
            query,
            origin,
            media,
            layer,
            Conditions.Register(conditions, query.ConditionText),
            containers
        );
    }

    /// <summary>Loads a <c>@container</c> block, registering the group its rules are inside.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Always loaded, whatever any container currently measures, and for a stronger
    ///         reason than <c>@media</c>'s.</b> A media verdict is at least constant across a
    ///         document at one instant; a container verdict differs between two panels showing the
    ///         same rules at the same moment, so there is no size at which the block could be decided
    ///         at load even in principle. The rules carry their group and the cascade asks per
    ///         element.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The condition is checked for readability here and against no box.</b> Every
    ///         refusal <see cref="ContainerQuery.TryEvaluate" /> can produce names a feature or a
    ///         length it could not parse and none of them reads the box — so an unreadable condition
    ///         is unreadable everywhere, and the diagnostic belongs at load rather than once per
    ///         container per frame in a list nothing drains.
    ///     </para>
    ///     <para>
    ///         Nesting is free and it nests through <c>@media</c> in either order: the two tables are
    ///         independent, so this carries the media group past untouched exactly as
    ///         <see cref="LoadMedia" /> carries the container group.
    ///     </para>
    /// </remarks>
    void LoadContainer(
        IContainerRule rule,
        StyleOrigin origin,
        MediaContext? media,
        int layer,
        int conditions,
        int containers
    ) {
        var name = rule.Name ?? string.Empty;
        var label = name.Length == 0 ? "@container" : $"@container {name}";

        if (!ContainerQuery.TryEvaluate(rule.ConditionText, default, out _, out var reason)) {
            diagnostics.Add(new SelectorDiagnostic($"{label} {rule.ConditionText}", reason!));
            return;
        }

        LoadInto(
            rule,
            origin,
            media,
            layer,
            conditions,
            Containers.Register(containers, name, rule.ConditionText)
        );
    }

    void LoadUnknown(
        IRule rule,
        StyleOrigin origin,
        MediaContext? media,
        int layer,
        int conditions,
        int containers
    ) {
        var text = rule.StylesheetText?.Text ?? rule.ToCss();

        if (!LayerRuleParser.IsLayerRule(text)) {
            diagnostics.Add(new SelectorDiagnostic(Summarise(text), "Vixen does not understand this rule"));
            return;
        }

        if (!LayerRuleParser.TryParse(text, out var parsed)) {
            diagnostics.Add(new SelectorDiagnostic(Summarise(text), "this @layer rule could not be read"));
            return;
        }

        if (parsed.Body is null) {
            // The statement form. It contributes no rules; all it does is fix the order, which is
            // the entire reason to write one.
            foreach (var name in parsed.Names) {
                rules.Layers.Declare(Qualify(name, layer));
            }

            return;
        }

        if (parsed.Names.Count > 1) {
            diagnostics.Add(
                new SelectorDiagnostic(Summarise(text), "a block @layer names one layer, not a list")
            );

            return;
        }

        // An anonymous `@layer { … }` gets a name nothing can spell, so no later rule can reopen it.
        var inner = parsed.Names.Count == 1
            ? rules.Layers.Declare(Qualify(parsed.Names[0], layer))
            : rules.Layers.Declare($"\0anonymous-{rules.Layers.Count}");

        LoadInto(Parser.Parse(parsed.Body), origin, media, inner, conditions, containers);
    }

    string Qualify(string name, int outer) =>
        outer == CascadeLayers.Unlayered ? name : $"{rules.Layers.NameOf(outer)}.{name}";

    void AddRule(IStyleRule style, StyleOrigin origin, int layer, int conditions, int containers) {
        selectorScratch.Clear();
        compiler.Compile(style.Selector, selectorScratch);

        if (selectorScratch.Count == 0) {
            // The compiler has already said why.
            return;
        }

        declarationScratch.Clear();
        foreach (var declaration in style.Style) {
            // ⚠ The selector, so that a shorthand this could not take apart names the rule it was
            // written in. `border: var(--x) solid` is the same six words in every rule that has it,
            // and the refusal is otherwise indistinguishable between two of them.
            if (!TryReadValue(declaration, style.SelectorText, out var value)) {
                continue;
            }

            Collect(declaration.Name, value, declaration.IsImportant, style.SelectorText);
        }

        // A comma-separated selector is several rules that happen to share a declaration block, and
        // the cascade has to see them as several: `#a, .b { … }` beats `.c` for one element and
        // loses to it for another.
        foreach (var selector in selectorScratch) {
            rules.Add(
                selector,
                CollectionsMarshal.AsSpan(declarationScratch),
                origin,
                layer,
                conditions,
                containers
            );
        }
    }

    /// <summary>Reads the declaration list in a <c>style="…"</c> attribute.</summary>
    /// <param name="css">The attribute's value: <c>width: 42%; flex-grow: 1</c>.</param>
    /// <param name="into">Where the declarations go. Cleared first.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same parser a rule body goes through, wrapped in a throwaway rule.</b> A
    ///         hand-rolled splitter on <c>;</c> and <c>:</c> is four lines and wrong in three ways —
    ///         a <c>;</c> inside a string, a <c>:</c> inside a <c>url()</c>, and above all the
    ///         shorthands, which <see cref="AddRule" /> gets from ExCSS for free. <c>padding: 4px</c>
    ///         has to become the four longhands the layout actually reads, and a second implementation
    ///         of that is a second thing that can disagree with a stylesheet about what the same
    ///         characters mean.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A brace is refused rather than escaped.</b> The wrapper is textual, so
    ///         <c>style="} tabs { display: none"</c> would otherwise close it and load a rule against
    ///         the whole document. Only the first rule is read, so the worst an injection could do is
    ///         lose declarations — but refusing outright is one rule a reader can hold, and no inline
    ///         style has a legitimate brace in it: there are no nested blocks and no at-rules here.
    ///     </para>
    ///     <para>
    ///         Refusals land in <see cref="Diagnostics" /> along with every other sheet's, so they are
    ///         drained and logged by the pass that already reads that list. A declaration ExCSS did not
    ///         recognise is dropped silently, exactly as it is in a stylesheet — the loud case is a
    ///         value that produced <i>nothing</i>, which is the typo worth a line.
    ///     </para>
    /// </remarks>
    public void ReadDeclarations(string css, List<InlineDeclaration> into) {
        ArgumentNullException.ThrowIfNull(css);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        if (string.IsNullOrWhiteSpace(css)) {
            return;
        }

        if (css.Contains('{', StringComparison.Ordinal) || css.Contains('}', StringComparison.Ordinal)) {
            diagnostics.Add(
                new SelectorDiagnostic(
                    Summarise(css),
                    "an inline style is a declaration list, so a brace cannot appear in one"
                )
            );

            return;
        }

        // ⚠ The braces go on before the carrier runs, not after: `CarrySystemColours` renames a
        // keyword only inside a block, and an inline style is a declaration list with no block
        // around it. Carrying the bare list would rename nothing and `style="color: Highlight"`
        // would keep the one defect the sheet path has just lost.
        if (Parser.Parse(CarrySystemColours("*{" + css + "}")).Children.FirstOrDefault() is IStyleRule rule) {
            foreach (var declaration in rule.Style) {
                if (!TryReadValue(declaration, InlineStyleAttribute, out var value)) {
                    continue;
                }

                ReadDeclaration(declaration.Name, value, declaration.IsImportant, into);
            }
        }

        if (into.Count == 0) {
            diagnostics.Add(new SelectorDiagnostic(Summarise(css), "nothing here parsed as a CSS declaration"));
        }
    }

    /// <summary>The <see cref="Collect" /> of the inline path: same shorthand hole, same patch.</summary>
    void ReadDeclaration(string name, string value, bool important, List<InlineDeclaration> into) {
        if (ShorthandExpansion.NeedsExpanding(name, value)) {
            expansionScratch.Clear();

            if (ShorthandExpansion.TryExpand(name, value, expansionScratch)) {
                foreach (var (longhand, part) in expansionScratch) {
                    into.Add(new InlineDeclaration(longhand, part, important));
                }

                return;
            }

            diagnostics.Add(
                new SelectorDiagnostic(
                    $"{name}: {value}",
                    "this shorthand could not be taken apart, so the longhands it would have set "
                    + "are not set",
                    // There is no rule to name — a `style="…"` attribute belongs to one element and
                    // has no selector — but saying so is still worth more than saying nothing, because
                    // it tells a reader not to go looking through the stylesheets for it.
                    InlineStyleAttribute
                )
            );
        }

        into.Add(new InlineDeclaration(name, value, important));
    }

    /// <summary>Interns one declaration, taking a shorthand apart first when ExCSS could not.</summary>
    /// <remarks>
    ///     ⚠ <b>Which shorthands and when is <see cref="ShorthandExpansion.NeedsExpanding" />'s
    ///     question, not this file's.</b> There are two holes and they have different conditions —
    ///     a shorthand ExCSS knows arrives unexpanded only when it holds a <c>var()</c>, and one
    ///     ExCSS has never heard of arrives unexpanded always — so a condition written here would be
    ///     half of the rule with nothing to say why. Running over ExCSS's own output remains out of
    ///     the question; see <see cref="ShorthandExpansion" /> for both holes and why each is silent.
    /// </remarks>
    void Collect(string name, string value, bool important, string? rule) {
        if (ShorthandExpansion.NeedsExpanding(name, value)) {
            expansionScratch.Clear();

            if (ShorthandExpansion.TryExpand(name, value, expansionScratch)) {
                foreach (var (longhand, part) in expansionScratch) {
                    Intern(longhand, part, important);
                }

                return;
            }

            // Kept as it stands rather than dropped: the declaration is still what the author wrote,
            // and a stylesheet that stopped applying because this file could not divide a value up
            // would be a worse failure than the one being reported.
            diagnostics.Add(
                new SelectorDiagnostic(
                    $"{name}: {value}",
                    "this shorthand could not be taken apart, so the longhands it would have set "
                    + "are not set",
                    rule
                )
            );
        }

        Intern(name, value, important);
    }

    /// <summary>Reads a declaration's value, dropping the declaration when ExCSS cannot say what it is.</summary>
    /// <param name="declaration">The property ExCSS parsed.</param>
    /// <param name="rule">The rule to name in a refusal.</param>
    /// <param name="value">Its value, when there is one.</param>
    /// <returns>Whether the value could be read.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>ExCSS.Property.Value</c> can throw, and the four alignment properties are where
    ///         it does.</b> <c>align-items</c>, <c>align-self</c>, <c>align-content</c> and
    ///         <c>justify-content</c> are the ones ExCSS 4.3.2 models with a
    ///         <c>ConditionalStartsWithValueConverter</c>: it matches the conditional start token —
    ///         <c>safe</c>, <c>unsafe</c>, <c>first</c>, <c>last</c> — then fails to match a position
    ///         after it, and keeps a <c>ConditionalStartValue</c> whose inner value is null.
    ///         <c>CssText</c> dereferences it. A bare prefix is enough, and so is a prefix with
    ///         trailing junk. ⚠ <c>justify-items</c>, <c>justify-self</c> and <c>place-items</c> have
    ///         no such converter and pass through whole, so the crash is invisible from any one
    ///         property — which is why the tests cover all four.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is a crash on author input</b>, and it was not a dropped declaration: the
    ///         throw came out of <c>StyleEngine.Load</c> uncaught, so a single mistyped
    ///         <c>align-items</c> in any <c>.vcss</c> took the rules after it with it — and, because
    ///         the sheet's text is registered before it is loaded, every later <c>Replace</c> and
    ///         <c>Reload</c> on that engine threw again. One typo permanently poisoned the document.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>catch</c> is around the property read and nothing else, on purpose.</b> No
    ///         Vixen code runs inside it — the only frames are ExCSS's own getter — so a
    ///         <see cref="NullReferenceException" /> raised there cannot be this assembly's bug being
    ///         swallowed. A guard one level out, around the rule or the declaration loop, would cover
    ///         <see cref="Collect" /> and <see cref="Intern" /> as well, and would turn a real defect
    ///         in either of those into a silently dropped stylesheet.
    ///     </para>
    ///     <para>
    ///         The refusal goes into <see cref="Diagnostics" /> with every other one, so the pass that
    ///         already drains that list reports it. ⚠ It cannot quote the value: <c>ToCss()</c> on the
    ///         declaration, and on the whole rule, dereferences the same null.
    ///     </para>
    /// </remarks>
    bool TryReadValue(IProperty declaration, string? rule, out string value) {
        try {
            value = declaration.Value;
            return true;
        } catch (NullReferenceException) {
            value = string.Empty;

            diagnostics.Add(
                new SelectorDiagnostic(
                    declaration.Name,
                    "ExCSS matched the start of this value and then could not read it back, so the "
                    + "declaration is dropped — which is what a bare 'safe', 'unsafe', 'first' or "
                    + "'last' does to an alignment property",
                    rule
                )
            );

            return false;
        }
    }

    void Intern(string name, string value, bool important) =>
        declarationScratch.Add(
            new Declaration(rules.Properties.Intern(name), rules.Values.Intern(value), important)
        );

    static string Summarise(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return "(empty rule)";
        }

        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 60 ? single : single[..57] + "...";
    }
}
