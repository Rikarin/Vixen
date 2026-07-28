// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
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
}
