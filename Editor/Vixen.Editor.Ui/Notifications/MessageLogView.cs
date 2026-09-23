// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

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
///         The panel is <c>MessageLogView.vxml</c>, its rows an <c>@rows</c> block (#758); this file
///         is the accessibility modifier and the intrinsic tags the rows and the detail pane are
///         written in.
///     </para>
/// </remarks>
public sealed partial class MessageLogView;

/// <summary>A message row's severity stripe, coloured by its <c>level-*</c> class.</summary>
/// <remarks>
///     ⚠ <b>One tiny type per tag, and that is the markup ports' convention rather than
///     ceremony</b> — see <c>Captions.cs</c> in the asset editors. A capitalised tag with a
///     <c>Text</c> writes the element's own text, where an interpolation inside a plain element makes
///     a child text node; the columns here were written as their own text by the hand-built rows, and
///     a row whose layout the stylesheet sizes by column has to stay that shape.
/// </remarks>
internal sealed class MessageMark : UiElement {
    /// <inheritdoc />
    protected override string TagName => "message-mark";
}

/// <inheritdoc cref="MessageMark" />
internal sealed class MessageTime : UiElement {
    /// <inheritdoc />
    protected override string TagName => "message-time";
}

/// <inheritdoc cref="MessageMark" />
internal sealed class MessageText : UiElement {
    /// <inheritdoc />
    protected override string TagName => "message-text";
}

/// <inheritdoc cref="MessageMark" />
internal sealed class MessageDetailText : UiElement {
    /// <inheritdoc />
    protected override string TagName => "message-detail-text";
}

/// <inheritdoc cref="MessageMark" />
internal sealed class MessageDetailHeading : UiElement {
    /// <inheritdoc />
    protected override string TagName => "message-detail-heading";
}

/// <inheritdoc cref="MessageMark" />
internal sealed class MessageDetailMeta : UiElement {
    /// <inheritdoc />
    protected override string TagName => "message-detail-meta";
}

/// <inheritdoc cref="MessageMark" />
internal sealed class MessageDetailBody : UiElement {
    /// <inheritdoc />
    protected override string TagName => "message-detail-body";
}
