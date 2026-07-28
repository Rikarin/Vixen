// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Platform.Web;

/// <summary>The kinds <c>vixen-platform.js</c> raises that are not
/// <see cref="PlatformEventKind" />s.</summary>
/// <remarks>
///     They ride the same ring as everything else so that ordering between them and real input is
///     preserved — a key press that happened before the tab was hidden must still be seen to have
///     happened before it — and <see cref="WebPlatform.PumpEvents" /> turns each one into the
///     lifecycle call it stands for. Values start at 200, above every <see cref="PlatformEventKind" />.
/// </remarks>
internal enum WebEventKind {
    /// <summary><c>visibilitychange</c> to hidden: the tab is no longer being composited.</summary>
    PageHidden = 200,

    /// <summary><c>visibilitychange</c> to visible.</summary>
    PageVisible = 201,

    /// <summary><c>pagehide</c>: the document is going away, or into the back/forward cache.</summary>
    PageUnloading = 202,

    /// <summary><c>freeze</c>: Chromium is about to discard the tab. The only memory signal a page
    /// gets.</summary>
    MemoryPressure = 203
}

/// <summary>One record from the drained ring, read by slot name rather than by index.</summary>
/// <remarks>
///     <para>
///         The layout is duplicated in <c>vixen-platform.js</c>, because nothing can make one side
///         derive it from the other across the language boundary. What can be done — and is — is to
///         put every read in one place, so a change to the JavaScript that this file has not
///         followed is a change to <em>this</em> file rather than a stray index somewhere in a
///         translation switch.
///     </para>
///     <para>
///         Every field is a <see cref="double" />. Coordinates are fractional on a trackpad and a
///         touchscreen, timestamps are milliseconds with sub-millisecond resolution, and key codes
///         and device ids are small integers a double holds exactly — so one array type carries all
///         of it with no tagging and no per-event allocation.
///     </para>
/// </remarks>
internal readonly ref struct WebEventRecord {
    readonly ReadOnlySpan<double> slots;

    public WebEventRecord(ReadOnlySpan<double> slots) => this.slots = slots;

    public int Kind => (int)slots[0];
    public uint WindowId => (uint)slots[1];

    /// <summary>When it happened, as <c>performance.now()</c> milliseconds.</summary>
    public double TimeStampMilliseconds => slots[2];

    public KeyModifiers Modifiers => (KeyModifiers)(int)slots[3];
    public Vector2 First => new((float)slots[4], (float)slots[5]);
    public Vector2 Second => new((float)slots[6], (float)slots[7]);
    public float Value => (float)slots[8];

    /// <summary>The string handle, for the kinds that carry text. The same slot as
    /// <see cref="Value" />, which no text-carrying kind uses.</summary>
    public int StringHandle => (int)slots[8];

    public int Code => (int)slots[9];
    public int Device => (int)slots[10];

    public Key Key => (Key)Code;
    public MouseButton MouseButton => (MouseButton)Code;
    public bool IsRepeat => Device != 0;

    /// <summary>The click count, which the DOM reports as <c>detail</c> and never as zero.</summary>
    public int ClickCount => Math.Max(1, Device);

    /// <summary>The browser's own identifier for a finger, stable for the length of the touch and
    /// reused afterwards — which is why it goes through <see cref="TouchTracker" /> rather than
    /// into an event.</summary>
    public long TouchIdentifier => (long)slots[10];
}
