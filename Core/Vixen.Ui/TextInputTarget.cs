// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui;

/// <summary>A focusable element that wants keystrokes to go through the operating system's input method.</summary>
/// <remarks>
///     <para>
///         <b>The framework half of <c>ITextInput</c>, which <c>Vixen.Ui</c> is not allowed to name.</b>
///         <c>Vixen.Platform</c> sits above the <c>Core/</c> assemblies, so a control cannot call
///         <c>Activate</c> or <c>SetCandidateArea</c> itself. What it can do is answer two questions
///         about itself, and let the host — <c>Vixen.Platform.Ui</c>'s <c>PlatformTextInput</c> — ask
///         them once a frame. The same shape as <see cref="UiDocument.Cursor" /> and
///         <c>PlatformCursor</c>, for the same layering reason.
///     </para>
///     <para>
///         ⚠ <b>Implementing this is what turns text input on, and nothing else does.</b> Text input
///         is off by default on every platform — while it is on, the platform gives keystrokes to
///         the IME first, so <c>W</c> may compose instead of moving a character. Desktop appears to
///         work without any of this only because SDL leaves text input running; a focused field on
///         web or on a phone receives nothing at all until somebody activates it.
///     </para>
/// </remarks>
public interface ITextInputTarget {
    /// <summary>Whether this element would accept typed text right now.</summary>
    /// <remarks>
    ///     ⚠ <b>False on a read-only or disabled field, which is not the same as unfocusable.</b>
    ///     Such a field is still focused and still selectable — selecting and copying is what a
    ///     read-only field is for — and turning the IME on for it would put a candidate window over
    ///     a field that will discard everything it commits.
    /// </remarks>
    bool AcceptsTextInput { get; }

    /// <summary>Where the caret is, in the document's absolute coordinate space, in logical points.</summary>
    /// <remarks>
    ///     ⚠ <b>Absolute and not element-relative, because the host converts to window coordinates
    ///     and a surface's origin is its window's.</b> This is what an input method places its
    ///     candidate list against: without it the list is drawn at a corner of the screen, over
    ///     something, and every CJK user sees it on the first character they type.
    /// </remarks>
    Rectangle CaretArea { get; }
}
