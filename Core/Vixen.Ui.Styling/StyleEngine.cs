// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
    /// <summary>Creates an engine with nothing loaded.</summary>
    public StyleEngine() {
        Names = new NameTable();
        Properties = new NameTable();
        Values = new NameTable();
        Selectors = new SelectorTable();
        Compiler = new SelectorCompiler(Selectors, Names);
        Rules = new StyleRuleSet(Selectors, Names, Properties, Values);
        Matcher = new SelectorMatcher(Selectors);
        Interning = new ComputedStyleCache();
        InlineStyles = new InlineStyleStore();
        Keyframes = new KeyframesTable();
        Loader = new StyleSheetLoader(Rules, Keyframes, Compiler);
        Resolver = new StyleResolver(Rules, InlineStyles, Matcher, Interning);
        Tree = new StyleTree(Names);
    }

    /// <summary>The table selector names are interned in.</summary>
    public NameTable Names { get; }

    /// <summary>The table property names are interned in.</summary>
    public NameTable Properties { get; }

    /// <summary>The table declaration values are interned in.</summary>
    public NameTable Values { get; }

    /// <summary>The flat store compiled selectors point into.</summary>
    public SelectorTable Selectors { get; }

    /// <summary>The ExCSS selector visitor.</summary>
    public SelectorCompiler Compiler { get; }

    /// <summary>The loaded rules.</summary>
    public StyleRuleSet Rules { get; }

    /// <summary>The selector matcher.</summary>
    public SelectorMatcher Matcher { get; }

    /// <summary>Where computed styles are interned.</summary>
    public ComputedStyleCache Interning { get; }

    /// <summary>The declarations written on elements themselves.</summary>
    public InlineStyleStore InlineStyles { get; }

    /// <summary>The <c>@keyframes</c> rules.</summary>
    public KeyframesTable Keyframes { get; }

    /// <summary>The stylesheet loader.</summary>
    public StyleSheetLoader Loader { get; }

    /// <summary>The cascade.</summary>
    public StyleResolver Resolver { get; }

    /// <summary>The element store.</summary>
    public StyleTree Tree { get; }

    /// <summary>Loads a stylesheet.</summary>
    /// <param name="css">Its text.</param>
    /// <param name="origin">Who it came from.</param>
    /// <param name="media">What to evaluate <c>@media</c> against.</param>
    public void Load(string css, StyleOrigin origin = StyleOrigin.Author, MediaContext media = default) =>
        Loader.Load(css, origin, media);

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
            var parent = Tree.ParentOf(i);
            styles[i] = Resolver.Resolve(Tree, new StyleNodeId(i), parent < 0 ? null : styles[parent]);
        }

        return styles;
    }
}
