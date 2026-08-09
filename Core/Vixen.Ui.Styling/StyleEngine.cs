// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Ui.Styling;

/// <summary>The whole styling pipeline, assembled.</summary>
/// <remarks>
///     <para>
///         The parts are separate because each one is worth testing on its own — the matcher against
///         a brute-force oracle, the cascade against CSS's own ordering rules, the sharing cache
///         against a resolver that does not share. They are also useless separately, and every one of
///         them needs the same four interning tables. This puts them together once.
///     </para>
///     <para>
///         Three name tables rather than one. Selector names, property names and value texts are
///         different namespaces, and folding them together would make the ancestor bloom's domain
///         include every colour in the stylesheet — a filter over a much larger set of names is a
///         filter that rejects less.
///     </para>
/// </remarks>
public sealed class StyleEngine {
    /// <summary>A sheet as it was handed in, kept so the rule set can be rebuilt without it.</summary>
    /// <param name="Css">Its text.</param>
    /// <param name="Origin">Who it came from.</param>
    /// <param name="Media">
    ///     The context it named, or <c>null</c> to follow <see cref="StyleEngine.Media" />.
    ///     <para>
    ///         ⚠ <b>Nullable, so that "this sheet is about a 320-pixel surface" and "this sheet is
    ///         about whatever surface the document is on" are different states.</b> They were the same
    ///         state before, and the one that lost was the second: <c>default(MediaContext)</c> is a
    ///         surface nought pixels wide, so every <c>@media (min-width: …)</c> in the engine was
    ///         evaluated against a window that does not exist and every one of them was false.
    ///     </para>
    /// </param>
    readonly record struct Sheet(string Css, StyleOrigin Origin, MediaContext? Media);

    readonly List<Sheet> sheets = [];

    /// <summary>Creates an engine with nothing loaded.</summary>
    public StyleEngine() {
        Names = new NameTable();
        Properties = new NameTable();
        Values = new NameTable();
        InlineStyles = new InlineStyleStore();
        Tree = new StyleTree(Names);
        Build();
    }

    /// <summary>How many stylesheets have been loaded.</summary>
    public int SheetCount => sheets.Count;

    /// <summary>The table selector names are interned in.</summary>
    public NameTable Names { get; }

    /// <summary>The table property names are interned in.</summary>
    public NameTable Properties { get; }

    /// <summary>The table declaration values are interned in.</summary>
    public NameTable Values { get; }

    /// <summary>The flat store compiled selectors point into.</summary>
    public SelectorTable Selectors { get; private set; }

    /// <summary>The ExCSS selector visitor.</summary>
    public SelectorCompiler Compiler { get; private set; }

    /// <summary>The loaded rules.</summary>
    public StyleRuleSet Rules { get; private set; }

    /// <summary>The selector matcher.</summary>
    public SelectorMatcher Matcher { get; private set; }

    /// <summary>Where computed styles are interned.</summary>
    public ComputedStyleCache Interning { get; private set; }

    /// <summary>The declarations written on elements themselves.</summary>
    public InlineStyleStore InlineStyles { get; }

    /// <summary>The <c>@keyframes</c> rules.</summary>
    public KeyframesTable Keyframes { get; private set; }

    /// <summary>The stylesheet loader.</summary>
    public StyleSheetLoader Loader { get; private set; }

    /// <summary>The cascade.</summary>
    public StyleResolver Resolver { get; private set; }

    /// <summary>The element store.</summary>
    public StyleTree Tree { get; }

    /// <summary>Runs the transitions and the <c>@keyframes</c> animations.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Rebuilt with the rest of the derived state, which is what makes a reload forget
    ///         what was in flight.</b> It holds the <see cref="Keyframes" /> table, and
    ///         <see cref="Build" /> replaces that — an animator kept across a reload would go on
    ///         interpolating against a <c>@keyframes</c> set the stylesheet no longer has.
    ///     </para>
    ///     <para>
    ///         It is here rather than on the document because it is the cascade's last tier: CSS
    ///         Cascading 5 § 6.2 puts a transitioning value above <c>!important</c>, and the thing
    ///         that owns the other tiers is the thing that should own this one. What the document adds
    ///         is a clock.
    ///     </para>
    /// </remarks>
    public Animator Animations { get; private set; }

