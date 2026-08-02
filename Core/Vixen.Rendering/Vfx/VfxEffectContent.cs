// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Vfx;

namespace Vixen.Rendering.Vfx;

/// <summary>One spawner, as a build writes it.</summary>
/// <remarks>
///     <see cref="VfxSpawner" />'s content form. The runtime type is a positional record struct, which
///     the serializer generator cannot emit for — it wants a parameterless constructor and
///     <c>init</c> members, which is what every row in this file is. See
///     <see cref="VfxEffectContent" /> for why the mirror is worth the duplication.
/// </remarks>
[DataContract("VfxSpawnerRow")]
public readonly record struct VfxSpawnerRow {
    /// <summary>An empty spawner, for the serializer.</summary>
    public VfxSpawnerRow() { }

    /// <summary>Whether it bursts or trickles.</summary>
    public VfxSpawnerKind Kind { get; init; }

    /// <summary>Particles a second, for a rate spawner.</summary>
    public float Rate { get; init; }

    /// <summary>How many each time, for a burst.</summary>
    public int Count { get; init; }

    /// <summary>When the first burst is, in seconds.</summary>
    public float Time { get; init; }

    /// <summary>How often it repeats. Zero or less bursts once.</summary>
    public float Interval { get; init; }

    /// <summary>This row as the runtime spawner.</summary>
    public VfxSpawner ToSpawner() => new(Kind, Rate, Count, Time, Interval);

    /// <summary>One runtime spawner as a row.</summary>
    /// <param name="spawner">The spawner.</param>
    /// <returns>The row.</returns>
    public static VfxSpawnerRow From(in VfxSpawner spawner) =>
        new() {
            Kind = spawner.Kind,
            Rate = spawner.Rate,
            Count = spawner.Count,
            Time = spawner.Time,
            Interval = spawner.Interval
        };
}

/// <summary>One initializer or updater, as a build writes it.</summary>
/// <remarks>
///     <b>Generic in the opcode, which is what keeps this file from growing with the instruction
///     set.</b> Every operation is an opcode, a salt, two parameter vectors and a custom-attribute
///     slot — <see cref="VfxOperation" /> is that and nothing else — so a new opcode is a new value in
///     an enum and changes nothing here. That is the same property the compiled form was given for the
///     sake of the GPU backend, paying off a second time.
/// </remarks>
[DataContract("VfxOperationRow")]
public readonly record struct VfxOperationRow {
    /// <summary>An empty operation, for the serializer.</summary>
    public VfxOperationRow() { }

    /// <summary>What it does.</summary>
    public VfxOpcode Opcode { get; init; }

    /// <summary>Which randomness it draws on.</summary>
    /// <remarks>
    ///     ⚠ <b>Written out and read back rather than reassigned on load</b>, so that an effect looks
    ///     the same in a shipped build as it did in the editor that authored it.
    ///     <c>VfxCompiledGraph.Compile</c> assigns a salt only where one is zero, which is what makes
    ///     the round trip exact — and what makes an effect whose salts were regenerated a different
    ///     effect with the same description.
    /// </remarks>
    public uint Salt { get; init; }

    /// <summary>Its first parameter vector.</summary>
    public Vector4 A { get; init; }

    /// <summary>Its second, for the operations that take two.</summary>
    public Vector4 B { get; init; }

    /// <summary>Which custom attribute it addresses, for the operations that address one.</summary>
    public int Slot { get; init; }

    /// <summary>This row as the runtime operation.</summary>
    public VfxOperation ToOperation() => new(Opcode, Salt, A, B, Slot);

    /// <summary>One runtime operation as a row.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns>The row.</returns>
    public static VfxOperationRow From(in VfxOperation operation) =>
        new() {
            Opcode = operation.Opcode,
            Salt = operation.Salt,
            A = operation.A,
            B = operation.B,
            Slot = operation.Slot
        };
}

