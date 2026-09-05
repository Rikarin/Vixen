// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Composition;

/// <summary>How long a subtree stays in the document after the thing that built it stopped wanting it.</summary>
/// <param name="Duration">How long it is kept, measured on <see cref="UiDocument.Now" />.</param>
/// <param name="Class">The class added to its top-level elements while it is on its way out.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The entering half of an appearance animation is nearly free and the leaving half is
///         the whole problem.</b> An element that arrives can animate from a class the cascade
///         applies on its first frame, because it is in the document to be cascaded over. An element
///         that leaves has to still be in the document while it does — so <c>Region.Clear</c>, which
///         removes synchronously and is what every <c>@for</c> row and <c>@if</c> arm ends through,
///         has to be able to say <i>not yet</i>, and something has to own the interval. That is what
///         this is: the interval, and the name of the class the author writes the transition against.
///     </para>
///     <para>
///         ⚠ <b>A duration rather than "until the animator stops", and that is a decision.</b> The
///         obvious alternative is to add the class, let the cascade resolve, and hold the elements
///         until <c>Animator</c> reports nothing running on them. It reads better and it is the
///         wrong instrument: nothing has cascaded at the moment a row leaves, so the answer on the
///         first frame is "nothing is running" for a row whose transition is about to start and for
///         a row that has none, and the two are indistinguishable. Waiting a frame to tell them
///         apart makes the removal depend on when a style pass happened to run, which is exactly
///         the class of test this repository has spent the most time de-flaking. A number the author
///         states is deterministic, is the same number they already wrote in the stylesheet, and is
///         asserted against frame counts driven by the test's own clock.
///     </para>
///     <para>
///         <b>What it costs</b> is that the number is written twice — once in
///         <c>transition: opacity 200ms</c> and once here — and the failure when they disagree is
///         benign in one direction (the row is removed mid-fade) and invisible in the other (the row
///         sits at its final appearance for the remainder). Neither is a wrong picture that looks
///         right, which is the bar.
///     </para>
/// </remarks>
public sealed record ExitSpec(TimeSpan Duration, string Class = "leaving");

/// <summary>One region on its way out, and the moment it stops being in the document.</summary>
/// <remarks>
///     ⚠ <b><see cref="Done" /> rather than removal from the document's list.</b> The two things that
///     end an exit — its interval running out, and the region being cleared outright because whatever
///     built it went away — reach it from opposite directions, and only one of them is walking the
///     list. A flag the walk skips means the other one never has to find its entry.
/// </remarks>
sealed class RegionExit {
    /// <summary>What is leaving.</summary>
    internal required Region Region { get; init; }

    /// <summary>The document time at or after which it stops being in the document.</summary>
    internal required TimeSpan Deadline { get; init; }

    /// <summary>What to tell the construct that owns the region once it has gone.</summary>
    internal required Action Finished { get; init; }

    /// <summary>Whether this exit has already been settled, one way or the other.</summary>
    internal bool Done { get; set; }
}
