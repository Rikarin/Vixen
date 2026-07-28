// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Memory;

namespace Vixen.Vfx;

/// <summary>
///     One system's particles, one array per attribute.
/// </summary>
/// <remarks>
///     <para>
///         <b>Struct of arrays, because every operation is a sweep.</b> Gravity reads and writes
///         velocity and touches nothing else; ageing touches age and lifetime. An array of particle
///         structs would pull a whole particle into cache to change four bytes of it, and would
///         defeat the vectoriser on every one of them. This is the same argument the ECS makes for
///         chunk storage and the renderer makes for <c>RenderDataHolder</c>, one level further down.
///     </para>
///     <para>
///         <b>An attribute a graph does not declare has no memory at all.</b> Not a zeroed array, not
///         a null check on every access — no allocation. A graph that never rotates its particles is
///         a graph whose rotation array is <see cref="NativeArray{T}.Empty" />, and the operations
///         that would have read it are not in the compiled list to begin with.
///     </para>
///     <para>
///         <b>The alive set is a prefix, not a mask.</b> Particles live in [0, <see cref="Count" />)
///         and a dead one is removed by copying the last live particle over it. That keeps every
///         sweep a dense loop with no branch per particle, at the cost of the order changing as
///         particles die — which nothing here promises and only a depth sort would care about, and a
///         depth sort re-orders anyway.
///     </para>
/// </remarks>
public sealed class ParticleBuffer : IDisposable {
    NativeArray<Vector3> position;
    NativeArray<Vector3> velocity;
    NativeArray<float> size;
    NativeArray<Vector4> colour;
    NativeArray<float> lifetime;
    NativeArray<float> age;
    NativeArray<float> rotation;
    NativeArray<float> angularVelocity;
    NativeArray<uint> identifier;

    bool disposed;

    /// <summary>Allocates storage for a declared set of attributes.</summary>
    /// <param name="attributes">Which attributes the graph uses. <see cref="VfxAttribute.Identifier" /> is added.</param>
    /// <param name="capacity">The most particles that can be alive at once.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity" /> is not positive.</exception>
    public ParticleBuffer(VfxAttribute attributes, int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        // Always present, whatever the graph said. Every random value a particle is given is a
        // function of it, so a system without it would re-roll a particle's randomness whenever
        // anything ahead of it died.
        Attributes = attributes | VfxAttribute.Identifier;
        Capacity = capacity;

        if (Has(VfxAttribute.Position)) {
            position = NativeArray<Vector3>.Zeroed(capacity, name: "Vfx.Position");
        }

        if (Has(VfxAttribute.Velocity)) {
            velocity = NativeArray<Vector3>.Zeroed(capacity, name: "Vfx.Velocity");
        }

        if (Has(VfxAttribute.Size)) {
            size = NativeArray<float>.Zeroed(capacity, name: "Vfx.Size");
        }

        if (Has(VfxAttribute.Colour)) {
            colour = NativeArray<Vector4>.Zeroed(capacity, name: "Vfx.Colour");
        }

        if (Has(VfxAttribute.Lifetime)) {
            lifetime = NativeArray<float>.Zeroed(capacity, name: "Vfx.Lifetime");
        }

        if (Has(VfxAttribute.Age)) {
            age = NativeArray<float>.Zeroed(capacity, name: "Vfx.Age");
        }

        if (Has(VfxAttribute.Rotation)) {
            rotation = NativeArray<float>.Zeroed(capacity, name: "Vfx.Rotation");
        }

        if (Has(VfxAttribute.AngularVelocity)) {
            angularVelocity = NativeArray<float>.Zeroed(capacity, name: "Vfx.AngularVelocity");
        }

        identifier = NativeArray<uint>.Zeroed(capacity, name: "Vfx.Identifier");
    }

    /// <summary>Which attributes have storage.</summary>
    public VfxAttribute Attributes { get; }

    /// <summary>The most particles that can be alive at once.</summary>
    public int Capacity { get; }

    /// <summary>How many are alive. They occupy [0, <see cref="Count" />).</summary>
    public int Count { get; private set; }

    /// <summary>How many more could be spawned before the buffer is full.</summary>
    public int Free => Capacity - Count;

    /// <summary>How many particles have ever been spawned, which is where the next identifier comes from.</summary>
    /// <remarks>
    ///     Monotonic and never reset while the system lives, so no two particles alive at the same
    ///     time — or at different times — share an identifier until it wraps at four billion.
    /// </remarks>
    public uint NextIdentifier { get; private set; }

