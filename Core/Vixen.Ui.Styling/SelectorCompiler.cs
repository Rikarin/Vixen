// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using ExCSS;
using Combinator = Vixen.Ui.Styling.Combinator;
using Selector = Vixen.Ui.Styling.Selector;

namespace Vixen.Ui.Styling;

/// <summary>Why a selector could not be compiled.</summary>
/// <param name="Text">The selector as it was written.</param>
/// <param name="Reason">What stopped it.</param>
public readonly record struct SelectorDiagnostic(string Text, string Reason);

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
    readonly NameTable names = names ?? throw new ArgumentNullException(nameof(names));
    readonly SelectorTable table = table ?? throw new ArgumentNullException(nameof(table));
    readonly List<SelectorDiagnostic> diagnostics = [];

    /// <summary>Everything that could not be compiled, and why.</summary>
    public IReadOnlyList<SelectorDiagnostic> Diagnostics => diagnostics;

    /// <summary>Compiles one ExCSS selector, splitting a list into its parts.</summary>
    /// <param name="selector">What ExCSS parsed.</param>
    /// <param name="compiled">Receives one entry per comma-separated selector.</param>
    public void Compile(ISelector selector, List<Selector> compiled) {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(compiled);

        if (selector is ListSelector list) {
            foreach (var child in list) {
                Compile(child, compiled);
            }

            return;
        }

        if (TryCompileOne(selector, out var result)) {
            compiled.Add(result);
        }
    }

    bool TryCompileOne(ISelector selector, out Selector compiled) {
        // Compounds are buffered and written in one run at the end. A `:not()` or `:is()` inside
        // this selector compiles *its* compounds into the same table on the way past, and a range
        // reserved up front would have those nested compounds land in the middle of it. Four tests
        // disagreed about that before the buffering was here.
        var buffered = new List<CompoundSelector>();
        var specificity = new Specificity();
        var pseudoElement = NameTable.None;
        var count = 0;

        // ExCSS records a combinator alongside the compound it *follows*, and the last one carries
        // an empty delimiter. Vixen records it on the compound it *precedes*, because the matcher
        // walks right to left and wants to know how to get from here to the previous one.
        var pending = Combinator.None;

        foreach (var (part, delimiter) in Parts(selector)) {
            if (!TryCompileCompound(part, pending, ref specificity, ref pseudoElement, out var compound)) {
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
                diagnostics.Add(new SelectorDiagnostic(selector.Text, $"the combinator '{delimiter}' is not supported"));
                compiled = default;
                return false;
            }

            pending = next;
        }

        if (count == 0) {
            diagnostics.Add(new SelectorDiagnostic(selector.Text, "the selector is empty"));
            compiled = default;
            return false;
        }

        var start = table.CompoundCount;
        foreach (var compound in buffered) {
            table.AddCompound(compound);
        }

        compiled = new Selector(start, count, specificity, pseudoElement);
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
        ref int pseudoElement,
        out CompoundSelector compound
    ) {
        compound = default;

        // Buffered for the same reason the compounds are: a nested selector writes its own simples
        // into this table while this compound is still being built.
        var buffered = new List<SimpleSelector>();

        foreach (var simple in Flatten(part)) {
            if (!TryCompileSimple(simple, ref specificity, ref pseudoElement, out var compiled)) {
                return false;
            }

            // A pseudo-element is not a test on the element; it says which box the rule targets, and
            // it has already been recorded on the way past.
            if (compiled is not null) {
                buffered.Add(compiled.Value);
            }
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

    bool TryCompileSimple(ISelector selector, ref Specificity specificity, ref int pseudoElement, out SimpleSelector? compiled) {
        compiled = null;

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
                compiled = new SimpleSelector(SimpleSelectorKind.Attribute, names.Intern(attribute.Attribute));
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

            case PseudoElementSelector element:
                specificity = specificity with { Types = specificity.Types + 1 };
                pseudoElement = names.Intern(element.Name);
                return true;

            case PseudoClassSelector pseudo:
                return TryCompilePseudoClass(pseudo, ref specificity, out compiled);

            default:
                diagnostics.Add(new SelectorDiagnostic(selector.Text, $"'{selector.Text}' is not a selector Vixen supports"));
                return false;
        }

        SimpleSelector Attribute(string attribute, string value, AttributeOperator op) =>
            new(SimpleSelectorKind.Attribute, names.Intern(attribute), names.Intern(value), op);
    }

    bool TryCompilePseudoClass(PseudoClassSelector pseudo, ref Specificity specificity, out SimpleSelector? compiled) {
        compiled = null;
        var name = Trim(pseudo.Text, ':');

        var state = name switch {
            "hover" => ElementState.Hover,
            "active" => ElementState.Active,
            "focus" => ElementState.Focus,
            "focus-visible" => ElementState.FocusVisible,
            "focus-within" => ElementState.FocusWithin,
            "disabled" => ElementState.Disabled,
            "checked" => ElementState.Checked,
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
            _ => (PositionTest?) null
        };

        if (position is not null) {
            specificity = specificity with { Classes = specificity.Classes + 1 };
            compiled = new SimpleSelector(SimpleSelectorKind.Position, Position: position.Value);
            return true;
        }

        if (name == "enabled") {
            // The absence of a state rather than a state of its own, which is what CSS means by it.
            specificity = specificity with { Classes = specificity.Classes + 1 };
            var nested = table.AddNested(NegatedState(ElementState.Disabled));
            compiled = new SimpleSelector(SimpleSelectorKind.Not, NestedStart: nested, NestedCount: 1);
            return true;
        }

        diagnostics.Add(new SelectorDiagnostic(pseudo.Text, $":{name} is not supported"));
        return false;
    }

    Selector NegatedState(ElementState state) {
        var simpleStart = table.SimpleCount;
        table.AddSimple(new SimpleSelector(SimpleSelectorKind.State, State: state));
        var compoundStart = table.CompoundCount;
        table.AddCompound(new CompoundSelector(Combinator.None, simpleStart, 1));
        return new Selector(compoundStart, 1, new Specificity(0, 1, 0), NameTable.None);
    }

    bool TryCompileNested(ISelector inner, SimpleSelectorKind kind, string text, out SimpleSelector? compiled) {
        compiled = null;

        var parts = new List<Selector>();
        var before = diagnostics.Count;
        Compile(inner, parts);

        if (parts.Count == 0 || diagnostics.Count != before) {
            diagnostics.Add(new SelectorDiagnostic(text, "its argument could not be compiled"));
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