/// <summary>How an effect is drawn, as a build writes it.</summary>
[DataContract("VfxRendererRow")]
public readonly record struct VfxRendererRow {
    /// <summary>A camera-facing billboard, for the serializer.</summary>
    public VfxRendererRow() { }

    /// <summary>Billboard, mesh, ribbon or light.</summary>
    public VfxRendererKind Kind { get; init; }

    /// <summary>Which way a billboard faces.</summary>
    public VfxBillboardAlignment Alignment { get; init; }

    /// <summary>Whether the particles are sorted before they are drawn.</summary>
    public VfxSortMode Sort { get; init; }

    /// <summary>The axis a fixed alignment locks to.</summary>
    public Vector3 Axis { get; init; }

    /// <summary>How much a metre per second lengthens a streak.</summary>
    public float Stretch { get; init; }

    /// <summary>Which custom attribute holds a ribbon's strip identifier.</summary>
    public int RibbonSlot { get; init; }

    /// <summary>How bright a particle at full alpha is, for a light renderer.</summary>
    public float Intensity { get; init; }

    /// <summary>How far a particle of unit size reaches, for a light renderer.</summary>
    public float Range { get; init; }

    /// <summary>This row as the runtime renderer.</summary>
    public VfxRenderer ToRenderer() =>
        new(Kind, Alignment, Sort, Axis, Stretch, RibbonSlot, Intensity, Range);

    /// <summary>One runtime renderer as a row.</summary>
    /// <param name="renderer">The renderer.</param>
    /// <returns>The row.</returns>
    public static VfxRendererRow From(in VfxRenderer renderer) =>
        new() {
            Kind = renderer.Kind,
            Alignment = renderer.Alignment,
            Sort = renderer.Sort,
            Axis = renderer.Axis,
            Stretch = renderer.Stretch,
            RibbonSlot = renderer.RibbonSlot,
            Intensity = renderer.Intensity,
            Range = renderer.Range
        };
}

/// <summary>One attribute a graph declares for itself, as a build writes it.</summary>
[DataContract("VfxCustomAttributeRow")]
public readonly record struct VfxCustomAttributeRow {
    /// <summary>An unnamed attribute, for the serializer.</summary>
    public VfxCustomAttributeRow() { }

    /// <summary>What the graph calls it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>What it holds.</summary>
    public VfxAttributeType Type { get; init; }

    /// <summary>This row as the runtime attribute.</summary>
    public VfxCustomAttribute ToAttribute() => new(Name, Type);

    /// <summary>One runtime attribute as a row.</summary>
    /// <param name="attribute">The attribute.</param>
    /// <returns>The row.</returns>
    public static VfxCustomAttributeRow From(in VfxCustomAttribute attribute) =>
        new() { Name = attribute.Name, Type = attribute.Type };
}

/// <summary>A particle effect as a build wrote it and a game loads it.</summary>
/// <remarks>
///     <para>
///         <b>The content form of <see cref="VfxCompiledGraph" />, and the piece whose absence meant a
///         game could not load a <c>.vxvfx</c> at all.</b> The authored file is a node graph — nodes,
///         edges, positions, the numbers somebody typed — which only the editor can read, and
///         <c>docs/overview.md</c> listed it among the formats "whose compiler is still owed". This is
///         what the compiler writes: the graph already reduced to the flat instruction list both
///         backends run, with nothing of the editor in it.
///     </para>
///     <para>
///         <b>Why the rows are mirrors rather than the runtime types annotated.</b>
///         <see cref="VfxOperation" /> and its siblings are positional record structs, and the
///         serializer generator emits for a parameterless constructor and <c>init</c> members — so
///         annotating them in place would mean rewriting types that every graph in every project
///         constructs positionally. The mirror costs one row per runtime type and buys the runtime
///         types keeping their shape. <see cref="Materials.MaterialContent" /> makes the same split for
///         a different reason, and says so.
///     </para>
///     <para>
///         ⚠ <b><see cref="VfxCompiledGraph.Attributes" /> is not stored, and must not be.</b> It is
///         derived from what the operations read and write, and <c>Compile</c> derives it again on the
///         way back in — so a stored copy is a second answer to a question with one answer, and the
///         two would disagree the first time an opcode's declaration changed and an old asset was
///         loaded by a new build. The same argument applies to every other derived fact here, of which
///         that is the only one.
///     </para>
///     <para>
///         ⚠ <b>An invalid graph throws on the way back.</b> <c>Compile</c> refuses an updater that
///         reads an attribute nothing writes, and <see cref="ToGraph" /> goes through it rather than
///         round it — so content the importer never produced fails loudly rather than running over
///         zeroed memory, which is the failure mode with no symptom.
///     </para>
/// </remarks>
[DataContract("VfxEffectContent")]
public sealed record VfxEffectContent {
    /// <summary>The version this reader speaks.</summary>
    public const int Current = 1;

