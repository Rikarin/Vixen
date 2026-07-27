// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation;

/// <summary>Something that happens at a moment in a clip: a footstep, a hit window, a spawn.</summary>
/// <param name="Name">What the event is. Game code matches on this.</param>
/// <param name="Time">When it happens, in seconds from the clip's start.</param>
/// <param name="Float">A number for whoever handles it — a volume, a radius.</param>
/// <param name="Int">An integer for whoever handles it — which foot, which socket.</param>
/// <param name="String">A string for whoever handles it — an asset name.</param>
/// <remarks>
///     <para>
///         <b>A name and three payload slots, not a delegate.</b> An event authored in a clip is
///         content: it is edited by whoever animates, it is serialised with the clip, and it has to
///         survive the method it once called being renamed. A game reads the name and decides; the
///         animation system never invokes anything.
///     </para>
///     <para>
///         The three payloads are Unity's, and for Unity's reason: they cover essentially every
///         event anyone authors, and the alternative — a general property bag — costs a dictionary
///         lookup and an allocation per event on a system that fires several per character per
///         second.
///     </para>
/// </remarks>
public readonly record struct AnimationEvent(
    string Name,
    float Time,
    float Float = 0f,
    int Int = 0,
    string String = ""
);

/// <summary>An event that actually fired this frame, and what fired it.</summary>
/// <param name="Event">The authored event.</param>
/// <param name="Layer">Which layer it came from.</param>
/// <param name="State">The state that was playing it.</param>
/// <param name="Weight">
///     How much that state was contributing when it fired, in <c>[0, 1]</c>.
/// </param>
/// <remarks>
///     The weight is what stops a crossfade firing two footsteps. Both clips cross their footstep
///     key during the blend and both events are real; the one at 5 % is not a step anybody heard,
///     and only the handler knows what its own threshold is. Reporting the weight and letting the
///     game filter is the only version of this that is not wrong for somebody.
/// </remarks>
public readonly record struct FiredAnimationEvent(
    AnimationEvent Event,
    int Layer,
    string State,
    float Weight
);

/// <summary>Where a frame's events are collected.</summary>
/// <remarks>
///     <para>
///         A buffer rather than a callback, because an event fires in the middle of evaluating a
///         blend tree — a point at which the pose is half-built, the layer stack is mid-flight, and
///         a handler that reacted by changing a parameter or destroying the entity would be doing it
///         to a system that is iterating itself. Draining the buffer after the pose is finished is
///         the only ordering that lets a handler do the obvious thing.
///     </para>
///     <para>
///         The list is reused across frames — <see cref="Clear" /> keeps its capacity — so a
///         character that fires two events a second settles at zero allocations after the first one.
///     </para>
/// </remarks>
public sealed class AnimationEventBuffer {
    readonly List<FiredAnimationEvent> events = [];

    /// <summary>How many events this frame produced.</summary>
    public int Count => events.Count;

    /// <summary>The events, in the order they were emitted.</summary>
    /// <param name="index">Which one.</param>
    /// <returns>The event.</returns>
    public FiredAnimationEvent this[int index] => events[index];

    /// <summary>Records an event.</summary>
    /// <param name="fired">The event.</param>
    public void Add(in FiredAnimationEvent fired) => events.Add(fired);

    /// <summary>Empties the buffer, keeping its capacity.</summary>
    public void Clear() => events.Clear();

    /// <summary>Walks this frame's events.</summary>
    /// <returns>The enumerator.</returns>
    public List<FiredAnimationEvent>.Enumerator GetEnumerator() => events.GetEnumerator();
}
