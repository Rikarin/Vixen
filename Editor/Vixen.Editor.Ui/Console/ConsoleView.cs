// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.Ui;

/// <summary>The panel that is looked at most often when something has gone wrong.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20 calls the Console "the least built thing in the shell" and the largest single
///         item in its Part A, and both were true: it was a <c>TextBlock</c> saying "Nothing logged
///         yet."</b> What replaces it is doc 20's A7 in full — a virtualised list over the ring the
///         diagnostics layer already keeps, level toggles carrying counts, a category filter, a
///         search box, collapse-duplicates, clear, clear-on-play, and a detail pane showing the whole
///         record and its stack.
///     </para>
///     <para>
///         ⚠ <b>It does not allocate per line.</b> Doc 20 is blunt about why — "a game logging per
///         frame into a panel that keeps strings is a leak with a UI" — and there are two halves to
///         it. <see cref="ConsoleModel" /> pulls only what is new out of the ring and keeps indices
///         rather than text; <c>VirtualizingPanel</c> keeps about thirty row elements whatever the
///         list holds. A hundred thousand lines is a hundred thousand records the sink had already
///         allocated, and thirty elements.
///     </para>
///     <para>
///         The panel is <c>ConsoleView.vxml</c>, its rows an <c>@rows</c> block (#758) — the last
///         production list that set <c>CreateRow</c>/<c>BindRow</c> by hand. This file is the
///         intrinsic tags the rows and the detail pane are written in. <c>ConsoleViewDumpTests</c>
///         holds the panel to what the hand-written control drew.
///     </para>
/// </remarks>
public sealed partial class ConsoleView;

/// <summary>A console row's severity stripe, coloured by its <c>level-*</c> class.</summary>
/// <remarks>
///     ⚠ <b>One tiny type per tag, and that is the markup ports' convention rather than
///     ceremony</b> — see <c>MessageMark</c>. A capitalised tag with a <c>Text</c> writes the
///     element's own text, where an interpolation inside a plain element makes a child text node;
///     the columns were written as their own text by the hand-built rows, and a row whose layout the
///     stylesheet sizes by column has to stay that shape.
/// </remarks>
internal sealed class ConsoleLevelMark : UiElement {
    /// <inheritdoc />
    protected override string TagName => "console-level-mark";
}

/// <inheritdoc cref="ConsoleLevelMark" />
internal sealed class ConsoleTime : UiElement {
    /// <inheritdoc />
    protected override string TagName => "console-time";
}

/// <inheritdoc cref="ConsoleLevelMark" />
internal sealed class ConsoleCategory : UiElement {
    /// <inheritdoc />
    protected override string TagName => "console-category";
}

/// <inheritdoc cref="ConsoleLevelMark" />
internal sealed class ConsoleMessage : UiElement {
    /// <inheritdoc />
    protected override string TagName => "console-message";
}

/// <inheritdoc cref="ConsoleLevelMark" />
internal sealed class ConsoleRepeats : UiElement {
    /// <inheritdoc />
    protected override string TagName => "console-repeats";
}

/// <inheritdoc cref="ConsoleLevelMark" />
internal sealed class ConsoleDetailHeading : UiElement {
    /// <inheritdoc />
    protected override string TagName => "console-detail-heading";
}

/// <inheritdoc cref="ConsoleLevelMark" />
internal sealed class ConsoleDetailMeta : UiElement {
    /// <inheritdoc />
    protected override string TagName => "console-detail-meta";
}

/// <inheritdoc cref="ConsoleLevelMark" />
internal sealed class ConsoleDetailStack : UiElement {
    /// <inheritdoc />
    protected override string TagName => "console-detail-stack";
}
