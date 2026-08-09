// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui;

public partial class UiElement {
    List<Declaration>? inline;
    InlineStyleId? inlineId;

    /// <summary>Writes a declaration onto this element, over anything a stylesheet said.</summary>
    /// <param name="property">The property, as a stylesheet writes it — <c>width</c>, <c>flex-grow</c>.</param>
    /// <param name="value">Its value, or <c>null</c> to take the declaration away again.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The escape hatch for lengths no stylesheet was given.</b> A splitter at 37%, a
    ///         docked panel the user has dragged to 240 pixels, a row positioned by a virtualiser:
    ///         all of them are numbers that exist only at run time, and none of them can be a rule.
    ///         Everything a rule <i>can</i> say should still be said in one — this is deliberately
    ///         awkward enough to be a last resort, and <see cref="UiElement.OffsetX" /> is the
    ///         cheaper answer whenever moving a box is enough.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It beats every rule, including one marked <c>!important</c> in a user
    ///         stylesheet.</b> That is what inline means in the cascade, and it is why a control
    ///         should write the fewest properties it can: a control that writes <c>height</c>
    ///         because it wanted to write <c>flex-basis</c> has taken the height away from every
    ///         theme that will ever style it.
    ///     </para>
    ///     <para>
    ///         <b>Values are re-interned and the block is rewritten in place</b> when the set of
    ///         properties has not changed, which is the case that repeats — a drag writes one
    ///         property sixty times a second. Only adding or removing a property allocates.
    ///     </para>
    /// </remarks>
    public void SetStyle(string property, string? value) => SetStyle(property, value, false);

    /// <summary>The same, for a declaration that carried <c>!important</c>.</summary>
    /// <param name="property">The property.</param>
    /// <param name="value">Its value, or <c>null</c> to take the declaration away again.</param>
    /// <param name="important">Whether it outranks an <c>!important</c> rule as well as a plain one.</param>
    /// <remarks>
    ///     ⚠ <b>An overload rather than an optional parameter, so that no existing call site changes
    ///     meaning.</b> The flag exists because <c>style="…"</c> in markup can carry an
    ///     <c>!important</c> and the cascade already has the tier for it —
    ///     <c>CascadeRanks.ImportantInline</c> — so parsing one and then dropping it would be the
    ///     silent wrong answer this file's neighbours keep getting caught by. Nothing in the engine
    ///     passes <see langword="true" /> of its own accord: a control writing this is overruling
    ///     every theme that will ever style it, twice over.
    /// </remarks>
    public void SetStyle(string property, string? value, bool important) {
        ArgumentNullException.ThrowIfNull(property);

        var styles = Document.Styles;
        var id = styles.Properties.Intern(property);

        inline ??= [];

        var index = inline.FindIndex(declaration => declaration.Property == id);

        if (value is null) {
            if (index < 0) {
                return;
            }

            inline.RemoveAt(index);
        } else {
            var declaration = new Declaration(id, styles.Values.Intern(value), important);

            if (index >= 0) {
                if (inline[index] == declaration) {
                    // ⚠ The early return is what makes assigning the same value free. A splitter
                    // that has not moved still writes its width every frame, and without this every
                    // one of those frames would invalidate the document and re-cascade the subtree.
                    return;
                }

                inline[index] = declaration;
            } else {
                inline.Add(declaration);
            }
        }

        Commit();
    }

    /// <summary>Reads back a declaration written with <see cref="SetStyle(string, string?)" />.</summary>
    /// <param name="property">The property.</param>
    /// <returns>Its value, or <c>null</c> if this element has none of its own.</returns>
    /// <remarks>
    ///     ⚠ <b>What this element declared, not what it computed.</b> An element with no inline
    ///     <c>width</c> answers <c>null</c> however wide a stylesheet made it — the computed value
    ///     is <see cref="UiElement.Style" />'s to answer, and conflating the two is how a caller ends
    ///     up writing back a value it only meant to read.
    /// </remarks>
    public string? GetStyle(string property) {
        ArgumentNullException.ThrowIfNull(property);

        if (inline is null) {
            return null;
        }

        var id = Document.Styles.Properties.Intern(property);

        foreach (var declaration in inline) {
            if (declaration.Property == id) {
                return Document.Styles.Values.NameOf(declaration.Value);
            }
        }

        return null;
    }

    /// <summary>Takes every inline declaration off this element.</summary>
    public void ClearStyle() {
        if (inline is not { Count: > 0 }) {
            return;
        }

        inline.Clear();
        Commit();
    }

    /// <summary>Whether anything has been written on this element directly.</summary>
    public bool HasInlineStyle => inline is { Count: > 0 };

    /// <summary>Publishes the current declarations to the store and points the style tree at them.</summary>
    /// <remarks>
    ///     ⚠ <b>The handle is taken once and kept.</b> Adding a block per change would grow the store
    ///     for the life of the document — see <c>InlineStyleStore.Replace</c> — and it would also
    ///     defeat the style-sharing key, which carries the handle: two elements that keep taking new
    ///     handles never look alike, however identical their declarations are.
    /// </remarks>
    void Commit() {
        var styles = Document.Styles;
        var block = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(inline);

        if (inlineId is { } existing) {
            styles.InlineStyles.Replace(existing, block);
        } else {
            inlineId = styles.AddInlineStyle(block);
            Document.Styles.Tree.SetInlineStyle(StyleNode, inlineId);
        }

        Document.Invalidate();
    }
}
