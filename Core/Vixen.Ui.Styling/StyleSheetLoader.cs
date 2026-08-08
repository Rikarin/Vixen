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
///         <b><c>@media</c>.</b> Evaluated at load, not at match. A game's UI surface changes size
///         far less often than it restyles, so re-loading on a resize is cheaper than carrying a
///         condition on every rule and testing it on every match.
///     </para>
///     <para>
///         <b>Anything unsupported.</b> Dropped with a diagnostic naming what was written, never
///         approximated — the same rule the selector compiler follows and for the same reason.
///     </para>
/// </remarks>
public sealed class StyleSheetLoader {
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
    public StyleSheetLoader(StyleRuleSet rules, KeyframesTable keyframes, SelectorCompiler compiler) {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(keyframes);
        ArgumentNullException.ThrowIfNull(compiler);

        this.rules = rules;
        this.keyframes = keyframes;
        this.compiler = compiler;
    }

    /// <summary>Everything that could not be loaded, and why.</summary>
    /// <remarks>The selector compiler's own diagnostics are separate and equally worth reading.</remarks>
    public IReadOnlyList<SelectorDiagnostic> Diagnostics => diagnostics;

    /// <summary>Every distinct <c>@media</c> condition this loader has been asked to evaluate.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes "the surface changed, does anything care" answerable without reloading to
    ///     find out.</b> Conditions are decided at load and then thrown away, so an engine handed a new
    ///     <see cref="MediaContext" /> had no way to tell a resize that flips a breakpoint from one
    ///     that moves a window by a pixel — and the cheap answer, reload always, is a full ExCSS parse
    ///     of every sheet on every frame of a window drag. See <see cref="StyleEngine.SetMedia" />.
    ///
    ///     A set rather than a list: one generated utility sheet repeats <c>(min-width: 768px)</c>
    ///     once per <c>md:</c> class in the project, and replaying a hundred copies of one question is
    ///     a hundred times the work for the same answer.
    /// </remarks>
    public IReadOnlyCollection<string> MediaConditions => conditions;

    readonly HashSet<string> conditions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads a stylesheet.</summary>
    /// <param name="css">Its text.</param>
    /// <param name="origin">Who it came from.</param>
    /// <param name="media">What to evaluate <c>@media</c> against.</param>
    public void Load(string css, StyleOrigin origin, MediaContext media) {
        ArgumentNullException.ThrowIfNull(css);
        LoadInto(Parser.Parse(css), origin, media, CascadeLayers.Unlayered);
    }

    void LoadInto(IStylesheetNode node, StyleOrigin origin, MediaContext media, int layer) {
        foreach (var child in node.Children) {
            switch (child) {
                case IStyleRule style:
                    AddRule(style, origin, layer);
                    break;

                case IMediaRule query:
                    LoadMedia(query, origin, media, layer);
                    break;

                case IKeyframesRule frames:
                    LoadKeyframes(frames);
                    break;

                case IRule rule when rule.Type == RuleType.Unknown:
                    LoadUnknown(rule, origin, media, layer);
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
                Collect(declaration.Name, declaration.Value, declaration.IsImportant);
            }

            keyframes.Add(rule.Name, offset, CollectionsMarshal.AsSpan(declarationScratch));
        }
    }

    void LoadMedia(IMediaRule query, StyleOrigin origin, MediaContext media, int layer) {
        // ⚠ Recorded before the verdict and whether or not the condition could be read, because what
        // needs it is `StyleEngine.SetMedia` deciding whether a resize could have changed any answer
        // — and a block that is currently dropped is exactly a block a wider window might keep.
        // Recording only the ones that matched would make growing a window work and shrinking it not.
        conditions.Add(query.ConditionText ?? string.Empty);

        if (!MediaQuery.TryEvaluate(query.ConditionText, media, out var applies, out var reason)) {
            diagnostics.Add(new SelectorDiagnostic($"@media {query.ConditionText}", reason!));
            return;
        }

        if (applies) {
            // Straight through, carrying the layer: `@layer x { @media … { … } }` and
            // `@media … { @layer x { … } }` mean the same thing and both have to reach the same place.
            LoadInto(query, origin, media, layer);
        }
    }

    void LoadUnknown(IRule rule, StyleOrigin origin, MediaContext media, int layer) {
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

        LoadInto(Parser.Parse(parsed.Body), origin, media, inner);
    }

    string Qualify(string name, int outer) =>
        outer == CascadeLayers.Unlayered ? name : $"{rules.Layers.NameOf(outer)}.{name}";

    void AddRule(IStyleRule style, StyleOrigin origin, int layer) {
        selectorScratch.Clear();
        compiler.Compile(style.Selector, selectorScratch);

        if (selectorScratch.Count == 0) {
            // The compiler has already said why.
            return;
        }

        declarationScratch.Clear();
        foreach (var declaration in style.Style) {
            Collect(declaration.Name, declaration.Value, declaration.IsImportant);
        }

        // A comma-separated selector is several rules that happen to share a declaration block, and
        // the cascade has to see them as several: `#a, .b { … }` beats `.c` for one element and
        // loses to it for another.
        foreach (var selector in selectorScratch) {
            rules.Add(selector, CollectionsMarshal.AsSpan(declarationScratch), origin, layer);
        }
    }

    /// <summary>Interns one declaration, taking a shorthand apart first when ExCSS could not.</summary>
    /// <remarks>
    ///     ⚠ <b>Only when the value holds a <c>var()</c>.</b> Everything else ExCSS has already
    ///     expanded, and running over its output would be this file second-guessing the parser
    ///     rather than filling the one hole it leaves — see <see cref="ShorthandExpansion" /> for
    ///     what that hole is and why it is silent.
    /// </remarks>
    void Collect(string name, string value, bool important) {
        if (ShorthandExpansion.IsShorthand(name) && VarSubstitution.NeedsSubstitution(value)) {
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
                    "this shorthand holds a var() and could not be taken apart, so the longhands it "
                    + "would have set are not set"
                )
            );
        }

        Intern(name, value, important);
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
