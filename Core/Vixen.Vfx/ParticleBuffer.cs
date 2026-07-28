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

    readonly NativeArray<float>[] customs;
    readonly int[] lanes;

    bool disposed;

    /// <summary>Allocates storage for a declared set of attributes.</summary>
    /// <param name="attributes">Which attributes the graph uses. <see cref="VfxAttribute.Identifier" /> is added.</param>
    /// <param name="capacity">The most particles that can be alive at once.</param>
    /// <param name="declared">The graph's custom attributes, in slot order.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity" /> is not positive.</exception>
    public ParticleBuffer(VfxAttribute attributes, int capacity, ReadOnlySpan<VfxCustomAttribute> declared = default) {
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

        // One array per declared slot, each a flat run of lanes. Flat rather than typed because the
        // type is the graph's to know and the storage's job is to be a place — every sweep that
        // touches one already knows how many lanes it is reading.
        customs = new NativeArray<float>[declared.Length];
        lanes = new int[declared.Length];

        for (var slot = 0; slot < declared.Length; slot++) {
            lanes[slot] = VfxAttributes.Lanes(declared[slot].Type);
            customs[slot] = NativeArray<float>.Zeroed(capacity * lanes[slot], name: "Vfx." + declared[slot].Name);
        }
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

    /// <summary>How many custom attributes this buffer has storage for.</summary>
    public int CustomCount => customs.Length;

    /// <summary>How many floats one particle occupies in a custom slot.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>Its lane count.</returns>
    public int Lanes(int slot) => lanes[slot];

    /// <summary>One custom attribute's storage, as a flat run of lanes.</summary>
    /// <param name="slot">The slot, as the graph assigned it.</param>
    /// <returns>The storage. Particle <c>i</c>'s lane <c>l</c> is at <c>i * Lanes(slot) + l</c>.</returns>
    /// <remarks>
    ///     Flat rather than a span of vectors, because the lane count is a property of the graph and
    ///     not of the type system here. A caller that knows its slot is a <c>Float3</c> can index it
    ///     three at a time; one that does not can copy it without caring.
    /// </remarks>
    public Span<float> Custom(int slot) => customs[slot].AsSpan();

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
    ///     moved into it has not been looked at yet. <see cref="Reap()" /> is the version that gets that
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
    public int Reap() => Reap(default);

    /// <summary>Removes every particle whose age has reached its lifetime, noting where they were.</summary>
    /// <param name="graveyard">
    ///     Filled with the position of each particle that died, in the order they were found. Pass an
    ///     empty span — which is what <see cref="Reap()" /> does — to skip recording. A span shorter
    ///     than the number that died keeps the first that fit.
    /// </param>
    /// <returns>How many died, whether or not they fitted in <paramref name="graveyard" />.</returns>
    /// <remarks>
    ///     The positions are recorded rather than the indices, because by the time anyone could look
    ///     at an index the particle that had it is gone — swap-removal has already put a survivor
    ///     there. A sub-emitter wants where the particle was, and that is the one thing the index
    ///     would no longer answer.
    /// </remarks>
    public int Reap(Span<Vector3> graveyard) {
        if (!Has(VfxAttribute.Age) || !Has(VfxAttribute.Lifetime)) {
            return 0;
        }

        var ages = Age;
        var lifetimes = Lifetime;
        var positions = Position;
        var record = !graveyard.IsEmpty && Has(VfxAttribute.Position);
        var died = 0;

        for (var index = 0; index < Count;) {
            if (ages[index] < lifetimes[index]) {
                index++;

                continue;
            }

            if (record && died < graveyard.Length) {
                graveyard[died] = positions[index];
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

        foreach (var custom in customs) {
            custom.Dispose();
        }
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

        // A custom attribute travels with its particle for the same reason every built-in does: a
        // swap-removal that left one behind would give the particle moved into the hole somebody
        // else's value, and nothing about it would look wrong.
        for (var slot = 0; slot < customs.Length; slot++) {
            var values = customs[slot].AsSpan();
            var width = lanes[slot];

            for (var lane = 0; lane < width; lane++) {
                values[(to * width) + lane] = values[(from * width) + lane];
            }
        }
    }
}