    /// <summary>What <c>@media</c> is evaluated against for sheets that did not name a context.</summary>
    /// <remarks>
    ///     ⚠ <b>The default is a nought-by-nought surface, and it is a deliberate non-answer rather
    ///     than a sensible starting point.</b> An engine nobody has told about a window cannot know
    ///     how wide one is, and inventing a plausible width would make <c>@media (min-width: 768px)</c>
    ///     silently mean something. A host sets this through <c>UiDocument</c>, which reads it off the
    ///     surface it is already holding.
    /// </remarks>
    public MediaContext Media { get; private set; }

    /// <summary>Re-evaluates every <c>@media</c> block against a new surface.</summary>
    /// <param name="media">What the surface is now.</param>
    /// <returns>Whether any block changed its mind, and the sheets were therefore reloaded.</returns>
    /// <remarks>
    ///     <para>
    ///         <b><c>@media</c> is evaluated at load and not at match</b> — see
    ///         <see cref="StyleSheetLoader" /> — which is the trade that makes matching cheap and makes
    ///         this method necessary. A surface that changed size has to re-ask the questions, and the
    ///         only way to re-ask them is to load the sheets again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Guarded on the verdicts rather than on the context, and the difference is a window
    ///         drag.</b> A resize changes <see cref="MediaContext.Width" /> on every frame of it; a
    ///         reload re-parses every stylesheet with ExCSS, which for a generated utility sheet is
    ///         not a per-frame cost. So the conditions the loader actually saw are replayed against
    ///         the old context and the new one, and the reload happens only where one of them
    ///         disagrees. A document with no <c>@media</c> in it — which is every stylesheet this
    ///         repository ships — replays nothing and reloads never.
    ///     </para>
    ///     <para>
    ///         ⚠ Anything in flight is forgotten when it does reload, <see cref="Animations" />
    ///         included, because everything derived from the rules is rebuilt. That is the same rule
    ///         <see cref="Replace" /> follows and it is why the guard above is worth having twice
    ///         over: without it, resizing a window would restart every fade in it.
    ///     </para>
    /// </remarks>
    public bool SetMedia(MediaContext media) {
        var previous = Media;

        if (previous == media) {
            return false;
        }

        Media = media;

        if (!VerdictsDiffer(previous, media)) {
            return false;
        }

        Reload();
        return true;
    }

    /// <summary>Whether any condition the loader evaluated would answer differently.</summary>
    /// <remarks>
    ///     A condition it could not evaluate at all is counted as false for both contexts, so an
    ///     unsupported feature is a block that stays dropped rather than a reload on every resize.
    /// </remarks>
    bool VerdictsDiffer(MediaContext before, MediaContext after) {
        foreach (var condition in Loader.MediaConditions) {
            if (Holds(condition, before) != Holds(condition, after)) {
                return true;
            }
        }

        return false;
    }

    static bool Holds(string condition, MediaContext context) =>
        MediaQuery.TryEvaluate(condition, context, out var matches, out _) && matches;

