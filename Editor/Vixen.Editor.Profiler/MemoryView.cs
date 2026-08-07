// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Profiler;

/// <summary>Doc 13's four memory arenas, each with its rows.</summary>
/// <remarks>
///     <para>
///         <b>The panel is <c>MemoryView.vxml</c>; this file exists only to make it public</b>, for
///         the reason <see cref="StatisticsView" />'s own part gives — the markup compiler emits an
///         <c>internal</c> partial and a panel another assembly constructs has to be public.
///     </para>
///     <para>
///         ⚠ <b>Refreshed on a button rather than every frame, and that is not laziness.</b>
///         <c>GC.GetGCMemoryInfo</c> and a walk of the leak tracker's dictionary are both real work,
///         and a panel that did them sixty times a second would be a memory panel that allocates. It
///         also makes the numbers <i>readable</i>: a heap size that changes every frame is one nobody
///         can compare with the one they wrote down a minute ago.
///     </para>
///     <para>
///         ⚠ <b>Refreshing must not collect.</b> <c>GC.GetTotalMemory(true)</c> would give a tidier
///         number by running a blocking gen-2 collection first — which changes what is being measured
///         and stalls the editor. <see cref="MemorySnapshot" /> passes <c>false</c> and says so.
///     </para>
/// </remarks>
public sealed partial class MemoryView;
