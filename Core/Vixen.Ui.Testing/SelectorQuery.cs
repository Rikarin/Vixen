// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using ExCSS;
using Vixen.Ui.Styling;
using Selector = Vixen.Ui.Styling.Selector;

namespace Vixen.Ui.Testing;

/// <summary>A selector string, compiled once and run against the tree whenever it is asked.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The cascade's own compiler and matcher, not a second implementation.</b> This is the
///         decision the whole query side rests on. A test framework that wrote its own selector
///         engine would agree with the stylesheets on <c>.panel button</c> and disagree on
///         <c>:nth-child(2n+1)</c>, <c>:not(.a, .b)</c> and the descendant-versus-child distinction
///         inside <c>:has()</c> — and it would disagree silently, so a test would pass against a
///         selector that styles nothing.
///     </para>
///     <para>
///         What that costs is ExCSS and a friend reference, and both are already paid: ExCSS is in
///         <c>Vixen.Ui.Styling</c>'s public surface because <see cref="SelectorCompiler.Compile" />
///         takes an <see cref="ISelector" />.
///     </para>
///     <para>
///         ⚠ The bloom is left on, which is the matcher's default and what the cascade uses. It is
///         maintained by the tree rather than rebuilt by the resolve pass, and it is allowed to be
///         conservative — a class removed from an ancestor stays in its descendants' summaries — so
///         it produces extra candidates and never a missed match. Turning it off "to be safe" would
///         mean the test's matching path was not the cascade's, which is the one thing this type
///         exists to guarantee.
///     </para>
/// </remarks>
sealed class SelectorQuery {
    // The same five flags Vixen.Ui.Styling's own loader constructs its parser with. A parser
    // configured differently would accept a selector the stylesheets reject, or the reverse.
    static readonly StylesheetParser Parser = new(true, true, true, true, true);

    readonly List<Selector> compiled;
    readonly string text;

    SelectorQuery(string text, List<Selector> compiled) {
        this.text = text;
        this.compiled = compiled;
    }

    /// <summary>Compiles a selector against a document's name and selector tables.</summary>
    /// <param name="document">Whose tables to compile into.</param>
    /// <param name="selector">The selector text.</param>
    /// <exception cref="UiTestException">It is not a selector this engine understands.</exception>
    /// <remarks>
    ///     ⚠ Compiled into the <i>document's</i> tables rather than into private ones. Names are
    ///     interned to integers and a matcher compares the integers, so a selector compiled against a
    ///     second table would be comparing this document's ids with another table's numbering and
    ///     would match nothing, or — worse — something.
    /// </remarks>
    public static SelectorQuery Compile(UiDocument document, string selector) {
        var compiled = new List<Selector>();
        var before = document.Styles.Compiler.Diagnostics.Count;

        Stylesheet sheet;

        try {
            // A selector is not a stylesheet, and the compiler takes what a stylesheet holds. Giving
            // it a body of one declaration is what turns the one into the other; the declaration is
            // never read.
            sheet = Parser.Parse(selector + " { color: red }");
        } catch (Exception cause) {
            throw new UiTestException($"\"{selector}\" is not a selector: {cause.Message}", cause);
        }

        if (sheet.Children.FirstOrDefault() is not IStyleRule rule) {
            throw new UiTestException(
                $"\"{selector}\" is not a selector. It parsed, but not into a rule with one — an "
                + "at-rule or a stray brace will do this."
            );
        }

        document.Styles.Compiler.Compile(rule.Selector, compiled);

        if (compiled.Count == 0) {
            // ⚠ The compiler drops what it does not support with a diagnostic rather than throwing,
            // which is right for a stylesheet — one bad rule should not take the sheet down — and
            // wrong here. A test whose selector silently matched nothing would report "expected 1,
            // found 0" and send somebody looking at the interface for an element that was never
            // being asked for.
            var reasons = document.Styles.Compiler.Diagnostics
                .Skip(before)
                .Select(diagnostic => $"{diagnostic.Text}: {diagnostic.Reason}")
                .ToArray();

            throw new UiTestException(
                $"\"{selector}\" compiled to nothing, so it can never match."
                + (reasons.Length > 0 ? Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", reasons) : string.Empty)
            );
        }

        return new(selector, compiled);
    }

    /// <summary>Everything at or under a scope that this matches, in document order.</summary>
    /// <param name="document">The document to match in.</param>
    /// <param name="scope">The subtree to look in. Matching is still done against the whole tree.</param>
    /// <remarks>
    ///     ⚠ <b>The scope narrows the candidates, not the matching.</b> <c>Find("span")</c> inside a
    ///     panel returns the spans under that panel, but <c>.card span</c> evaluated there still asks
    ///     the real ancestors whether any is a card — which is what the DOM does, and what makes a
    ///     scoped query composable with a selector that reaches upwards.
    /// </remarks>
    public List<UiElement> Match(UiDocument document, UiElement scope) {
        var matched = new List<UiElement>();

        foreach (var element in UiTest.Descendants(scope)) {
            if (Matches(document, element)) {
                matched.Add(element);
            }
        }

        return matched;
    }

    /// <summary>Whether one element matches.</summary>
    /// <param name="document">The document it is in.</param>
    /// <param name="element">The element.</param>
    /// <returns>Whether any of the comma-separated parts matches it.</returns>
    /// <remarks>
    ///     Separate from <see cref="Match" /> because asking about one element is the common case —
    ///     <c>Filter</c> and <c>Closest</c> both do it — and answering it by matching a whole subtree
    ///     and looking for the element in the result is quadratic for no reason.
    /// </remarks>
    public bool Matches(UiDocument document, UiElement element) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(element);

        // A removed element still holds the id it was created with, and that id now addresses a slot
        // the tree has tombstoned. Matching against it would be asking about a stranger.
        if (element.IsRemoved || !document.Styles.Tree.IsAlive(element.StyleNode)) {
            return false;
        }

        foreach (var selector in compiled) {
            if (document.Styles.Matcher.Matches(document.Styles.Tree, element.StyleNode, selector)) {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public override string ToString() => text;
}
