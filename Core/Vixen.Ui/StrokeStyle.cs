// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>What a <c>border-style</c> or an <c>outline-style</c> asks a stroke to look like.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="Solid" /> is zero and it is what an element that declares no style gets,
///         which is a deliberate departure from CSS's initial value of <c>none</c>.</b> A browser
///         paints nothing for <c>border-width: 2px</c> alone; this engine has always painted, and
///         every stylesheet, theme and utility class in the tree is written against that — the
///         <c>border-*</c> family emits a width and no style at all, and v4 emits
///         <c>outline-style: solid</c> beside every <c>outline-*</c> width precisely because a
///         browser would otherwise draw nothing. Taking CSS's initial value here would blank every
///         border in the repository at once, to obey a rule whose only purpose is to let the
///         <c>border</c> shorthand carry a width without committing to one.
///     </para>
///     <para>
///         ⚠ <b><c>groove</c>, <c>ridge</c>, <c>inset</c> and <c>outset</c> are absent rather than
///         aliased onto <see cref="Solid" />.</b> All four are two-tone: CSS derives a lighter and a
///         darker shade of the border colour and gives one to each pair of edges, which is a second
///         colour this engine's border record does not carry. An alias would resolve, compute and
///         paint a flat ring — the inert shape the utility gate exists to keep out — so an
///         unrecognised keyword is read as <see cref="Solid" /> only in the sense that any word this
///         table does not list is, and no utility can produce one.
///     </para>
/// </remarks>
enum StrokeStyle : byte {
    /// <summary>One unbroken line. The default here, for the reason on the type.</summary>
    Solid,

    /// <summary>Nothing is painted. Both <c>none</c> and <c>hidden</c> arrive here.</summary>
    None,

    /// <summary>Marks three times the thickness, with gaps of twice it.</summary>
    Dashed,

    /// <summary>Square marks one thickness long, with gaps of one.</summary>
    Dotted,

    /// <summary>Two lines a third of the thickness each, with a third between them.</summary>
    Double
}

/// <summary>One mark of a dashed or dotted line, as a distance along the run and a length.</summary>
/// <param name="Start">How far along the run the mark begins.</param>
/// <param name="Length">How long the mark is.</param>
readonly record struct DashMark(float Start, float Length);

/// <summary>How a dashed or a dotted line is broken into marks.</summary>
/// <remarks>
///     <para>
///         <b>One distribution rule and three consumers</b> — a border's ring, a border's per-edge
///         band, and a text decoration's bar. Doc 43 § A3 records the four families that read
///         <c>partial</c> for want of exactly this, and they read <c>partial</c> together because
///         there was one thing missing rather than four.
///     </para>
///     <para>
///         ⚠ <b>The marks are stretched to fit and the run always begins and ends with one.</b> The
///         obvious implementation walks the run in fixed periods and stops when it runs out, which
///         leaves a stub at the far end whose length is whatever the arithmetic happened to leave —
///         so a dashed box has a full mark in one corner and a sliver in the other, and the sliver
///         moves when the box is resized by a pixel. Holding the <i>mark</i> fixed and spreading the
///         remainder over the gaps is what makes both ends of every edge look alike, and it is the
///         rule CSS Backgrounds 3 § 4.2 states for exactly that reason.
///     </para>
///     <para>
///         ⚠ <b>A run too short for two marks is one mark spanning the whole of it.</b> Not a stub
///         and not nothing: a one-pixel dashed line across an eight-pixel box is a solid line, which
///         is what a browser draws and is the only answer that does not make a short edge disappear.
///     </para>
/// </remarks>
static class Dashes {
    /// <summary>Whether a style is drawn as marks at all.</summary>
    public static bool Broken(StrokeStyle style) => style is StrokeStyle.Dashed or StrokeStyle.Dotted;

    /// <summary>How long one mark is, as a multiple of the line's thickness.</summary>
    /// <remarks>
    ///     ⚠ Chosen to make a dot square and a dash visibly a dash at one pixel, which is the
    ///     thickness nearly every rule in this repository uses. A dash of twice the thickness at one
    ///     pixel is a two-pixel mark next to a two-pixel gap, which reads as a dotted line.
    /// </remarks>
    public static float MarkOf(StrokeStyle style, float thickness) =>
        thickness * (style == StrokeStyle.Dotted ? 1f : 3f);

    /// <summary>How long the nominal gap is, before the remainder is spread over the gaps.</summary>
    public static float GapOf(StrokeStyle style, float thickness) =>
        thickness * (style == StrokeStyle.Dotted ? 1f : 2f);

    /// <summary>The marks along one run.</summary>
    /// <param name="length">How long the run is.</param>
    /// <param name="thickness">How thick the line is, which is what sets the mark and gap lengths.</param>
    /// <param name="style">Which of the two broken styles.</param>
    /// <param name="into">Where the marks go. Cleared first.</param>
    /// <remarks>
    ///     ⚠ <b>The ink is exactly <c>count × mark</c>, which is what makes this testable without a
    ///     picture.</b> A dashed run covers strictly less than the run it is on and covers a length
    ///     nobody has to eyeball, so a test can assert the covered area rather than compare two
    ///     images — the closed-form oracle this repository prefers over a reference PNG.
    /// </remarks>
    public static void Along(float length, float thickness, StrokeStyle style, List<DashMark> into) {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();

        if (length <= 0f) {
            return;
        }

        var mark = MarkOf(style, thickness);
        var gap = GapOf(style, thickness);

        if (mark <= 0f || length <= mark) {
            into.Add(new DashMark(0f, length));
            return;
        }

        // The nearest whole number of periods, where a period is a mark and a gap and the run ends
        // on a mark — so `length + gap` is what divides evenly. Clamped so the marks cannot ask for
        // more room than the run has, which `Round` can do on a run just under two periods.
        var period = mark + gap;
        var count = Math.Clamp((int) MathF.Round((length + gap) / period), 1, (int) (length / mark));

        if (count <= 1) {
            into.Add(new DashMark(0f, length));
            return;
        }

        var spread = (length - (count * mark)) / (count - 1);

        for (var i = 0; i < count; i++) {
            into.Add(new DashMark(i * (mark + spread), mark));
        }
    }
}
