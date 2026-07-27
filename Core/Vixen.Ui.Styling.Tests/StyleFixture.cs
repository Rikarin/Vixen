// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using ExCSS;
using Selector = Vixen.Ui.Styling.Selector;

namespace Vixen.Ui.Styling.Tests;

/// <summary>A parsed stylesheet, an element tree, and the pieces that match one against the other.</summary>
/// <remarks>
///     Assembled here rather than in each test because the interesting assertion is nearly always
///     "which rules matched", and getting there needs four objects that only make sense together.
/// </remarks>
sealed class StyleFixture {
    static readonly StylesheetParser Parser = new(true, true, true, true, true);

    public StyleFixture() {
        Names = new NameTable();
        Table = new SelectorTable();
        Compiler = new SelectorCompiler(Table, Names);
        Index = new RuleIndex(Table);
        Matcher = new SelectorMatcher(Table);
        Tree = new StyleTree(Names);
    }

    public NameTable Names { get; }

    public SelectorTable Table { get; }

    public SelectorCompiler Compiler { get; }

    public RuleIndex Index { get; }

    public SelectorMatcher Matcher { get; }

    public StyleTree Tree { get; }

    /// <summary>Compiles every selector in a stylesheet and indexes the rules, in source order.</summary>
    /// <param name="css">The stylesheet.</param>
    /// <returns>The compiled selectors, one per comma-separated part.</returns>
    public List<Selector> Load(string css) {
        var sheet = Parser.Parse(css);
        var compiled = new List<Selector>();

        foreach (var rule in sheet.Children.OfType<StyleRule>()) {
            Compiler.Compile(rule.Selector, compiled);
        }

        foreach (var selector in compiled) {
            Index.Add(selector);
        }

        return compiled;
    }

    /// <summary>Compiles one selector and returns it, asserting it compiled.</summary>
    /// <param name="selectorText">The selector.</param>
    /// <returns>The compiled selector.</returns>
    public Selector Compile(string selectorText) {
        var compiled = new List<Selector>();
        var sheet = Parser.Parse(selectorText + " { color: red }");
        Compiler.Compile(((StyleRule) sheet.Children.First()).Selector, compiled);
        return compiled.Single();
    }

    /// <summary>Whether a selector matches an element, through the indexed path.</summary>
    /// <param name="selectorText">The selector.</param>
    /// <param name="element">The element.</param>
    /// <returns>Whether it matches.</returns>
    public bool Matches(string selectorText, StyleNodeId element) =>
        Matcher.Matches(Tree, element, Compile(selectorText));

    /// <summary>The rules that match an element, found through the bucketed index and the bloom.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The matching rule indices, ascending.</returns>
    public List<int> MatchIndexed(StyleNodeId element) {
        var candidates = new List<int>();
        Index.Collect(Tree, element, candidates);

        var matched = new List<int>();
        foreach (var rule in candidates) {
            if (Matcher.Matches(Tree, element, Index.SelectorOf(rule))) {
                matched.Add(rule);
            }
        }

        return matched;
    }

    /// <summary>The rules that match an element, found by testing every rule with no bloom.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The matching rule indices, ascending.</returns>
    /// <remarks>
    ///     The oracle. It shares the matcher — what is being checked is that bucketing and the bloom
    ///     filter are pure optimisations, which is precisely the claim that would otherwise be an
    ///     argument rather than a fact.
    /// </remarks>
    public List<int> MatchBruteForce(StyleNodeId element) {
        var matched = new List<int>();
        for (var rule = 0; rule < Index.Count; rule++) {
            if (Matcher.Matches(Tree, element, Index.SelectorOf(rule), useBloom: false)) {
                matched.Add(rule);
            }
        }

        return matched;
    }
}
