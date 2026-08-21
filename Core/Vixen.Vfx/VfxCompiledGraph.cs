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
    VfxCompiledGraph(
        VfxSpawner[] spawners,
        VfxOperation[] initializers,
        VfxOperation[] updaters,
        VfxRenderer? renderer,
        VfxAttribute attributes,
        VfxCustomAttribute[] customs,
        int capacity
    ) {
        Spawners = spawners;
        Initializers = initializers;
        Updaters = updaters;
        Renderer = renderer;
        Attributes = attributes;
        Customs = customs;
        Capacity = capacity;
    }

    /// <summary>What makes particles.</summary>
    public VfxSpawner[] Spawners { get; }

    /// <summary>What a particle starts as, applied once to each new one.</summary>
    public VfxOperation[] Initializers { get; }

    /// <summary>What happens to it, applied to every live particle every step.</summary>
    public VfxOperation[] Updaters { get; }

    /// <summary>How it is drawn, or <see langword="null" /> if nothing draws it.</summary>
    /// <remarks>
    ///     Optional, because a graph that is never drawn should not pay for the attributes drawing
    ///     would read. A simulation used to drive something else — a physics probe, a test, an
    ///     effect whose particles are consumed rather than seen — has no renderer and no colour and
    ///     no size, and that is the same rule as everywhere else here: storage is what is used.
    /// </remarks>
    public VfxRenderer? Renderer { get; }

    /// <summary>Every attribute the graph touches. Storage is allocated for exactly these.</summary>
    public VfxAttribute Attributes { get; }

    /// <summary>The attributes the graph declared for itself, in slot order.</summary>
    /// <remarks>
    ///     Slot order <i>is</i> declaration order: an operation's <see cref="VfxOperation.Slot" /> is
    ///     an index into this, and the emitted shader declares its buffers in the same sequence. That
    ///     is what makes both backends agree without either of them looking a name up.
    /// </remarks>
    public VfxCustomAttribute[] Customs { get; }

    /// <summary>The slot a name was given, or -1 if the graph does not declare it.</summary>
    /// <param name="name">The attribute's name.</param>
    /// <returns>Its slot.</returns>
    /// <remarks>
    ///     For a caller writing the graph and for a host binding its buffers. Nothing inside the
    ///     simulation calls this: an operation carries the slot it was compiled with, because a name
    ///     lookup per particle per frame is exactly the cost the slot exists to avoid.
    /// </remarks>
    public int SlotOf(string name) {
        for (var slot = 0; slot < Customs.Length; slot++) {
            if (string.Equals(Customs[slot].Name, name, StringComparison.Ordinal)) {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>The most particles that may be alive at once.</summary>
    public int Capacity { get; }

    /// <summary>How many bytes one particle of this graph occupies.</summary>
    public int BytesPerParticle => VfxAttributes.BytesPerParticle(Attributes);

    /// <summary>Checks a graph and works out what it needs.</summary>
    /// <param name="spawners">What makes particles.</param>
    /// <param name="initializers">What a new particle starts as.</param>
    /// <param name="updaters">What happens to a live particle each step.</param>
    /// <param name="capacity">The most particles alive at once.</param>
    /// <param name="renderer">How it is drawn. Its reads join the derived attribute set.</param>
    /// <param name="customs">
    ///     The attributes the graph declares for itself. Slots are assigned in this order, and an
    ///     operation's <see cref="VfxOperation.Slot" /> is an index into it.
    /// </param>
    /// <returns>The compiled graph.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity" /> is not positive.</exception>
    /// <exception cref="ArgumentException">
    ///     An updater reads an attribute nothing gives it, a custom attribute's name is not an
    ///     identifier or is declared twice, or an operation names a slot the graph does not have.
    /// </exception>
    /// <remarks>
    ///     Salts are assigned here, from each operation's position in the two lists, so an author never
    ///     writes one and two operations can never share one. An operation that arrives with a salt
    ///     already set keeps it, which is what lets a golden test pin an exact effect.
    /// </remarks>
    public static VfxCompiledGraph Compile(
        ReadOnlySpan<VfxSpawner> spawners,
        ReadOnlySpan<VfxOperation> initializers,
        ReadOnlySpan<VfxOperation> updaters,
        int capacity,
        VfxRenderer? renderer = null,
        ReadOnlySpan<VfxCustomAttribute> customs = default
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        var declared = Declare(customs);
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

        // The renderer declares what it reads the same way an operation does, so a velocity-aligned
        // billboard is what makes a graph allocate velocity even when nothing in the simulation would
        // have. Drawing is a reader of the particle state and there is no reason it should be the one
        // kind of reader that has to be accounted for by hand.
        foreach (var operation in initialized.Concat(updated)) {
            if (VfxOpcodes.IsCustom(operation.Opcode) && (uint)operation.Slot >= (uint)declared.Length) {
                throw new ArgumentException(
                    $"`{operation.Opcode}` targets custom attribute slot {operation.Slot}, and the graph declares "
                    + $"{declared.Length}. A slot is an index into the declaration list, so an operation cannot name "
                    + "one that is not there.",
                    nameof(customs)
                );
            }
        }

        return new(
            spawners.ToArray(),
            initialized,
            updated,
            renderer,
            attributes | (renderer?.Reads ?? VfxAttribute.None) | VfxAttribute.Identifier,
            declared,
            capacity
        );
    }

    /// <summary>Checks the custom declarations and fixes their slots.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The name has to be an identifier</b>, because it names a binding in the emitted
    ///         shader and a host looks it up by that name in the reflection. A custom attribute called
    ///         "particle size" would compile on the CPU and produce a shader that does not parse,
    ///         which is a failure a long way from its cause.
    ///     </para>
    ///     <para>
    ///         <b>And it has to be unique</b>, or <see cref="SlotOf" /> answers for one of two slots
    ///         and the author cannot tell which.
    ///     </para>
    ///     <para>
    ///         <b>Float lanes only.</b> A <see cref="VfxAttributeType.UInt" /> is refused: the one
    ///         unsigned attribute here is the identifier and it belongs to the runtime, and a custom
    ///         integer would need its own storage, its own interpolation rule — there is none — and its
    ///         own buffer element type in the shader. That is a design rather than a lane count.
    ///     </para>
    /// </remarks>
    static VfxCustomAttribute[] Declare(ReadOnlySpan<VfxCustomAttribute> customs) {
        var declared = customs.ToArray();

        for (var slot = 0; slot < declared.Length; slot++) {
            var name = declared[slot].Name;

            if (string.IsNullOrEmpty(name) || !(char.IsLetter(name[0]) || name[0] == '_')
                || !name.All(character => char.IsLetterOrDigit(character) || character == '_')) {
                throw new ArgumentException(
                    $"`{name}` cannot name a custom attribute: it has to be an identifier, because it names a binding "
                    + "in the emitted shader and a host binds by that name.",
                    nameof(customs)
                );
            }

            // ⚠ Reachable only since a graph could name one from the editor. The name becomes a
            // buffer in the emitted shader, in the same scope as the push constants and the helpers,
            // so `seed` or `age` produces source that does not parse — reported here, against the name
            // that was typed, rather than as a parse error in generated text nobody wrote.
            if (VfxShaderEmitter.IsReserved(name)) {
                throw new ArgumentException(
                    $"`{name}` cannot name a custom attribute: the emitted shader already declares something by that "
                    + "name. Pick another — the emitter's own names are its push constants, its built-in particle "
                    + "buffers and their compaction twins, and its helper functions.",
                    nameof(customs)
                );
            }

            if (declared[slot].Type == VfxAttributeType.UInt) {
                throw new ArgumentException(
                    $"`{name}` cannot be a UInt. Custom attributes are float lanes: the one unsigned quantity here is "
                    + "the identifier, and it belongs to the runtime.",
                    nameof(customs)
                );
            }

            for (var other = 0; other < slot; other++) {
                if (string.Equals(declared[other].Name, name, StringComparison.Ordinal)) {
                    throw new ArgumentException(
                        $"`{name}` is declared twice, as slot {other} and slot {slot}. A name has to answer for one "
                        + "slot or nothing can look it up.",
                        nameof(customs)
                    );
                }

                // ⚠ And `glow` beside `glowOut`, which are two names here and one buffer in the
                // shader: every particle buffer gets a compaction twin called `{name}Out`.
                if (string.Equals(declared[other].Name + "Out", name, StringComparison.Ordinal)
                    || string.Equals(name + "Out", declared[other].Name, StringComparison.Ordinal)) {
                    throw new ArgumentException(
                        $"`{name}` and `{declared[other].Name}` cannot both be custom attributes: the emitted shader "
                        + "gives every particle buffer a compaction twin called `{name}Out`, so one of these two would "
                        + "be declared twice.",
                        nameof(customs)
                    );
                }
            }
        }

        return declared;
    }

    /// <summary>Gives every operation without a salt one derived from where it sits.</summary>
    static VfxOperation[] Salt(ReadOnlySpan<VfxOperation> operations, uint first) {
        var salted = operations.ToArray();

        // Three apart, because the widest random operation here draws three values from consecutive
        // salts — a position in a box. Two operations sharing a salt would correlate every value they
        // drew, which reads as a pattern in the effect rather than as a bug in the graph.
        const uint Stride = 4;

        for (var index = 0; index < salted.Length; index++) {
            if (salted[index].Salt == 0 && VfxOpcodes.NeedsSalt(salted[index].Opcode)) {
                salted[index] = salted[index] with { Salt = first + ((uint)index * Stride) };
            }
        }

        return salted;
    }
}
