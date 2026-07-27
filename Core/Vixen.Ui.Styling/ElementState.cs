// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>The transient conditions a selector can ask about.</summary>
/// <remarks>
///     Kept as flags on the element rather than as a query into the input system, because the
///     cascade has to be able to answer "what would this element look like if it were hovered"
///     without anything actually being hovered — which is what a transition needs in order to know
///     what it is transitioning to.
/// </remarks>
[Flags]
public enum ElementState : uint {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The pointer is over it.</summary>
    Hover = 1 << 0,

    /// <summary>It is being pressed.</summary>
    Active = 1 << 1,

    /// <summary>It has keyboard focus.</summary>
    Focus = 1 << 2,

    /// <summary>It has focus and the focus should be shown — keyboard navigation rather than a click.</summary>
    FocusVisible = 1 << 3,

    /// <summary>It does not accept input.</summary>
    Disabled = 1 << 4,

    /// <summary>It is a checkbox, radio or toggle that is on.</summary>
    Checked = 1 << 5,

    /// <summary>Focus is on it or on something inside it.</summary>
    FocusWithin = 1 << 6
}
