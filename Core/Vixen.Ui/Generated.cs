// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>The box a <c>::before</c> or <c>::after</c> rule generates. Not an element of the document.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>In the box tree and not in the element tree, which is the decision doc 43's A12 waited
///         on.</b> It is a layout child of the element that generated it — first for <c>::before</c>,
///         last for <c>::after</c> — and it is in that element's <see cref="UiElement.PaintOrder" />,
///         so the two walks that read paint order draw it and hit-test it. It is in nothing else:
///         not <see cref="UiElement.Children" />, not the style tree, not the accessibility tree,
///         and never an event's target. That is the DOM's own arrangement — a pseudo-element is not a
///         child node — and the one that keeps <c>:first-child</c>, <c>:empty</c>, the tab order and
///         eleven hundred <c>Children</c> readers from ever seeing a box an author wrote in a sheet.
///     </para>
///     <para>
///         An element rather than a lighter type because everything a generated box has to do — be
///         measured by its text, be painted with a background and a border, be shaped in the right
///         font — is already what an element with text does, and a second implementation of any of
///         it would be the one that drifts. Its <see cref="UiElement.Style" /> comes from
///         <see cref="StyleUpdater.PseudoStyleOf" />, not from a slot of its own; see
///         <see cref="StyleResolver.ResolvePseudo" /> for why it has none.
///     </para>
/// </remarks>
internal sealed class GeneratedBox : UiElement {
    /// <summary>Creates the box an element generates.</summary>
    /// <param name="kind">Which of the two.</param>
    public GeneratedBox(PseudoElement kind) => Kind = kind;

    /// <summary>Which pseudo-element this is.</summary>
    public PseudoElement Kind { get; }
}

public sealed partial class UiDocument {
    /// <summary>The <c>content</c> property's id, interned the first time a generated box is asked about.</summary>
    int contentProperty = -1;

    /// <summary>How many generated boxes have been built, ever. A work counter for tests.</summary>
    internal int GeneratedBoxesBuilt { get; private set; }

    /// <summary>Brings an element's generated boxes into line with what the cascade now says about them.</summary>
    /// <param name="element">The originating element, whose own style has just been applied.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Run from <c>Apply</c>, after the element's style and before its children, because
    ///         whether a box exists is a computed value and not a structural fact.</b> CSS
    ///         Pseudo-Elements 4 § 2.4 generates a <c>::before</c> exactly when its <c>content</c>
    ///         computes to something other than <c>none</c> or <c>normal</c>, so a class toggled on
    ///         the element can make one appear, and only the style pass knows.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Never on an element that carries text of its own.</b> Such an element measures
    ///         itself — its <c>Text</c> is a single run shaped as one — and the layout tree refuses a
    ///         node that both measures and has children. A browser puts the generated text inline,
    ///         beside the element's own words; Vixen has no run that can carry a second style, so the
    ///         honest answer is no box rather than a box the layout throws on. See
    ///         <c>InlineKnownGaps.txt</c>.
    ///     </para>
    /// </remarks>
    void Generate(UiElement element) {
        if (element is GeneratedBox) {
            return;
        }

        var bare = string.IsNullOrEmpty(element.Text);

        var before = bare ? ContentOf(Restyler.PseudoStyleOf(element.StyleNode, PseudoElement.Before)) : null;
        var after = bare ? ContentOf(Restyler.PseudoStyleOf(element.StyleNode, PseudoElement.After)) : null;

        Reconcile(element, PseudoElement.Before, before);
        Reconcile(element, PseudoElement.After, after);
    }

    /// <summary>Builds, rewrites or drops one of an element's two generated boxes.</summary>
    void Reconcile(UiElement element, PseudoElement kind, string? content) {
        var box = kind == PseudoElement.Before ? element.GeneratedBefore : element.GeneratedAfter;

        if (content is null) {
            if (box is not null) {
                Ungenerate(element, box);
            }

            return;
        }

        if (box is null) {
            box = new GeneratedBox(kind);
            var node = Layout.CreateNode();

            box.Bind(this, kind == PseudoElement.Before ? "::before" : "::after", element, StyleNodeId.Invalid, node);

            // ⚠ First and last in the LAYOUT child list, which is not the element list: a surface
            // root is in the second and not the first, and a `::before` is in the first and not the
            // second. `LayoutIndexOf` counts the `::before` for exactly that reason, and `Adopt`
            // inserts a new child ahead of an `::after` rather than appending behind it.
            if (kind == PseudoElement.Before) {
                Layout.InsertChild(element.LayoutNode, node, 0);
            } else {
                Layout.AddChild(element.LayoutNode, node);
            }

            element.SetGenerated(kind, box);
            GeneratedBoxesBuilt++;
        }

        // The setter compares, so an unchanged string costs a comparison and dirties nothing.
        box.Text = content;
    }

