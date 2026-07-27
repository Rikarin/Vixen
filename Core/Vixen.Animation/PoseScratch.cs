// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation;

/// <summary>
///     The temporary poses a blend needs, rented and returned instead of allocated.
/// </summary>
/// <remarks>
///     <para>
///         Evaluating a tree of motions needs a buffer per child being blended, and the depth of the
///         tree decides how many are alive at once. Allocating them is a hundred joints × forty
///         bytes × however many children, every frame, per character — which is the shape of garbage
///         that does not show up in a profile as one thing and does show up as a collection pause.
///     </para>
///     <para>
///         A stack rather than a general pool, because the lifetimes are strictly nested: a node
///         rents what it needs, evaluates its children, blends, and returns. <see cref="Rent" />
///         hands out a buffer and <see cref="Lease.Dispose" /> puts it back, so <c>using</c> is what
///         enforces the nesting and a node that forgets is a leak of one array rather than a
///         corrupted pose.
///     </para>
///     <para>
///         Not thread-safe, and deliberately so: one of these belongs to one animator, and two
///         characters animating in parallel have one each. Sharing would need a lock in the
///         innermost loop of the system to save an array per character.
///     </para>
/// </remarks>
public sealed class PoseScratch {
    readonly List<BoneTransform[]> buffers = [];
    int depth;

    /// <summary>Creates a pool for poses of a given skeleton.</summary>
    /// <param name="jointCount">How many joints a pose holds.</param>
    public PoseScratch(int jointCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(jointCount);
        JointCount = jointCount;
    }

    /// <summary>How many joints each buffer holds.</summary>
    public int JointCount { get; }

    /// <summary>How many buffers have ever been needed at once.</summary>
    /// <remarks>
    ///     The high-water mark, which is the number of arrays this will ever hold. Worth looking at
    ///     in a diagnostic: it is the depth of the deepest blend a character actually evaluated.
    /// </remarks>
    public int Capacity => buffers.Count;

    /// <summary>Takes a buffer.</summary>
    /// <returns>The lease. Dispose it — with <c>using</c> — to give the buffer back.</returns>
    public Lease Rent() {
        if (depth == buffers.Count) {
            buffers.Add(new BoneTransform[JointCount]);
        }

        return new(this, buffers[depth++]);
    }

    void Return() => depth--;

    /// <summary>A rented buffer, returned when it is disposed.</summary>
    public readonly ref struct Lease {
        readonly PoseScratch owner;
        readonly BoneTransform[] buffer;

        internal Lease(PoseScratch owner, BoneTransform[] buffer) {
            this.owner = owner;
            this.buffer = buffer;
        }

        /// <summary>The pose.</summary>
        public Span<BoneTransform> Pose => buffer;

        /// <summary>Gives the buffer back.</summary>
        public void Dispose() => owner.Return();
    }
}
