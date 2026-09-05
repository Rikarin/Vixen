// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Layout;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

public sealed partial class UiDocument {
    readonly StyleValueParser reader;
    readonly int color;

    /// <summary>Interns a property name, so a control can read it without a dictionary probe per frame.</summary>
    /// <param name="name">The property, as a stylesheet writes it.</param>
    /// <returns>Its identifier, for <see cref="ColorOf" /> and <see cref="LengthOf" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Per document, not global.</b> The tables belong to a <see cref="StyleEngine" /> and
    ///     two documents intern independently, so an identifier from one means something else in the
    ///     other. A control caching one must cache it against the document it came from — which is
    ///     the document it was created in, and elements do not move between documents.
    /// </remarks>
    public int PropertyId(string name) {
        ArgumentNullException.ThrowIfNull(name);
        return Styles.Properties.Intern(name);
    }

    /// <summary>The colour a computed style gives a property, if it gives one.</summary>
    /// <param name="style">The style, from <see cref="UiElement.Style" />.</param>
    /// <param name="property">The property, from <see cref="PropertyId" />.</param>
    /// <returns>The colour, or <c>null</c> if the property is absent or is not a colour.</returns>
    /// <remarks>
    ///     What a control that draws itself reads. <see cref="UiElement.OnDraw" /> is the escape
    ///     hatch out of the declarative side, and a hatch that could not see the cascade would make
    ///     every custom-drawn control carry hard-coded colours no theme could reach — which is most
    ///     of the interesting ones: a slider's fill, a checkbox's tick, a spinner's arc.
    /// </remarks>
    public Color4? ColorOf(ComputedStyle style, int property) {
        if (!style.TryGet(property, out var id)) {
            return null;
        }

        var value = reader.Parse(id);
        return value.Kind == StyleValueKind.Color ? value.Color : null;
    }

    /// <summary>The length in pixels a computed style gives a property, if it gives an absolute one.</summary>
    /// <param name="style">The style.</param>
    /// <param name="property">The property, from <see cref="PropertyId" />.</param>
    /// <returns>The length, or <c>null</c>.</returns>
    /// <remarks>
    ///     Absolute lengths only, and deliberately so. A percentage resolves against something this
    ///     cannot see and an <c>em</c> against a font size that is on the element rather than in the
    ///     style — a control that needs either should be reading the layout results, which have had
    ///     both resolved for it.
    /// </remarks>
    public float? LengthOf(ComputedStyle style, int property) {
        if (!style.TryGet(property, out var id)) {
            return null;
        }

        var value = reader.Parse(id);
        if (value.Kind == StyleValueKind.List && value.Items.Length > 0) {
            value = value.Items[0];
        }

        return value is { Kind: StyleValueKind.Length, Unit: StyleUnit.Pixels } ? value.Number : null;
    }

    /// <summary>The bare number a computed style gives a property, if it gives one.</summary>
    /// <param name="style">The style, from <see cref="UiElement.Style" />.</param>
    /// <param name="property">The property, from <see cref="PropertyId" />.</param>
    /// <returns>The number, or <c>null</c> if the property is absent or is not a bare number.</returns>
    /// <remarks>
    ///     The third of the readings a control needs, beside <see cref="ColorOf" /> and
    ///     <see cref="LengthOf" />: <c>opacity</c>, <c>flex-grow</c> and <c>z-index</c> are numbers
    ///     rather than lengths, and reading one through <see cref="LengthOf" /> answers <c>null</c>
    ///     because a number carries no unit — which looks exactly like the property being absent.
    /// </remarks>
    public float? NumberOf(ComputedStyle style, int property) {
        if (!style.TryGet(property, out var id)) {
            return null;
        }

        var value = reader.Parse(id);
        return value.Kind == StyleValueKind.Number ? value.Number : null;
    }

    /// <summary>The keyword a computed style gives a property, if it gives a single one.</summary>
    /// <param name="style">The style, from <see cref="UiElement.Style" />.</param>
    /// <param name="property">The property, from <see cref="PropertyId" />.</param>
    /// <returns>The keyword, or <c>null</c> if the property is absent or is not one bare identifier.</returns>
    /// <remarks>
    ///     <para>
    ///         The fourth reading beside <see cref="ColorOf" />, <see cref="LengthOf" /> and
    ///         <see cref="NumberOf" />, and the one the other three cannot stand in for: a keyword is
    ///         what they all answer <c>null</c> to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is the whole defect it was added for.</b> SVG's <c>fill</c> and
    ///         <c>stroke</c> take a <i>paint</i>, and <c>none</c> is a paint — so
    ///         <c>fill: none</c> and a <c>fill</c> nobody set are two different instructions that
    ///         <see cref="ColorOf" /> reports identically. <c>Icon.Resolve</c> read the first as the
    ///         second and painted the icon in the inherited colour, which is the opposite of what was
    ///         asked for and is invisible to any gate that only knows whether a property is read.
    ///     </para>
    ///     <para>
    ///         The string comes out of the name table rather than being built, so a caller comparing
    ///         it per frame allocates nothing. A two-word value — <c>safe center</c> — answers
    ///         <c>null</c>, because it is not one keyword and a caller asking this question wants to
    ///         know that.
    ///     </para>
    /// </remarks>
    public string? KeywordOf(ComputedStyle style, int property) {
        if (!style.TryGet(property, out var id)) {
            return null;
        }

        var value = reader.Parse(id);
        return value.Kind == StyleValueKind.Keyword ? Styles.Names.NameOf(value.Keyword) : null;
    }

