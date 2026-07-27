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
    readonly record struct Sheet(string Css, StyleOrigin Origin, MediaContext Media);

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

    /// <summary>Loads a stylesheet.</summary>
    /// <param name="css">Its text.</param>
    /// <param name="origin">Who it came from.</param>
    /// <param name="media">What to evaluate <c>@media</c> against.</param>
    /// <returns>The sheet's index, for <see cref="Replace" />.</returns>
    public int Load(string css, StyleOrigin origin = StyleOrigin.Author, MediaContext media = default) {
        ArgumentNullException.ThrowIfNull(css);

        sheets.Add(new(css, origin, media));
        Loader.Load(css, origin, media);
        return sheets.Count - 1;
    }

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
            Loader.Load(sheet.Css, sheet.Origin, sheet.Media);
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
        nameof(Resolver)
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
            styles[i] = Resolver.Resolve(Tree, new StyleNodeId(i), parent < 0 ? null : styles[parent]);
        }

        return styles;
    }
}