    /// <summary>Which version of the format this is.</summary>
    public int Version { get; init; } = Current;

    /// <summary>How many particles one instance has room for.</summary>
    public int Capacity { get; init; } = 1024;

    /// <summary>What makes particles.</summary>
    public VfxSpawnerRow[] Spawners { get; init; } = [];

    /// <summary>What a particle starts as.</summary>
    public VfxOperationRow[] Initializers { get; init; } = [];

    /// <summary>What happens to it, every step.</summary>
    public VfxOperationRow[] Updaters { get; init; } = [];

    /// <summary>The attributes the graph declared for itself, in slot order.</summary>
    public VfxCustomAttributeRow[] Customs { get; init; } = [];

    /// <summary>Whether anything draws it.</summary>
    /// <remarks>
    ///     A flag beside the renderer rather than a nullable one, because "no renderer" is a real and
    ///     ordinary state — a simulation driving something else has no colour and no size — and a
    ///     nullable value type in a chunk is a shape the format does not have. False leaves
    ///     <see cref="Renderer" /> at its default and unread.
    /// </remarks>
    public bool Drawn { get; init; }

    /// <summary>How it is drawn, when <see cref="Drawn" /> says it is.</summary>
    public VfxRendererRow Renderer { get; init; }

    /// <summary>This content as the graph a simulation runs.</summary>
    /// <returns>The compiled graph.</returns>
    /// <exception cref="ArgumentException">The content does not describe a valid graph.</exception>
    public VfxCompiledGraph ToGraph() {
        var spawners = new VfxSpawner[Spawners.Length];
        var initializers = new VfxOperation[Initializers.Length];
        var updaters = new VfxOperation[Updaters.Length];
        var customs = new VfxCustomAttribute[Customs.Length];

        for (var index = 0; index < spawners.Length; index++) {
            spawners[index] = Spawners[index].ToSpawner();
        }

        for (var index = 0; index < initializers.Length; index++) {
            initializers[index] = Initializers[index].ToOperation();
        }

        for (var index = 0; index < updaters.Length; index++) {
            updaters[index] = Updaters[index].ToOperation();
        }

        for (var index = 0; index < customs.Length; index++) {
            customs[index] = Customs[index].ToAttribute();
        }

        return VfxCompiledGraph.Compile(
            spawners,
            initializers,
            updaters,
            Math.Max(Capacity, 1),
            Drawn ? Renderer.ToRenderer() : null,
            customs
        );
    }

    /// <summary>One compiled graph as the content a build writes.</summary>
    /// <param name="graph">The graph.</param>
    /// <returns>The content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    public static VfxEffectContent From(VfxCompiledGraph graph) {
        ArgumentNullException.ThrowIfNull(graph);

        var spawners = new VfxSpawnerRow[graph.Spawners.Length];
        var initializers = new VfxOperationRow[graph.Initializers.Length];
        var updaters = new VfxOperationRow[graph.Updaters.Length];
        var customs = new VfxCustomAttributeRow[graph.Customs.Length];

        for (var index = 0; index < spawners.Length; index++) {
            spawners[index] = VfxSpawnerRow.From(graph.Spawners[index]);
        }

        for (var index = 0; index < initializers.Length; index++) {
            initializers[index] = VfxOperationRow.From(graph.Initializers[index]);
        }

        for (var index = 0; index < updaters.Length; index++) {
            updaters[index] = VfxOperationRow.From(graph.Updaters[index]);
        }

        for (var index = 0; index < customs.Length; index++) {
            customs[index] = VfxCustomAttributeRow.From(graph.Customs[index]);
        }

        return new() {
            Capacity = graph.Capacity,
            Spawners = spawners,
            Initializers = initializers,
            Updaters = updaters,
            Customs = customs,
            Drawn = graph.Renderer is not null,
            Renderer = graph.Renderer is { } renderer ? VfxRendererRow.From(renderer) : default
        };
    }
}
