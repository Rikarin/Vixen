// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
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
            Parser.Parse(css),
            origin,
            media,
            CascadeLayers.Unlayered,
            MediaConditions.Unconditional,
            ContainerConditions.Unconditional
        );
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

        if (Parser.Parse("*{" + css + "}").Children.FirstOrDefault() is IStyleRule rule) {
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
