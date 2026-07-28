// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Vfx;

/// <summary>How a spawner decides when to emit.</summary>
public enum VfxSpawnerKind {
    /// <summary>A number of particles at once, at a time, optionally repeating.</summary>
    Burst,

    /// <summary>A number of particles per second, continuously.</summary>
    Rate
}

/// <summary>One reason particles appear.</summary>
/// <param name="Kind">Whether it bursts or trickles.</param>
/// <param name="Rate">For <see cref="VfxSpawnerKind.Rate" />: particles per second.</param>
/// <param name="Count">For <see cref="VfxSpawnerKind.Burst" />: how many each time.</param>
/// <param name="Time">For <see cref="VfxSpawnerKind.Burst" />: when the first one is, in seconds.</param>
/// <param name="Interval">
///     For <see cref="VfxSpawnerKind.Burst" />: how often it repeats. Zero or less bursts once.
/// </param>
public readonly record struct VfxSpawner(VfxSpawnerKind Kind, float Rate = 0f, int Count = 0, float Time = 0f, float Interval = 0f) {
    /// <summary>A steady stream.</summary>
    /// <param name="perSecond">How many particles a second.</param>
    /// <returns>The spawner.</returns>
    public static VfxSpawner AtRate(float perSecond) => new(VfxSpawnerKind.Rate, perSecond);

    /// <summary>A one-off burst.</summary>
    /// <param name="count">How many.</param>
    /// <param name="time">When, in seconds after the system starts.</param>
    /// <returns>The spawner.</returns>
    public static VfxSpawner Burst(int count, float time = 0f) => new(VfxSpawnerKind.Burst, Count: count, Time: time);

    /// <summary>A burst that repeats.</summary>
    /// <param name="count">How many each time.</param>
    /// <param name="interval">How often, in seconds.</param>
    /// <param name="time">When the first one is.</param>
    /// <returns>The spawner.</returns>
    public static VfxSpawner Repeating(int count, float interval, float time = 0f) =>
        new(VfxSpawnerKind.Burst, Count: count, Time: time, Interval: interval);
}

/// <summary>
///     A particle system as the runtime executes it: what makes particles, what they start as, and
///     what happens to them.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the artefact both backends read, and it is data.</b> Not a delegate tree, which
///         a shader cannot be compiled from; not shader source, which a CPU cannot run; not an object
///         graph, which neither can be uploaded. An array of fixed-size operations can be walked by a
///         loop, emitted as shader source, written to disk, and compared for equality — and doc 06
///         says this is the part that has to be designed in rather than retrofitted, so it is the part
///         that was designed first.
///     </para>
///     <para>
///         <b>Storage is derived, not declared.</b> <see cref="Attributes" /> is the union of what the
///         operations touch, computed here. An author cannot forget to declare an attribute and cannot
///         declare one nothing uses, because there is nowhere to say either.
///     </para>
///     <para>
///         <b>Reading something nothing writes is refused at compile time.</b> An updater that reads
///         velocity in a graph whose initializers never set it would run over zeroed memory and look
///         like an effect that does nothing — the worst kind of failure, because it has no symptom to
///         search for. <see cref="Compile" /> says so instead.
///     </para>
/// </remarks>
public sealed class VfxCompiledGraph {
    VfxCompiledGraph(VfxSpawner[] spawners, VfxOperation[] initializers, VfxOperation[] updaters, VfxAttribute attributes, int capacity) {
        Spawners = spawners;
        Initializers = initializers;
        Updaters = updaters;
        Attributes = attributes;
        Capacity = capacity;
    }

    /// <summary>What makes particles.</summary>
    public VfxSpawner[] Spawners { get; }

    /// <summary>What a particle starts as, applied once to each new one.</summary>
    public VfxOperation[] Initializers { get; }

    /// <summary>What happens to it, applied to every live particle every step.</summary>
    public VfxOperation[] Updaters { get; }

    /// <summary>Every attribute the graph touches. Storage is allocated for exactly these.</summary>
    public VfxAttribute Attributes { get; }

    /// <summary>The most particles that may be alive at once.</summary>
    public int Capacity { get; }

    /// <summary>How many bytes one particle of this graph occupies.</summary>
    public int BytesPerParticle => VfxAttributes.BytesPerParticle(Attributes);

    /// <summary>Checks a graph and works out what it needs.</summary>
    /// <param name="spawners">What makes particles.</param>
    /// <param name="initializers">What a new particle starts as.</param>
    /// <param name="updaters">What happens to a live particle each step.</param>
    /// <param name="capacity">The most particles alive at once.</param>
    /// <returns>The compiled graph.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity" /> is not positive.</exception>
    /// <exception cref="ArgumentException">An updater reads an attribute nothing gives it.</exception>
    /// <remarks>
    ///     Salts are assigned here, from each operation's position in the two lists, so an author never
    ///     writes one and two operations can never share one. An operation that arrives with a salt
    ///     already set keeps it, which is what lets a golden test pin an exact effect.
    /// </remarks>
    public static VfxCompiledGraph Compile(
        ReadOnlySpan<VfxSpawner> spawners,
        ReadOnlySpan<VfxOperation> initializers,
        ReadOnlySpan<VfxOperation> updaters,
        int capacity
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        var initialized = Salt(initializers, 1);
        var updated = Salt(updaters, 1 + (uint)initializers.Length);

        var written = VfxAttribute.None;
        var attributes = VfxAttribute.None;

        foreach (var operation in initialized) {
            written |= VfxOpcodes.Writes(operation.Opcode);
            attributes |= VfxOpcodes.Writes(operation.Opcode) | VfxOpcodes.Reads(operation.Opcode);
        }

        // Age is the runtime's own, advanced every step whenever there is a lifetime to compare it
        // against — so an updater may read it without any initializer having written it.
        if ((written & VfxAttribute.Lifetime) != 0) {
            written |= VfxAttribute.Age;
            attributes |= VfxAttribute.Age;
        }

        foreach (var operation in updated) {
            var reads = VfxOpcodes.Reads(operation.Opcode);
            var missing = reads & ~written;

            if (missing != VfxAttribute.None) {
                throw new ArgumentException(
                    $"The updater `{operation.Opcode}` reads {missing}, which no initializer writes. It would run over "
                    + "zeroed memory and produce an effect that looks broken rather than one that fails.",
                    nameof(updaters)
                );
            }

            written |= VfxOpcodes.Writes(operation.Opcode);
            attributes |= reads | VfxOpcodes.Writes(operation.Opcode);
        }

        return new(spawners.ToArray(), initialized, updated, attributes | VfxAttribute.Identifier, capacity);
    }

    /// <summary>Gives every operation without a salt one derived from where it sits.</summary>
    static VfxOperation[] Salt(ReadOnlySpan<VfxOperation> operations, uint first) {
        var salted = operations.ToArray();

        // Three apart, because the widest random operation here draws three values from consecutive
        // salts — a position in a box. Two operations sharing a salt would correlate every value they
        // drew, which reads as a pattern in the effect rather than as a bug in the graph.
        const uint Stride = 4;

        for (var index = 0; index < salted.Length; index++) {
            if (salted[index].Salt == 0 && VfxOpcodes.IsRandom(salted[index].Opcode)) {
                salted[index] = salted[index] with { Salt = first + ((uint)index * Stride) };
            }
        }

        return salted;
    }
}