    /// <summary>An element's <c>color</c>, which is what a control draws itself in.</summary>
    /// <param name="element">The element.</param>
    /// <returns>Its colour, or black if it has none.</returns>
    /// <remarks>
    ///     Black rather than transparent when nothing said, because <c>color</c> is inherited and a
    ///     document whose root never set one is a document nobody has themed yet — where invisible
    ///     controls read as a broken framework and black ones read as an unthemed one.
    /// </remarks>
    public Color4 ForegroundOf(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);
        return ColorOf(element.Style, color) ?? Color4.Black;
    }

    bool alignmentInterned;
    int textAlign;
    int flowDirection;
    int alignedCenter;
    int alignedLeft;
    int alignedRight;
    int alignedEnd;
    int rightToLeft;

    /// <summary>How far along the inline axis a line of text sits, given the room it has spare.</summary>
    /// <param name="element">The element whose <c>text-align</c> and <c>direction</c> decide it.</param>
    /// <param name="slack">The content box's width less the line's, which may be negative.</param>
    /// <returns>What to add to the line's left edge.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Public because the glyphs are not the only thing on the line.</b> The draw path
    ///         has always applied this; a caret, a selection band and a hit test have to apply the
    ///         identical number or they land somewhere the text is not — and until they did, a
    ///         wrapped RTL field drew its caret at the left edge of the block while the short line it
    ///         belonged to sat flush against the right, fifty pixels away. ⚠ Two implementations of
    ///         this rule would be the same defect waiting to come back, so there is one, and
    ///         <c>DrawListBuilder</c> is a caller of it rather than the owner.
    ///     </para>
    ///     <para>
    ///         <c>start</c> and <c>end</c> are resolved against <c>direction</c>, the same property
    ///         the layout resolves its logical edges with — so a label written <c>text-end</c> lands
    ///         on the same side as the padding <c>pe-2</c> gave it. <c>justify</c> falls through to
    ///         the start, which is not a shortcut: CSS aligns the <i>last</i> line of a justified
    ///         block to the start, and a single-line run is its own last line.
    ///     </para>
    ///     <para>
    ///         ⚠ Negative slack is left alone. Text wider than its box overflows to the right of the
    ///         start edge whatever the alignment says, because centring it would hide the beginning of
    ///         the string — and the beginning is the part a reader needs to recognise what has been
    ///         cut off.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The initial value of <c>text-align</c> is <c>start</c>, and <c>start</c> is not
    ///         the left.</b> Reading a miss as zero made every Arabic and Hebrew paragraph nobody had
    ///         written an alignment for ragged down the right and flush against the left, which no
    ///         assertion about glyph order can see — the glyphs inside each line were in the correct
    ///         order the whole time.
    ///     </para>
    /// </remarks>
    public float TextAlignShift(UiElement element, float slack) {
        ArgumentNullException.ThrowIfNull(element);

        if (slack <= 0f) {
            return 0f;
        }

        if (!alignmentInterned) {
            textAlign = Styles.Properties.Intern("text-align");
            flowDirection = Styles.Properties.Intern("direction");
            alignedCenter = Styles.Values.Intern("center");
            alignedLeft = Styles.Values.Intern("left");
            alignedRight = Styles.Values.Intern("right");
            alignedEnd = Styles.Values.Intern("end");
            rightToLeft = Styles.Values.Intern("rtl");

            alignmentInterned = true;
        }

        var mirrored = element.Style.TryGet(flowDirection, out var flow) && flow == rightToLeft;

        if (!element.Style.TryGet(textAlign, out var alignment)) {
            return mirrored ? slack : 0f;
        }

        // The physical keywords first, because they mean a side whatever the direction is — that is
        // the whole difference between them and the logical ones.
        if (alignment == alignedCenter) {
            return slack * 0.5f;
        }

        if (alignment == alignedRight) {
            return slack;
        }

        if (alignment == alignedLeft) {
            return 0f;
        }

        // `start`, `end`, and anything unrecognised — which lands on the start edge, the same place
        // an element with no `text-align` at all sits.
        return mirrored != (alignment == alignedEnd) ? slack : 0f;
    }

    /// <summary>The width of an element's content box, which is what a line of its text is aligned in.</summary>
    /// <param name="element">The element.</param>
    /// <returns>Its width less its borders and padding.</returns>
    /// <remarks>
    ///     Read from the layout results rather than from the style, so a percentage padding is the
    ///     number flexbox resolved rather than a percentage this would have to resolve again. Against
    ///     the content box and not the border box, because using the latter pushes centred text off
    ///     by half the padding, in the direction that looks like the padding is uneven.
    /// </remarks>
    public float ContentWidthOf(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);

        return element.Width
            - Layout.GetComputedBorder(element.LayoutNode, Edge.Left)
            - Layout.GetComputedPadding(element.LayoutNode, Edge.Left)
            - Layout.GetComputedBorder(element.LayoutNode, Edge.Right)
            - Layout.GetComputedPadding(element.LayoutNode, Edge.Right);
    }
}
