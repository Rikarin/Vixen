// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Testing;

/// <summary>Some elements a test is talking about, and everything it can do to them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A recipe, not a snapshot.</b> A subject holds the <i>question</i> — "everything
///         matching <c>.toast</c>" — and asks it again every time it is used. That single decision is
///         what makes waiting work: <c>Get(".toast").ShouldExist()</c> runs frames and re-asks, so an
///         element the game creates three frames later is found. A subject holding the elements it
///         matched when it was built could only ever assert about the past, and every test would need
///         a hand-written loop in front of it.
///     </para>
///     <para>
///         It also means a subject survives its elements. A list re-rendered between two commands
///         hands the second command the new elements rather than a fistful of removed ones, which is
///         exactly the "element is detached from the DOM" failure that Cypress has to warn about and
///         this cannot produce.
///     </para>
///     <para>
///         The cost is that resolution is not free and is paid per command. A selector over a
///         thousand elements is a thousand matcher calls, once per assertion — which is the same
///         order as one style pass, in a harness that has already agreed to run frames.
///     </para>
/// </remarks>
public sealed partial class UiSubject {
    readonly Func<List<UiElement>> resolve;

    UiSubject(UiTest test, string description, Func<List<UiElement>> resolve) {
        Test = test;
        Description = description;
        this.resolve = resolve;
    }

    /// <summary>The harness this came from.</summary>
    public UiTest Test { get; }

    /// <summary>How this subject reads in a log line or a failure.</summary>
    public string Description { get; }

    /// <summary>What matches right now.</summary>
    /// <remarks>
    ///     ⚠ Re-runs the query on every read and does <b>not</b> wait. It is the escape hatch for a
    ///     test that wants the elements after it has asserted something about them; the assertions
    ///     are what wait.
    /// </remarks>
    public IReadOnlyList<UiElement> Elements => resolve();

    /// <summary>How many match right now.</summary>
    public int Count => resolve().Count;

    /// <summary>The one element that matches.</summary>
    /// <exception cref="UiTestException">There is not exactly one.</exception>
    /// <remarks>Does not wait, for the reason <see cref="Elements" /> does not.</remarks>
    public UiElement Element {
        get {
            var matched = resolve();

            if (matched.Count != 1) {
                throw Fail($"{Description} is one element", Describe(matched), 0);
            }

            return matched[0];
        }
    }

    /// <summary>A subject over elements already in hand.</summary>
    internal static UiSubject Of(UiTest test, string description, List<UiElement> elements) =>
        new(test, description, () => elements);

    /// <summary>A subject over a question that gets asked again each time.</summary>
    internal static UiSubject Deferred(UiTest test, string description, Func<List<UiElement>> resolve) =>
        new(test, description, resolve);

    /// <summary>Everything under these that matches a selector.</summary>
    /// <param name="selector">The selector.</param>
    /// <returns>A subject over the matches.</returns>
    /// <remarks>Excludes the subject's own elements, as the DOM's <c>querySelectorAll</c> does.</remarks>
    public UiSubject Find(string selector) {
        ArgumentNullException.ThrowIfNull(selector);

        var query = SelectorQuery.Compile(Test.Document, selector);

        return Derive($"find \"{selector}\"", elements => {
            var matched = new List<UiElement>();

            foreach (var element in elements) {
                foreach (var found in query.Match(Test.Document, element)) {
                    // The scope itself is a candidate of its own subtree walk, and `find` means
                    // strictly below. Without this, `Get(".row").Find(".row")` returns the rows.
                    if (!ReferenceEquals(found, element) && !matched.Contains(found)) {
                        matched.Add(found);
                    }
                }
            }

            return matched;
        });
    }

    /// <summary>Only the ones that also match a selector.</summary>
    /// <param name="selector">The selector.</param>
    /// <returns>A subject over those.</returns>
    public UiSubject Filter(string selector) {
        ArgumentNullException.ThrowIfNull(selector);

        var query = SelectorQuery.Compile(Test.Document, selector);

        return Derive(
            $"filter \"{selector}\"",
            elements => elements.Where(element => query.Matches(Test.Document, element)).ToList()
        );
    }

    /// <summary>Only the ones whose text contains this.</summary>
    /// <param name="text">What to look for.</param>
    /// <returns>A subject over those.</returns>
    public UiSubject Contains(string text) {
        ArgumentNullException.ThrowIfNull(text);

        return Derive(
            $"contains \"{text}\"",
            elements => elements
                .Where(element => element.Text is { } value && value.Contains(text, StringComparison.Ordinal))
                .ToList()
        );
    }

    /// <summary>Only the ones a predicate likes.</summary>
    /// <param name="predicate">What to keep.</param>
    /// <param name="description">How it reads in a log line.</param>
    /// <returns>A subject over those.</returns>
    /// <remarks>
    ///     The extension point, so that a control with a notion of "selected" that no selector
    ///     reaches can still be waited on without this assembly growing a member for it.
    /// </remarks>
    public UiSubject Where(Func<UiElement, bool> predicate, string description) {
        ArgumentNullException.ThrowIfNull(predicate);

        return Derive($"where {description}", elements => elements.Where(predicate).ToList());
    }

