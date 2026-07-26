// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>A mouse button, named by role rather than by side.</summary>
/// <remarks>
///     <see cref="Primary" /> is the button under the index finger — which is the physical left
///     button for most people and the right one for a left-handed pointer configuration. The OS has
///     already applied that swap by the time an event reaches us, so binding to
///     <see cref="Primary" /> respects the user's setting and binding to "left" silently does not.
/// </remarks>
public enum MouseButton : byte {
    /// <summary>No button — a move, or a wheel event.</summary>
    None = 0,

    /// <summary>The main button: select, click, drag.</summary>
    Primary = 1,

    /// <summary>The context-menu button.</summary>
    Secondary = 2,

    /// <summary>The wheel pressed in.</summary>
    Middle = 3,

    /// <summary>The first thumb button, conventionally "back".</summary>
    Extra1 = 4,

    /// <summary>The second thumb button, conventionally "forward".</summary>
    Extra2 = 5
}
