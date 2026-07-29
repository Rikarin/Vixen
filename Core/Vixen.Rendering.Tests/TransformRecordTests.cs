// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The world matrix in a buffer instead of in the command stream.
/// </summary>
/// <remarks>
///     <para>
///         The last thing between a compacted draw list and a merged draw. Everything else the plan
///         needed was about <em>bindings</em> — a material's set, a mesh's two buffers — and a push
///         constant is none of those: it is data travelling in the command buffer, which means it is
///         per command by construction. Three objects that bind nothing between them still cannot
///         become one command while each has a matrix to push.
///     </para>
///     <para>
///         ⚠ <strong>Both halves move together.</strong> <c>UseRecords</c> decides whether the host
///         pushes, and the permutation decides whether the shader reads a push constant or a buffer.
///         Either alone draws a picture: a matrix pushed to a shader that reads the buffer leaves
///         every object at record zero, and a buffer nobody filled puts every object at the origin.
///     </para>
/// </remarks>
public sealed class TransformRecordTests : IDisposable {
    static readonly PermutationKey<bool> UseRecords =
        ParameterKeys.NewPermutation(false, "Lit.UseTransformRecords");

    readonly NullDevice device;
    readonly EffectSystem effects = new();
    readonly List<string> resolved = [];

    public TransformRecordTests() : this(counting: true) { }

    TransformRecordTests(bool counting) {
        device = new(new() { Record = true, Features = NullDevice.Everything with { HasDrawIndirectCount = counting } });
        effects.AddProvider(new Compiles(resolved));
    }

    public void Dispose() => device.Dispose();

    // --- The decision -------------------------------------------------------

    /// <summary>On a device that can draw an indirect count, both halves come on together.</summary>
    [Fact]
    public void The_capability_turns_records_on() {
        using var harness = Build();

        Assert.True(harness.Transforms.EnableRecords(UseRecords));
        Assert.True(harness.Transforms.UseRecords);
        Assert.False(harness.Transforms.IsRecording);
    }

    /// <summary>And on a device without it, both stay off.</summary>
    /// <remarks>
    ///     ⚠ The gate is not about what can read a buffer — every device can. It is about whether the
    ///     read is worth paying for: without a device-side draw count there is no compaction, without
    ///     compaction there is no merged command, and then a push constant is strictly cheaper than a
    ///     dependent read per vertex. Off is the right answer rather than the degraded one.
    /// </remarks>
    [Fact]
    public void Without_the_capability_neither_half_comes_on() {
        using var plain = new TransformRecordTests(counting: false);
        using var harness = plain.Build();

        Assert.False(harness.Transforms.EnableRecords(UseRecords));
        Assert.False(harness.Transforms.UseRecords);
        Assert.True(harness.Transforms.IsRecording);
    }

    /// <summary>The permutation reaches the effect key, so the right shader is compiled.</summary>
    /// <remarks>
    ///     <strong>The failure with no symptom.</strong> Turning the host's half on without the
    ///     shader's leaves a shader reading a push constant nothing pushed — which is not zero, but
    ///     whatever the last pass left in that range. It draws.
    /// </remarks>
    [Fact]
    public void The_permutation_reaches_the_effect_key() {
        using var harness = Build();
        harness.Transforms.EnableRecords(UseRecords);

        AddMesh(harness);
        harness.System.Draw();

        Assert.Contains(resolved, key => key.Contains("UseTransformRecords=true", StringComparison.Ordinal));
    }

    /// <summary>And the same objects on a device without the capability compile the pushing one.</summary>
    [Fact]
    public void And_without_it_the_pushing_variant_is_compiled() {
        using var plain = new TransformRecordTests(counting: false);
        using var harness = plain.Build();

        harness.Transforms.EnableRecords(UseRecords);

        AddMesh(harness);
        harness.System.Draw();

        Assert.Contains(plain.resolved, key => key.Contains("UseTransformRecords=false", StringComparison.Ordinal));
    }

    // --- The buffer ---------------------------------------------------------