    /// <summary>The first one.</summary>
    /// <returns>A subject over it.</returns>
    public UiSubject First() => Derive("first", elements => elements.Take(1).ToList());

    /// <summary>The last one.</summary>
    /// <returns>A subject over it.</returns>
    public UiSubject Last() => Derive("last", elements => elements.TakeLast(1).ToList());

    /// <summary>The one at an index.</summary>
    /// <param name="index">Which, counting from zero.</param>
    /// <returns>A subject over it, empty when there are fewer than that.</returns>
    public UiSubject Nth(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return Derive($"nth({index})", elements => elements.Skip(index).Take(1).ToList());
    }

    /// <summary>Their parents.</summary>
    /// <returns>A subject over them, without repeats.</returns>
    public UiSubject Parent() =>
        Derive("parent", elements => Distinct(elements.Select(element => element.Parent)));

    /// <summary>Their immediate children.</summary>
    /// <returns>A subject over them.</returns>
    public UiSubject Children() =>
        Derive("children", elements => elements.SelectMany(element => element.Children).ToList());

    /// <summary>The nearest ancestor of each — itself included — that matches a selector.</summary>
    /// <param name="selector">The selector.</param>
    /// <returns>A subject over them, without repeats.</returns>
    public UiSubject Closest(string selector) {
        ArgumentNullException.ThrowIfNull(selector);

        var query = SelectorQuery.Compile(Test.Document, selector);

        return Derive($"closest \"{selector}\"", elements => Distinct(elements.Select(element => {
            for (var candidate = element; candidate is not null; candidate = candidate.Parent) {
                if (query.Matches(Test.Document, candidate)) {
                    return candidate;
                }
            }

            return null;
        })));
    }

    /// <summary>Waits for exactly one element, then hands it over.</summary>
    /// <param name="action">What to do with it.</param>
    /// <returns>This, so it reads as part of a chain.</returns>
    /// <remarks>
    ///     The bridge to ordinary code. Everything this assembly does not have a member for — reading
    ///     a control's own property, calling a method on it — happens in here, after the waiting.
    /// </remarks>
    public UiSubject Then(Action<UiElement> action) {
        ArgumentNullException.ThrowIfNull(action);

        action(Await("then", Exactly(1))[0]);
        return this;
    }

    /// <inheritdoc />
    public override string ToString() => Description;

    /// <summary>Runs frames until a condition holds, and fails saying what it saw instead.</summary>
    /// <param name="command">What is being waited for, as it reads in the log.</param>
    /// <param name="condition">
    ///     What to check. Returns <c>null</c> when satisfied, and otherwise what it found — which
    ///     goes straight into the failure message, so it should read as an answer to the command.
    /// </param>
    /// <returns>What matched when the condition was satisfied.</returns>
    /// <remarks>
    ///     <para>
    ///         The whole retry engine, and it is deliberately this small. Every assertion and every
    ///         action in this assembly is a condition handed to this method, which means there is one
    ///         place that decides what waiting means, one place that counts frames, and one place
    ///         that builds a failure.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The condition is checked before the first frame runs, not after.</b> A test that
    ///         asserts on an interface already in the right state must not cost a frame, or every
    ///         assertion would advance the clock and a suite's gesture timings would depend on how
    ///         many things it asserted.
    ///     </para>
    /// </remarks>
    internal List<UiElement> Await(string command, Func<List<UiElement>, string?> condition) {
        var index = Test.Log.Begin($"{Description} · {command}");
        var frames = 0;

        while (true) {
            var matched = resolve();
            var found = condition(matched);

            if (found is null) {
                Test.Log.Complete(index, Describe(matched), frames);
                return matched;
            }

            if (frames >= Test.Options.RetryFrames) {
                Test.Log.Complete(index, $"failed — {found}", frames);
                throw Fail($"{Description} · {command}", found, frames);
            }

            Test.Frame();
            frames++;
        }
    }

    /// <summary>A condition that wants a particular number of elements.</summary>
    internal Func<List<UiElement>, string?> Exactly(int count) =>
        matched => matched.Count == count ? null : Describe(matched);

    /// <summary>How a set of elements reads in a log line or a failure.</summary>
    internal string Describe(List<UiElement> matched) =>
        matched.Count switch {
            0 => "no elements",
            1 => Test.Describe(matched[0]),
            <= 4 => $"{matched.Count} elements: {string.Join(", ", matched.Select(Test.Describe))}",
            _ => $"{matched.Count} elements: {string.Join(", ", matched.Take(4).Select(Test.Describe))}, …"
        };

    UiSubject Derive(string step, Func<List<UiElement>, List<UiElement>> transform) =>
        new(Test, $"{Description} · {step}", () => transform(resolve()));

    UiTestException Fail(string what, string found, int frames) =>
        UiTestException.Build(what, found, frames, Test.Log, Test.Tree());

    static List<UiElement> Distinct(IEnumerable<UiElement?> elements) {
        var result = new List<UiElement>();

        foreach (var element in elements) {
            if (element is not null && !result.Contains(element)) {
                result.Add(element);
            }
        }

        return result;
    }
}
