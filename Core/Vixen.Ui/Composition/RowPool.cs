// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Composition;

/// <summary>A control that shows a long list by re-using a handful of row elements.</summary>
/// <remarks>
///     <para>
///         <b>The seam between composition and a virtualizing control, and it exists because the
///         dependency only goes one way.</b> <c>VirtualizingPanel</c> and <c>VirtualizingGrid</c>
///         live in <c>Vixen.Ui.Controls</c>, which references <c>Vixen.Ui</c>; so
///         <see cref="BuildContext" /> cannot name either of them, and until something in this
///         assembly described what they do, the only way to fill one was to write
///         <c>CreateRow</c>/<c>BindRow</c> by hand in C#. That is what
///         <a href="https://github.com/Rikarin/Vixen/issues/758">#758</a> is about.
///     </para>
///     <para>
///         ⚠ <b>Four members, and the shape of them is the whole argument for why a pooled list is
///         not a <c>@for</c>.</b> A loop is told a sequence and keys; this is told a <i>count</i>,
///         asked for slots when it runs short of them, and then tells a slot which index it is
///         showing now. A slot is not an identity — the pool only ever grows, and a row that was
///         line 4 is line 900 after a scroll — so every rule the keyed reconciler teaches is false
///         here: nothing survives, nothing is matched, and the body is built once per slot rather
///         than once per item.
///     </para>
///     <para>
///         ⚠ <b>It is the control's own contract restated, not a new one.</b> The two delegates are
///         the ones the controls already had, with the control's own type taken out of
///         <c>CreateRow</c> so that this assembly can name it; <see cref="RowHost" /> is what
///         <c>CreateRow</c> used to reach through the panel to get at. Nothing about how a row is
///         measured, parked or positioned is here, because none of it is composition's business.
///     </para>
///     <para>
///         ⚠ <b>Two implementations, and their own names for the same members differ.</b>
///         <c>VirtualizingPanel</c> calls a slot a row and <c>VirtualizingGrid</c> calls it a tile,
///         so both implement this explicitly rather than renaming what every caller already uses —
///         and one <c>BuildContext.Pool</c> fills either, which is the whole of what the seam buys.
///     </para>
///     <para>
///         ⚠ <b>Shipped ahead of its callers, and it has three now, one per half.</b>
///         <c>MessageLogView.vxml</c> and <c>ConsoleView.vxml</c> fill their <c>VirtualizingPanel</c>s
///         through this seam with an <c>@rows</c> block, which the markup compiler turns into
///         <c>BuildContext.Pool</c> (#758), and <c>AssetGrid.vxml</c> fills the editor's one
///         <c>VirtualizingGrid</c> the same way (#1406). No production virtualised list is filled
///         from C# any more. Recorded here rather than left to a grep, because "a finished thing
///         nothing calls" is this repository's commonest defect and a reader is owed the callers
///         before concluding anything about the seam.
///     </para>
/// </remarks>
public interface IRowPool {
    /// <summary>How many items there are.</summary>
    int RowCount { get; set; }

    /// <summary>Where a row element belongs. A slot made anywhere else is not in the list.</summary>
    UiElement RowHost { get; }

    /// <summary>Makes one pool slot. Called only when the pool has to grow.</summary>
    Func<UiElement>? CreateRow { get; set; }

    /// <summary>Tells a slot which item it is showing now. Called on every realise, not only on a change.</summary>
    Action<UiElement, int>? BindRow { get; set; }
}