    /// <summary>Takes a generated box out of the layout and paint trees.</summary>
    void Ungenerate(UiElement element, GeneratedBox box) {
        Layout.RemoveChild(element.LayoutNode, box.LayoutNode);
        Layout.DestroyRecursive(box.LayoutNode);
        element.SetGenerated(box.Kind, null);
    }

    /// <summary>Drops both of an element's generated boxes, ahead of it starting to measure itself.</summary>
    /// <remarks>
    ///     Called from <c>UiElement.OnTextChanged</c>, before the measure function goes on: the layout
    ///     tree refuses a measure function on a node with children, and a generated box is one. The
    ///     next style pass would have dropped them anyway — <see cref="Generate" /> refuses a box on
    ///     an element with text — but the measure function cannot wait for it.
    /// </remarks>
    internal void Ungenerate(UiElement element) {
        if (element.GeneratedBefore is { } before) {
            Ungenerate(element, before);
        }

        if (element.GeneratedAfter is { } after) {
            Ungenerate(element, after);
        }
    }

    /// <summary>What a generated box's <c>content</c> computes to, as text, or null for no box.</summary>
    /// <param name="style">The box's style, from <see cref="StyleUpdater.PseudoStyleOf" />.</param>
    /// <returns>The string to draw — possibly empty, which is still a box — or null.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Strings only.</b> One or more quoted strings, concatenated as CSS Content 3 § 1
    ///         concatenates them, with CSS's escapes read. <c>attr()</c>, counters, quotes and
    ///         images are not read and give no box — an author asking for a counter gets nothing
    ///         rather than the literal text <c>counter(item)</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>content: ""</c> is a box, and the commonest one.</b> An empty string is the
    ///         idiom for a decorative box sized and painted entirely by its other properties — a dot, a
    ///         rule, a triangle — and treating it as "no content" would delete exactly those. Only
    ///         <c>none</c>, <c>normal</c> and no declaration at all mean no box, plus
    ///         <c>display: none</c>, which suppresses any box.
    ///     </para>
    /// </remarks>
    string? ContentOf(ComputedStyle? style) {
        if (style is null) {
            return null;
        }

        if (contentProperty < 0) {
            contentProperty = Styles.Properties.Intern("content");
        }

        if (!style.TryGet(contentProperty, out var value)) {
            return null;
        }

        if (style.TryGet(display, out var shown) && shown == none) {
            return null;
        }

        return ParseContent(Styles.Values.NameOf(value));
    }

    /// <summary>Reads a <c>content</c> value made only of strings.</summary>
    /// <param name="text">The computed value as the cascade holds it.</param>
    /// <returns>The concatenated strings, or null if the value is anything else.</returns>
    internal static string? ParseContent(string text) {
        var result = new StringBuilder();
        var any = false;
        var i = 0;

        while (i < text.Length) {
            var c = text[i];

            if (char.IsWhiteSpace(c)) {
                i++;
                continue;
            }

            if (c is not ('"' or '\'')) {
                return null;
            }

            any = true;
            i++;

            var closed = false;

            while (i < text.Length) {
                var d = text[i];

                if (d == c) {
                    closed = true;
                    i++;
                    break;
                }

                if (d == '\\' && i + 1 < text.Length) {
                    i++;

                    // CSS Syntax 3 § 4.3.7: up to six hex digits and one optional space after them,
                    // so that `"\2022 x"` is a bullet and a space-less x.
                    var start = i;

                    while (i < text.Length && i - start < 6 && Uri.IsHexDigit(text[i])) {
                        i++;
                    }

                    if (i > start) {
                        var code = int.Parse(text.AsSpan(start, i - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        result.Append(code is 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF) ? "�" : char.ConvertFromUtf32(code));

                        if (i < text.Length && text[i] == ' ') {
                            i++;
                        }

                        continue;
                    }

                    // An escaped newline continues the string and contributes nothing.
                    if (text[i] != '\n') {
                        result.Append(text[i]);
                    }

                    i++;
                    continue;
                }

                result.Append(d);
                i++;
            }

            if (!closed) {
                return null;
            }
        }

        return any ? result.ToString() : null;
    }
}