    /// <summary>A transform every sheet's text goes through on its way to the parser.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The seam <c>@apply</c> needs, and the reason it is here rather than in front of
    ///         <see cref="Load" />.</b> A caller can already transform its own text before handing it
    ///         over; what it cannot do is be present for a <see cref="Reload" />, and a reload is not
    ///         a rare event — <see cref="SetMedia" /> triggers one, <see cref="Replace" /> triggers
    ///         one, and both replay <see cref="SheetText" />. A transform applied by the caller would
    ///         be applied once and then quietly dropped by the next resize.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What <see cref="sheets" /> keeps is the text as it was handed in, not the text the
    ///         parser saw.</b> That is what makes the transform re-runnable, and it is also what
    ///         <c>HotReloadHost</c> depends on: a failed reload puts a sheet back, and putting back
    ///         an already-expanded sheet would expand it twice the next time round.
    ///     </para>
    ///     <para>
    ///         Deliberately a <c>Func</c> rather than an interface. This assembly does not know what a
    ///         utility is — <c>Vixen.Ui.Styling.Utilities</c> sits above it — so the seam has to be
    ///         something that assembly can fill without this one naming its vocabulary.
    ///     </para>
    /// </remarks>
    public Func<string, string>? Preprocessor { get; set; }

    /// <summary>Loads a stylesheet.</summary>
    /// <param name="css">Its text.</param>
    /// <param name="origin">Who it came from.</param>
    /// <param name="media">
    ///     What to evaluate <c>@media</c> against, or <c>null</c> to follow <see cref="Media" /> — and
    ///     to keep following it, so that <see cref="SetMedia" /> reaches this sheet too.
    /// </param>
    /// <returns>The sheet's index, for <see cref="Replace" />.</returns>
    public int Load(string css, StyleOrigin origin = StyleOrigin.Author, MediaContext? media = null) {
        ArgumentNullException.ThrowIfNull(css);

        // ⚠ Added before the preprocessor runs, because a preprocessor that needs to know what the
        // whole document declares — which is exactly what `@apply` needs — reads `SheetText`, and a
        // sheet that is not in the list yet is a sheet whose own `@theme` it cannot see.
        sheets.Add(new(css, origin, media));
        Loader.Load(Preprocess(css), origin, media ?? Media);
        return sheets.Count - 1;
    }

    string Preprocess(string css) => Preprocessor is null ? css : Preprocessor(css);

    /// <summary>Replaces a loaded sheet with new text.</summary>
    /// <param name="sheet">The index <see cref="Load" /> returned.</param>
    /// <param name="css">The new text.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Everything reloads, not just the sheet that changed.</b> Rules are appended and
    ///         never removed — an index, a layer order and a declaration arena all assume that — so
    ///         a sheet cannot be lifted out of the middle of a set. Rebuilding from the texts is a
    ///         few milliseconds for a stylesheet a human just typed, and it is the difference
    ///         between a reload and an <i>overlay</i>: replaying the sheets is what makes a deleted
    ///         rule stop applying, where merely re-adding the new text leaves the old one underneath
    ///         still winning wherever the new one says nothing.
    ///     </para>
    ///     <para>
    ///         What survives is what elements hold handles to: the name tables the style tree
    ///         interned its tags and classes against, the inline-style store, and the tree itself.
    ///         What does not is the rule set and everything derived from it, the interning cache
    ///         included — a <see cref="ComputedStyle" /> from before the reload is a different
    ///         object from the identical one after it, which is why a caller has to forget what it
    ///         applied rather than compare against it.
    ///     </para>
    /// </remarks>
    public void Replace(int sheet, string css) {
        ArgumentNullException.ThrowIfNull(css);
        ArgumentOutOfRangeException.ThrowIfNegative(sheet);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sheet, sheets.Count);

