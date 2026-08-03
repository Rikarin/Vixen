// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;

namespace Vixen.Platform;

/// <summary>
///     Turns whatever a mobile platform calls a finger into a small, stable integer, and works out
///     how far it moved.
/// </summary>
/// <remarks>
///     <para>
///         <b>Both mobile platforms need this and neither provides it.</b> UIKit identifies a touch
///         by the address of a <c>UITouch</c> object, which is reused after the touch ends; Android
///         identifies one by a pointer id that is stable for a gesture and reused afterwards, and
///         exposes it by index into a batched event whose indices renumber as fingers leave. Neither
///         is a number an application should ever see, and both need the same bookkeeping to become
///         one: allocate on down, look up on move, release on up.
///     </para>
///     <para>
///         <b>Small ids, reused lowest-first.</b> <see cref="PlatformEvent.DeviceId" /> is the finger,
///         and code downstream indexes arrays by it — a UITouch pointer would not do, and neither
///         would a monotonically increasing counter after an hour of play. So the tracker hands out
///         the lowest free id and takes it back when the finger lifts.
///     </para>
///     <para>
///         <b>The delta is computed here for the same reason.</b> Android's <c>MotionEvent</c> gives
///         a position and no delta; UIKit gives the previous location only while the touch object is
///         alive. Deriving it from the last position this tracker saw gives one definition of
///         "moved" across both, and it is the definition a gesture recogniser upstream needs.
///     </para>
///     <para>
///         Not thread-safe, and does not need to be: touches arrive on the platform's own thread,
///         which is the thread that owns the platform.
///     </para>
/// </remarks>
public sealed class TouchTracker {
    /// <summary>How many fingers may be tracked at once.</summary>
    /// <remarks>
    ///     Ten. Both platforms stop reporting somewhere below this — iOS at five on a phone, eleven
    ///     on an iPad — and a device that reported more would be reporting palm contact, which is
    ///     not input.
    /// </remarks>
    public const int MaximumTouches = 10;

    readonly Dictionary<long, Touch> active = [];
    readonly bool[] taken = new bool[MaximumTouches];

    /// <summary>How many fingers are down.</summary>
    public int Count => active.Count;

    /// <summary>Starts tracking a finger.</summary>
    /// <param name="key">The platform's own identifier — a <c>UITouch</c> handle, a pointer id.</param>
    /// <param name="position">Where it went down, in logical points.</param>
    /// <param name="finger">The small id to report it as.</param>
    /// <returns>
    ///     <see langword="false" /> if this key is already down, or if <see cref="MaximumTouches" />
    ///     are already being tracked.
    /// </returns>
    /// <remarks>
    ///     A repeated key is refused rather than re-allocated. It means the platform sent a second
    ///     down without an up — which happens after a gesture recogniser swallows the middle of a
    ///     sequence — and quietly allocating a second id for one finger would leave the first
    ///     stuck down forever.
    /// </remarks>
    public bool TryBegin(long key, Vector2 position, out int finger) {
        finger = -1;

        if (active.ContainsKey(key)) {
            return false;
        }

        for (var candidate = 0; candidate < taken.Length; candidate++) {
            if (taken[candidate]) {
                continue;
            }

            taken[candidate] = true;
            active[key] = new(candidate, position);
            finger = candidate;
            return true;
        }

        return false;
    }

    /// <summary>Records a finger moving.</summary>
    /// <param name="key">The platform's identifier.</param>
    /// <param name="position">Where it is now, in logical points.</param>
    /// <param name="finger">The id it was given.</param>
    /// <param name="delta">How far it moved since the last report.</param>
    /// <returns><see langword="false" /> if this key is not being tracked.</returns>
    public bool TryMove(long key, Vector2 position, out int finger, out Vector2 delta) {
        if (!active.TryGetValue(key, out var touch)) {
            finger = -1;
            delta = default;
            return false;
        }

        finger = touch.Finger;
        delta = position - touch.Position;
        active[key] = touch with { Position = position };
        return true;
    }

    /// <summary>Stops tracking a finger and releases its id.</summary>
    /// <param name="key">The platform's identifier.</param>
    /// <param name="finger">The id it had.</param>
    /// <returns><see langword="false" /> if this key was not being tracked.</returns>
    public bool TryEnd(long key, out int finger) {
        if (!active.Remove(key, out var touch)) {
            finger = -1;
            return false;
        }

        finger = touch.Finger;
        taken[touch.Finger] = false;
        return true;
    }

    /// <summary>Where a tracked finger was last seen.</summary>
    /// <param name="key">The platform's identifier.</param>
    /// <param name="position">The position.</param>
    /// <returns><see langword="false" /> if this key is not being tracked.</returns>
    public bool TryGetPosition(long key, [NotNullWhen(true)] out Vector2? position) {
        if (active.TryGetValue(key, out var touch)) {
            position = touch.Position;
            return true;
        }

        position = null;
        return false;
    }

    /// <summary>Releases every finger and returns the ids that were down.</summary>
    /// <returns>The ids, ascending.</returns>
    /// <remarks>
    ///     What a platform calls when it is told the touch sequence was cancelled — an incoming
    ///     call, a system gesture, the application going to the background mid-drag. The caller
    ///     turns each into a <see cref="PlatformEventKind.TouchUp" />, because an application that
    ///     is never told a finger lifted keeps drawing the line it was dragging.
    /// </remarks>
    public IReadOnlyList<int> Clear() {
        var fingers = active.Values.Select(touch => touch.Finger).Order().ToArray();

        active.Clear();
        Array.Clear(taken);

        return fingers;
    }

    readonly record struct Touch(int Finger, Vector2 Position);
}
