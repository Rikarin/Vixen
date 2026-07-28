// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>Writing run-time lengths onto elements, said once.</summary>
/// <remarks>
///     <para>
///         Every control in this assembly positions something a stylesheet could not have known
///         about — a row at y = 880 000, a node at the zoom the user has dragged to, a keyframe at
///         3.4 seconds — and every one of them has to turn a <see cref="float" /> into a string a
///         parser will accept. Doing that inline meant the same
///         <c>ToString("0.##", CultureInfo.InvariantCulture) + "px"</c> in forty places, which is
///         forty chances to leave the culture out.
///     </para>
///     <para>
///         ⚠ <b>The culture is not optional.</b> A Czech or German thread renders <c>12.5</c> as
///         <c>12,5</c>; the style parser reads that as two values, takes the first, and lays out at
///         twelve pixels. The failure is a layout that is subtly wrong on some machines and right on
///         the developer's, which is the worst kind there is.
///     </para>
///     <para>
///         Two decimal places, because sub-pixel precision below a hundredth changes no picture and
///         a longer string is a longer intern-table key on a value that is rewritten every frame of
///         a drag.
///     </para>
/// </remarks>
static class Inline {
    /// <summary>A length, as a stylesheet would write it.</summary>
    public static string Px(float value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";

    /// <summary>A bare number, for <c>flex-grow</c> and <c>opacity</c>.</summary>
    public static string Number(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Places an absolutely-positioned element inside its parent.</summary>
    /// <param name="element">The element.</param>
    /// <param name="x">Its left edge, relative to the parent.</param>
    /// <param name="y">Its top edge.</param>
    /// <param name="width">How wide.</param>
    /// <param name="height">How tall.</param>
    public static void Place(this UiElement element, float x, float y, float width, float height) {
        element.SetStyle("left", Px(x));
        element.SetStyle("top", Px(y));
        element.SetStyle("width", Px(width));
        element.SetStyle("height", Px(height));
    }
}