        sheets[sheet] = sheets[sheet] with { Css = css };
        Reload();
    }

    /// <summary>The text a sheet was loaded from.</summary>
    /// <param name="sheet">The index <see cref="Load" /> returned.</param>
    /// <returns>Its text.</returns>
    /// <remarks>What a failed reload puts back, which is why the engine keeps it rather than the caller.</remarks>
    public string SheetText(int sheet) {
        ArgumentOutOfRangeException.ThrowIfNegative(sheet);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sheet, sheets.Count);

        return sheets[sheet].Css;
    }

    /// <summary>Rebuilds the rule set from the sheets as they now read.</summary>
    public void Reload() {
        Build();

        foreach (var sheet in sheets) {
            Loader.Load(Preprocess(sheet.Css), sheet.Origin, sheet.Media ?? Media);
        }
    }

    /// <summary>
    ///     Stands up the rule set and everything derived from it.
    /// </summary>
    /// <remarks>
    ///     <c>MemberNotNull</c> rather than field initialisers: the constructor and
    ///     <see cref="Reload" /> want the same eight objects built the same way, and duplicating
    ///     that so the compiler can see it is how the two drift apart.
    /// </remarks>
    [MemberNotNull(
        nameof(Selectors),
        nameof(Compiler),
        nameof(Rules),
        nameof(Matcher),
        nameof(Interning),
        nameof(Keyframes),
        nameof(Loader),
        nameof(Resolver),
        nameof(Animations)
    )]
    void Build() {
        Selectors = new SelectorTable();
        Compiler = new SelectorCompiler(Selectors, Names);
        Rules = new StyleRuleSet(Selectors, Names, Properties, Values);
        Matcher = new SelectorMatcher(Selectors);
        Interning = new ComputedStyleCache();
        Keyframes = new KeyframesTable();
        Loader = new StyleSheetLoader(Rules, Keyframes, Compiler);
        Resolver = new StyleResolver(Rules, InlineStyles, Matcher, Interning);

        // After `Keyframes`, which it holds — see the remarks on `Animations` for why it is rebuilt
        // here rather than kept. `Names` is the keyword table `StyleValueParser` interns identifiers
        // in, which is the same one `UiDocument` hands its own reader.
        Animations = new Animator(Properties, Values, Names, Keyframes);
    }

    /// <summary>Records declarations written on an element itself.</summary>
    /// <param name="declarations">The declarations, as they would be written in a rule body.</param>
    /// <returns>A handle to hand to <see cref="StyleResolver.Resolve" />.</returns>
    public InlineStyleId AddInlineStyle(ReadOnlySpan<Declaration> declarations) =>
        InlineStyles.Add(declarations);

    /// <summary>Resolves every element in the tree, parents before children.</summary>
    /// <returns>One computed style per element, indexed by <see cref="StyleNodeId.Index" />.</returns>
    /// <remarks>
    ///     Parents first is not an optimisation, it is a requirement: inheritance reads the parent's
    ///     <i>resolved</i> table, which is what keeps it one pass rather than a climb per property.
    ///     Elements are created parents-first, so ascending index already is that order.
    /// </remarks>
    public ComputedStyle[] ResolveAll() {
        // ⚠ **The sharing cache lives for one pass, and this is the pass.** Its key says what an
        // element *is* — parent, tag, id, classes, own state, inline style — and deliberately says
        // nothing about its ancestors beyond which one it hangs off. So an entry cached before a
        // parent gained `:hover` or `:checked` is stale, and serving it means `.card:hover .button`
        // and `checkbox:checked box` resolve to what they were before the state changed.
        //
        // `StyleUpdater` has always called this and this method never did, which is why the bug is
        // invisible from every test that goes through the updater and from every test that changes
        // an element's *own* state — that one is in the key, so the key changes and the entry misses.
        // It takes a rule whose subject is a descendant of the element that changed.
        Resolver.BeginPass();

        var styles = new ComputedStyle[Tree.Count];

        for (var i = 0; i < Tree.Count; i++) {
            // ⚠ A removed slot keeps its place so that the indices above it do not move — see
            // StyleTree.Remove — and resolves to nothing. Cascading it would be work for an element
            // no longer in the document, against a parent that has usually been removed with it.
            if (!Tree.IsAliveAt(i)) {
                styles[i] = ComputedStyle.Empty;
                continue;
            }

            var parent = Tree.ParentOf(i);
            styles[i] = Resolver.Resolve(
                Tree,
                new StyleNodeId(i),
                parent < 0 ? null : styles[parent],
                Tree.InlineAt(i)
            );
        }

        return styles;
    }
}
