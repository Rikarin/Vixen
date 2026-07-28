// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls;

/// <summary>A control was activated.</summary>
/// <remarks>
///     <para>
///         <b>Activation rather than a click</b>, which is why it is not simply
///         <see cref="TapEvent" /> renamed: a button is activated by Space, by Enter, by its access
///         key and by a tap, and a handler that had to listen for four things to hear one would get
///         three of them wrong. <see cref="Device" /> says which it was for the handlers that care —
///         a menu closing on activation wants to know whether to return the focus to the keyboard.
///     </para>
///     <para>
///         It bubbles, which is what makes a toolbar a real thing. One handler on the toolbar hears
///         every button inside it and reads <see cref="UiEvent.Source" />, rather than the toolbar
///         subscribing to twenty children and unsubscribing from each as it is rebuilt.
///     </para>
/// </remarks>
public sealed class ClickEvent : UiEvent {
    /// <summary>What activated it.</summary>
    public ActivationDevice Device { get; init; }

    /// <summary>How many taps in a row, for the pointer case. One for a keyboard activation.</summary>
    public int Count { get; init; } = 1;

    /// <summary>What was held on the keyboard at the time.</summary>
    public ModifierKeys Modifiers { get; init; }
}

/// <summary>What activated a control.</summary>
public enum ActivationDevice : byte {
    /// <summary>A tap or a click.</summary>
    Pointer,

    /// <summary>Space, Enter, or an access key.</summary>
    Keyboard,

    /// <summary>Code called <c>Activate</c>. A test, a script, an automation peer.</summary>
    Code
}

/// <summary>A control's value became another value.</summary>
/// <typeparam name="T">The type of the value.</typeparam>
/// <remarks>
///     <para>
///         Generic, and the router handles that correctly for a reason worth knowing: handlers are
///         matched on the event's exact type, and <c>ValueChangedEvent&lt;float&gt;</c> and
///         <c>ValueChangedEvent&lt;string&gt;</c> are two types. So a panel can listen for every
///         slider inside it without hearing from the text boxes, with no filtering of its own.
///     </para>
///     <para>
///         ⚠ <b>Raised after the value has changed</b>, and only when it actually did.
///         <see cref="Previous" /> is carried because the common thing to do with a change is to
///         undo it, and a handler that has to have remembered the old value cannot be written on a
///         control it did not create.
///     </para>
/// </remarks>
public sealed class ValueChangedEvent<T> : UiEvent {
    /// <summary>What it was.</summary>
    public T? Previous { get; init; }

    /// <summary>What it is.</summary>
    public T? Value { get; init; }
}

/// <summary>Something that opens and closes did one of them.</summary>
/// <remarks>
///     Distinct from <c>ValueChangedEvent&lt;bool&gt;</c> rather than an alias for it, because a
///     dialog opening and a checkbox being ticked are both a <c>bool</c> and nothing that listens
///     for one wants the other. The type is the filter.
/// </remarks>
public sealed class OpenChangedEvent : UiEvent {
    /// <summary>Whether it is now open.</summary>
    public bool IsOpen { get; init; }
}

/// <summary>Why something that opens and closes was closed.</summary>
/// <remarks>
///     Carried because the three are not interchangeable to a caller: a dialog dismissed with Escape
///     is a cancellation, one closed by its own button is a decision, and one closed because the
///     thing it was about went away is neither.
/// </remarks>
public enum CloseReason : byte {
    /// <summary>Code closed it.</summary>
    Code,

    /// <summary>Escape.</summary>
    Cancelled,

    /// <summary>A click outside it.</summary>
    LightDismissed,

    /// <summary>A control inside it that closes it — an item chosen from a menu, a dialog's button.</summary>
    Committed
}
