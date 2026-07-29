// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Testing;

/// <summary>The editor was asked to do something it could not.</summary>
/// <remarks>
///     ⚠ <b>Distinct from <c>UiTestException</c> on purpose.</b> That one means an element was not
///     there or was not clickable, which is a question about the interface. This one means a command
///     is not registered, a panel is not open, or a scenario step did not happen — questions about
///     the <i>editor</i>, whose answers are elsewhere. A suite that could not tell them apart would
///     have to read every message to find out which half to go and look at.
/// </remarks>
public sealed class EditorSessionException : Exception {
    /// <summary>An unexplained failure.</summary>
    public EditorSessionException() { }

    /// <summary>A failure with something to say.</summary>
    /// <param name="message">What went wrong.</param>
    public EditorSessionException(string message) : base(message) { }

    /// <summary>A failure caused by another.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="inner">What caused it.</param>
    public EditorSessionException(string message, Exception inner) : base(message, inner) { }
}