    /// <summary>Whether an attribute has storage.</summary>
    /// <param name="attribute">The attribute.</param>
    /// <returns><see langword="true" /> if it does.</returns>
    public bool Has(VfxAttribute attribute) => (Attributes & attribute) != 0;

    /// <summary>Where the particles are.</summary>
    public Span<Vector3> Position => position.AsSpan();

    /// <summary>How they are moving.</summary>
    public Span<Vector3> Velocity => velocity.AsSpan();

    /// <summary>How big they are.</summary>
    public Span<float> Size => size.AsSpan();

    /// <summary>What colour they are.</summary>
    public Span<Vector4> Colour => colour.AsSpan();

    /// <summary>How long they live.</summary>
    public Span<float> Lifetime => lifetime.AsSpan();

    /// <summary>How long they have lived.</summary>
    public Span<float> Age => age.AsSpan();

    /// <summary>Their roll.</summary>
    public Span<float> Rotation => rotation.AsSpan();

    /// <summary>How fast that roll is changing.</summary>
    public Span<float> AngularVelocity => angularVelocity.AsSpan();

    /// <summary>Their identifiers.</summary>
    public Span<uint> Identifier => identifier.AsSpan();

    /// <summary>Adds particles, and says where they landed.</summary>
    /// <param name="count">How many to add. More than <see cref="Free" /> adds what fits.</param>
    /// <param name="first">The index of the first one added.</param>
    /// <returns>How many were actually added.</returns>
    /// <remarks>
    ///     <b>Refusing is the whole capacity policy.</b> A system asked for more particles than it has
    ///     room for gets the ones that fit and no warning, because the alternative is either a
    ///     reallocation in the middle of a frame or an effect that stops emitting at a threshold
    ///     nobody chose. The capacity is the budget, and it is the author's to set.
    /// </remarks>
    public int Spawn(int count, out int first) {
        first = Count;
        var added = Math.Clamp(count, 0, Free);

        var identifiers = Identifier;

        for (var index = 0; index < added; index++) {
            identifiers[first + index] = NextIdentifier++;
        }

        Count += added;

        return added;
    }

    /// <summary>Removes a particle by moving the last live one into its place.</summary>
    /// <param name="index">The particle to remove.</param>
    /// <remarks>
    ///     The caller is sweeping and must not advance past the index it just wrote to: whatever was
    ///     moved into it has not been looked at yet. <see cref="Reap" /> is the version that gets that
    ///     right.
    /// </remarks>
    public void RemoveAt(int index) {
        var last = Count - 1;

        if (index != last) {
            CopyParticle(last, index);
        }

        Count = last;
    }

    /// <summary>Removes every particle whose age has reached its lifetime.</summary>
    /// <returns>How many died.</returns>
    /// <remarks>
    ///     A single backward-safe sweep rather than a compaction pass with a live mask. Because
    ///     removal fills the hole from the end, the index is only advanced when the particle at it
    ///     survived — which is the whole subtlety of swap-removal and the reason it lives here rather
    ///     than in every caller.
    /// </remarks>
    public int Reap() {
        if (!Has(VfxAttribute.Age) || !Has(VfxAttribute.Lifetime)) {
            return 0;
        }

        var ages = Age;
        var lifetimes = Lifetime;
        var died = 0;

        for (var index = 0; index < Count;) {
            if (ages[index] < lifetimes[index]) {
                index++;

                continue;
            }

            RemoveAt(index);
            died++;
        }

        return died;
    }

    /// <summary>Removes every particle, keeping the storage.</summary>
    public void Clear() => Count = 0;

    /// <summary>Frees the storage.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        position.Dispose();
        velocity.Dispose();
        size.Dispose();
        colour.Dispose();
        lifetime.Dispose();
        age.Dispose();
        rotation.Dispose();
        angularVelocity.Dispose();
        identifier.Dispose();
    }

    void CopyParticle(int from, int to) {
        if (Has(VfxAttribute.Position)) {
            position[to] = position[from];
        }

        if (Has(VfxAttribute.Velocity)) {
            velocity[to] = velocity[from];
        }

        if (Has(VfxAttribute.Size)) {
            size[to] = size[from];
        }

        if (Has(VfxAttribute.Colour)) {
            colour[to] = colour[from];
        }

        if (Has(VfxAttribute.Lifetime)) {
            lifetime[to] = lifetime[from];
        }

        if (Has(VfxAttribute.Age)) {
            age[to] = age[from];
        }

        if (Has(VfxAttribute.Rotation)) {
            rotation[to] = rotation[from];
        }

        if (Has(VfxAttribute.AngularVelocity)) {
            angularVelocity[to] = angularVelocity[from];
        }

        identifier[to] = identifier[from];
    }
}