    /// <summary>Each object's matrix is at the object's own slot.</summary>
    /// <remarks>
    ///     <para>
    ///         <strong>The slot is the whole design.</strong> A record index that had to be allocated
    ///         would need a map from object to record, a rebuild whenever an object went away, and a
    ///         second copy of it on the device for the compaction shader to read. The object array is
    ///         already dense and already indexed the way a draw's <c>firstInstance</c> can address it,
    ///         so the record array is the object array and there is nothing to allocate.
    ///     </para>
    ///     <para>
    ///         Three distinct translations, so a version that wrote the same matrix three times or
    ///         wrote them in visibility order would fail rather than pass on identity.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_object_is_at_its_own_record() {
        using var harness = Build();
        harness.Transforms.EnableRecords(UseRecords);

        var first = AddMesh(harness, new(1f, 0f, 10f));
        var second = AddMesh(harness, new(2f, 0f, 10f));
        var third = AddMesh(harness, new(3f, 0f, 10f));

        harness.System.Draw();

        var records = harness.Transforms.Records;
        Assert.Equal(3, harness.Transforms.RecordCount);

        Assert.Equal(1f, records[first.Index].M41);
        Assert.Equal(2f, records[second.Index].M41);
        Assert.Equal(3f, records[third.Index].M41);
    }

    /// <summary>
    ///     The buffer exists even with the records off, because the shader declares it either way.
    /// </summary>
    /// <remarks>
    ///     ⚠ <strong>What this rules out is a dark frame, not an untransformed one.</strong> A binding
    ///     is in a shader's plan because it was declared, not because a variant reads it — so
    ///     <c>transforms</c> is in set 0 whichever way the permutation went. And a set short one entry
    ///     is not bound <em>at all</em>: a frame that pushed its matrices and left this empty would
    ///     lose the whole of set 0 — the sun, the environment, the shadow atlas — and draw nothing.
    ///     One identity record is what it costs to make that impossible.
    /// </remarks>
    [Fact]
    public void A_pushing_frame_still_leaves_the_binding_something() {
        using var harness = Build();

        AddMesh(harness);
        harness.System.Draw();

        Assert.False(harness.Transforms.UseRecords);
        Assert.True(harness.Transforms.Buffer.IsValid);
    }

    /// <summary>The base index is the frame's own region, in whole records.</summary>
    /// <remarks>
    ///     <para>
    ///         <strong>An index rather than a bound offset.</strong> The buffer holds one region per
    ///         frame in flight — a descriptor bound at zero reads the region another frame is writing
    ///         — and a resource reaches a set through a handle a host named, with nowhere to put an
    ///         offset. So the whole ring is bound and the shader adds this to the instance index.
    ///     </para>
    ///     <para>
    ///         Whole records, always: a region's stride is a multiple of 256 and a matrix is 64, so a
    ///         region begins on a record boundary. A base that landed mid-record would shear every
    ///         object's matrix across two of them.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_base_is_where_this_frames_region_starts() {
        using var harness = Build();
        harness.Transforms.EnableRecords(UseRecords);

        AddMesh(harness);

        var seen = new HashSet<int>();

        for (var frame = 0; frame < device.FramesInFlight + 1; frame++) {
            harness.System.Draw();
            seen.Add(harness.Transforms.BaseIndex);

            Assert.True(harness.Transforms.BaseIndex >= 0);
        }

        // The ring is walked rather than sat on, which is the only reason the offset exists.
        Assert.Equal(device.FramesInFlight, seen.Count);
    }

    /// <summary>The handle and the base are published where the frame's set reads them.</summary>
    /// <remarks>
    ///     Published from <c>Prepare</c> rather than read once by a host, because both move: the
    ///     buffer is recreated when the scene outgrows it, and the base changes every frame by
    ///     construction. A host that wrote either of them down would bind a retired buffer at a stale
    ///     offset, and the picture would be last frame's poses on this frame's geometry.
    /// </remarks>
    [Fact]
    public void The_buffer_and_the_base_are_published() {
        using var harness = Build();
        var scene = new ParameterCollection();

        harness.Transforms.Scene = scene;
        harness.Transforms.ShaderName = "Lit";
        harness.Transforms.EnableRecords(UseRecords);

        AddMesh(harness);
        harness.System.Draw();

        Assert.Equal(
            harness.Transforms.Buffer,
            scene.Get(ParameterKeys.New<BufferHandle>("Lit.transforms"))
        );

        Assert.Equal(
            harness.Transforms.BaseIndex,
            scene.Get(ParameterKeys.New<int>("Lit.transformBase"))
        );
    }

    // --- The draw -----------------------------------------------------------

    /// <summary>A directly recorded draw carries the record index in its first instance.</summary>
    /// <remarks>
    ///     The same field instancing uses, and that is the point rather than a coincidence: the API
    ///     adds it into the instance index before the vertex stage runs, so a draw reaches its own
    ///     record with no descriptor, no dynamic offset and nothing per object in the command stream.
    ///     Instancing is this with a run of more than one.
    /// </remarks>
    [Fact]
    public void The_draw_carries_the_record_index() {
        using var harness = Build();
        harness.Transforms.EnableRecords(UseRecords);

        AddMesh(harness);
        AddMesh(harness);

        harness.System.Draw();
        device.Recorder!.Clear();
        RecordStage(harness);

        var draws = device.Recorder.OfKind(RecordedCommandKind.DrawIndexed).ToList();

        Assert.Equal(2, draws.Count);
        Assert.Equal([0L, 1L], [.. draws.Select(command => command.E).Order()]);
    }

    /// <summary>And a pushing frame carries nothing there, as it always did.</summary>
    /// <remarks>
    ///     The control. <c>firstInstance</c> is a real draw argument with a meaning of its own, so a
    ///     feature that filled it whether or not anything read it would be changing what every
    ///     ordinary draw means to buy nothing.
    /// </remarks>
    [Fact]
    public void A_pushing_draw_carries_nothing_there() {
        using var harness = Build();

        AddMesh(harness);
        AddMesh(harness);

        harness.System.Draw();
        device.Recorder!.Clear();
        RecordStage(harness);

        foreach (var command in device.Recorder.OfKind(RecordedCommandKind.DrawIndexed)) {
            Assert.Equal(0L, command.E);
        }
    }

    // --- The fixture --------------------------------------------------------

    void RecordStage(Harness h) {
        var target = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 16, 16, TextureUsage.ColourTarget, Name: "target"))
        );

        using var list = device.BeginCommandList();
        list.BeginRenderPass(new([new(target)], name: "Opaque"));

        h.System.Record(
            h.System.Views[0],
            h.Opaque,
            new(list, effects) { Device = device, Output = new([PixelFormat.Rgba8UNorm]) }
        );

        list.EndRenderPass();
        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    Harness Build() {
        var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = new EffectPipelineDescriber(device) };
        var materials = new MaterialRenderFeature { Effects = effects, Device = device };
        var transforms = new TransformRenderFeature { Device = device };

        materials.PermutationKeys["Lit"] = [UseRecords];

        meshes.Add(transforms);
        meshes.Add(materials);
        system.AddFeature(meshes);

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        system.SetViews(
            [new("camera") { Stages = opaque.Mask, Position = Vector3.Zero, Frustum = new(view * projection) }]
        );

        return new() {
            System = system,
            Opaque = opaque,
            Meshes = meshes,
            Materials = materials,
            Transforms = transforms,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex }),
            Indices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Index })
        };
    }

    static RenderObjectId AddMesh(Harness h, Vector3 position = default) {
        var id = h.System.Objects.Add(
            new() {
                Bounds = new(new Vector3(position.X, position.Y, 10f), 1f),
                Stages = h.Opaque.Mask,
                FeatureIndex = h.Meshes.Index
            }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices,
            IndexBuffer = h.Indices,
            IndexFormat = IndexFormat.UInt16,
            Count = 3,
            InstanceCount = 1
        };

        h.System.Objects.Data.Data(h.Transforms.World)[id.Index] = Matrix4x4.FromTranslation(position);
        h.Materials.Assign(h.System, id, new("Lit"));

        return id;
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required RenderStage Opaque { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required TransformRenderFeature Transforms { get; init; }
        public required BufferHandle Vertices { get; init; }
        public required BufferHandle Indices { get; init; }

        public void Dispose() => System.Dispose();
    }

    /// <summary>Anything that is asked for, with the key it was asked for recorded.</summary>
    sealed class Compiles(List<string> resolved) : IEffectProvider {
        public Effect? TryGet(EffectKey key) {
            resolved.Add(key.ToString());
            return new() { Key = key, Stages = Modules };
        }

        static ImmutableArray<EffectStage> Modules =>
            [new(ShaderStage.Vertex, [1, 2, 3, 4], "main"), new(ShaderStage.Fragment, [5, 6, 7, 8], "main")];
    }
}
