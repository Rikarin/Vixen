// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Ui;

/// <summary>The panel doc 20's A7 asks for: where the toasts accumulate once they have gone.</summary>
/// <remarks>
///     <para>
///         <b><see cref="NotificationCenter" /> has kept a bounded history since it was written and
///         nothing showed it.</b> A toast is a message that expires, and the thing a person does
///         after an import fails is look away, look back, and find it gone — errors are exempt from
///         expiry precisely because of that, which leaves the editor's corner slowly filling with
///         undismissed errors instead. This is the place they go.
///     </para>
///     <para>
///         ⚠ <b>Not the Console, and the difference is who wrote the line.</b> The console is the
///         whole of <c>Vixen.Core.Diagnostics</c>' ring — every category, every level, the game's
///         lines and the engine's. This is what the <i>editor</i> decided was worth interrupting
///         somebody about, which is a list two orders of magnitude shorter and the one you scan
///         after something went wrong. The console mirror means every entry here is in there too;
///         the reverse is emphatically not true.
///     </para>
///     <para>
///         ⚠ <b>The history is the model and this holds no copy of it.</b>
///         <see cref="NotificationCenter.History" /> is newest-first and bounded, so the rows are
///         built from it on change rather than accumulated here — an editor left open for a week is
///         then bounded by the centre's own limit rather than by two lists that have to agree.
///     </para>
///     <para>
///         The panel is <c>MessageLogView.vxml</c> (#89); this file is the accessibility modifier
///         and the reasoning, the arrangement <c>SceneHierarchyView</c> uses. The toolbar and the
///         detail pane are markup; the rows stay a <c>VirtualizingPanel</c> row template, because a
///         virtualiser's pool slot is not an identity a <c>@for</c> could key on (#758).
///         <c>MessageLogViewDumpTests</c> holds the port to what the hand-written control drew.
///     </para>
/// </remarks>
public sealed partial class MessageLogView;
