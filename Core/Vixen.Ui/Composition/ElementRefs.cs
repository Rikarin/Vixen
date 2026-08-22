// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Vixen.Ui.Composition;

/// <summary>
///     What an <c>@for</c> row's <c>refs</c> assigns into: one element per live iteration, found by
///     the same key the loop is reconciled on.
/// </summary>
/// <typeparam name="TElement">What the tag makes. A <see cref="UiElement" /> or a subclass of one.</typeparam>
/// <remarks>
///     <para>
///         ⚠ <b>Why <c>ref</c> could not be made to do this.</b> A <c>ref</c> is one assignment to
///         one member and a loop has many rows, so the member would hold whichever row was built
///         last — and, because <c>BuildContext.For</c> reuses a surviving key's region and
///         <i>does not re-run its body</i>, whichever row was built last <i>the first time the
///         sequence contained it</i>. That is <c>VXML2010</c>. A member holding a <c>List&lt;T&gt;</c>
///         has the same defect: the body appends once per key ever, so a reordered or filtered
///         sequence leaves the list in an order nothing corresponds to, silently.
///     </para>
///     <para>
///         ⚠ <b>So the handle is keyed on the iteration rather than filled by the body.</b> The key
///         is the one <c>@for</c> already reconciles on, taken from the loop rather than recomputed
///         here, and the entry is registered against the row's region — so a row that leaves the
///         sequence takes its entry with it, and a row that survives keeps the element it has always
///         had whatever its position becomes. Reordering cannot hand back the wrong control because
///         position is not what is being asked.
///     </para>
///     <para>
///         ⚠ <b>Enumeration is deliberately not offered.</b> The order of the rows is the order of
///         the sequence the panel already holds, and a second answer to that question is a second
///         answer to get wrong. Iterate the model and look each row up.
///     </para>
///     <para>
///         <b>The frame rule, and why the indexer throws.</b> An <c>@for</c> body builds inside an
///         effect, so the entries appear when the document's effects are next flushed and not on the
///         line that changed the sequence. A lookup that answered <c>null</c> there would be a wrong
///         answer to a right question, arriving as a <see cref="NullReferenceException" /> some
///         distance away; <see cref="this[object]" /> says what happened instead. Code that expects a
///         key to be absent asks <see cref="TryGet" />.
///     </para>
/// </remarks>
public sealed class ElementRefs<TElement> where TElement : UiElement {
    readonly Dictionary<object, TElement> live = [];

    /// <summary>How many rows are registered.</summary>
    public int Count => live.Count;

    /// <summary>The element registered for one iteration's key.</summary>
    /// <param name="key">The key, as written in the loop's <c>key</c> attribute.</param>
    /// <exception cref="KeyNotFoundException">Nothing is registered for it.</exception>
    public TElement this[object key] {
        get {
            ArgumentNullException.ThrowIfNull(key);

            return live.TryGetValue(key, out var element)
                ? element
                : throw new KeyNotFoundException(Missing(key));
        }
    }

    /// <summary>The same, for a caller to whom an absent key is an answer rather than a fault.</summary>
    /// <param name="key">The key.</param>
    /// <param name="element">What was registered for it, if anything.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryGet(object key, [NotNullWhen(true)] out TElement? element) {
        ArgumentNullException.ThrowIfNull(key);
        return live.TryGetValue(key, out element);
    }

    /// <summary>Whether a key has an element.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Whether it does.</returns>
    public bool Contains(object key) {
        ArgumentNullException.ThrowIfNull(key);
        return live.ContainsKey(key);
    }

    internal void Add(object key, TElement element) => live[key] = element;

    /// <summary>Takes an entry out, if it is still the one that was put in.</summary>
    /// <remarks>
    ///     ⚠ <b>Conditional, because a region is cleared after its replacement is built.</b> Two rows
    ///     with the same key are a mistake the reconciler does not police, and an unconditional
    ///     removal would let the loser's teardown delete the winner's entry — a handle that is empty
    ///     for exactly one key, which is the hardest possible shape of this bug to see.
    /// </remarks>
    internal void Remove(object key, TElement element) {
        if (live.TryGetValue(key, out var current) && ReferenceEquals(current, element)) {
            live.Remove(key);
        }
    }

    string Missing(object key) =>
        live.Count == 0
            ? $"No element is registered for '{key}', and none is registered at all. An @for body "
            + "builds inside an effect, so a 'refs' handle is empty until the document's effects "
            + "are flushed — advance a frame, or call Document.Effects.Flush(), after changing the "
            + "sequence."
            : $"No element is registered for '{key}'. {live.Count.ToString(CultureInfo.InvariantCulture)} "
            + "are, so the @for has run: the key asked for is not one the loop produced. A 'refs' "
            + "handle is keyed on the loop's own key, which is the item itself when the loop "
            + "declares no 'key' attribute.";
}
