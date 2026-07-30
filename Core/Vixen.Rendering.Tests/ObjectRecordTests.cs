// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Features;
using Vixen.Rendering.Lighting;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The per-object scalars a clustered frame could not deliver.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The clustered path binds no per-draw set at all</strong> — a fragment finds its own
///         lights in the grid, so choosing eight per object would be work whose answer nothing reads.
///         Which meant everything else in that block arrived as whatever set 3 last held, and
///         <c>probeIndex</c> is in it. Per-object reflection probes are one of the three features
///         <c>docs/plan/23-bindless-materials.md</c> was written to unblock, so the two could not both be on.
///     </para>
///     <para>
///         ⚠ <strong>The failure is a picture rather than an error.</strong> Every object reflecting
///         one object's probe looks like a probe placement mistake, and reads as plausible in a scene
///         where the probes are similar.
///     </para>
/// </remarks>
public sealed class ObjectRecordTests : IDisposable {
    static readonly PermutationKey<bool> UseObjectRecords =
        ParameterKeys.NewPermutation(false, "Lit.UseObjectRecords");

    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    /// <summary>
    ///     The records are only addressable if the draw carries the object's slot.
    /// </summary>
    /// <remarks>
    ///     <strong>Another feature's answer, which is why it is a parameter rather than a check.</strong>
    ///     What addresses a record is <c>SV_InstanceID</c>, and the API adds <c>firstInstance</c> into
    ///     it — which holds the object's slot only because the transform record path put it there.
    ///     Asked without that, every draw carries zero and every object reads record zero.
    /// </remarks>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Records_need_the_draw_to_carry_the_slot(bool addressable, bool expected) {
        using var lighting = new ForwardLightingRenderFeature { Device = device };

        Assert.Equal(expected, lighting.EnableRecords(UseObjectRecords, addressable));
        Assert.Equal(expected, lighting.UseRecords);
    }

    /// <summary>Each object's probe is at the object's own slot.</summary>
    /// <remarks>
    ///     Per <em>slot</em> and not per visible object, because a slot is what the instance index
    ///     carries. Two objects far enough apart to take different probes, so a version that wrote one
    ///     record for the frame, or wrote them in visibility order, fails rather than passing on a
    ///     scene where every answer is the same.
    /// </remarks>
    [Fact]
    public void Every_object_gets_its_own_probe() {
        using var h = Build();
        h.Lighting.EnableRecords(UseObjectRecords, addressable: true);

        var near = AddMesh(h, new(0f, 0f, 10f));
        var far = AddMesh(h, new(40f, 0f, 10f));

        h.System.Draw();

        var records = h.Lighting.Records;

        Assert.Equal(0, records[near.Index].ProbeIndex);
        Assert.Equal(1, records[far.Index].ProbeIndex);
        Assert.True(records[near.Index].ProbeWeight > 0f);
    }

    /// <summary>
    ///     And with the path off there is still a buffer, because the shader declares one either way.
    /// </summary>
    /// <remarks>
    ///     ⚠ <strong>What this rules out is a dark frame, not a probe-less one.</strong> A binding is
    ///     in a shader's plan because it was declared, so <c>objects</c> is in set 0 whichever way the
    ///     permutation went — and a set short one entry is not bound at all. A frame that bound its
    ///     per-draw block and left this empty would lose the whole of set 0.
    /// </remarks>
    [Fact]
    public void A_frame_that_does_not_use_them_still_leaves_a_buffer() {
        using var h = Build();

        AddMesh(h, new(0f, 0f, 10f));
        h.System.Draw();

        Assert.False(h.Lighting.UseRecords);
        Assert.True(h.Lighting.RecordBuffer.IsValid);
    }

    /// <summary>The buffer and its base are published where the frame's set reads them.</summary>
    /// <remarks>
    ///     Its own base, and not the transform feature's: two buffers, two rings, two frames in flight
    ///     advancing independently. Sharing one would read this frame's probes out of whichever region
    ///     the other buffer happened to be writing.
    /// </remarks>
    [Fact]
    public void The_buffer_and_its_base_are_published() {
        using var h = Build();
        var scene = new ParameterCollection();

        h.Lighting.Scene = scene;
        h.Lighting.ShaderName = "Lit";
        h.Lighting.EnableRecords(UseObjectRecords, addressable: true);

        AddMesh(h, new(0f, 0f, 10f));
        h.System.Draw();

        Assert.Equal(h.Lighting.RecordBuffer, scene.Get(ParameterKeys.New<BufferHandle>("Lit.objects")));
        Assert.Equal(h.Lighting.RecordBase, scene.Get(ParameterKeys.New<int>("Lit.objectBase")));
    }

    /// <summary>The clustered flag and the record flag are contributed as two permutations.</summary>
    /// <remarks>
    ///     Both are facts about the frame rather than the object, so every object shares one bit of
    ///     each — and both are contributed whichever way they went, so the number of bits the variant
    ///     mask packs does not depend on the answer. A key registered only when true would make one
    ///     object hash to different variants on two devices for a reason unrelated to it.
    /// </remarks>
    [Fact]
    public void Both_flags_are_contributed() {
        using var h = Build();

        Assert.Single(h.Lighting.PermutationKeys);

        h.Lighting.EnableRecords(UseObjectRecords, addressable: false);

        Assert.Equal(2, h.Lighting.PermutationKeys.Count);
        Assert.Contains(h.Lighting.PermutationKeys, key => key.Name == "Lit.UseObjectRecords");
    }

    // --- The fixture --------------------------------------------------------

    Harness Build() {
        var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var meshes = new MeshRenderFeature();
        var lighting = new ForwardLightingRenderFeature { Device = device, Clustered = true };
        var probes = new ReflectionProbeSelector();

        // Two probes far enough apart that an object is inside exactly one of them, so "which probe"
        // is a question with a different answer per object rather than one the fixture pre-decides.
        probes.Probes.Add(new() { Bounds = new(new(-8f, -8f, 2f), new(8f, 8f, 18f)), CapturePosition = new(0f, 0f, 10f) });
        probes.Probes.Add(new() { Bounds = new(new(32f, -8f, 2f), new(48f, 8f, 18f)), CapturePosition = new(40f, 0f, 10f) });

        lighting.Probes = probes;
        lighting.Lights.Add(RenderLight.Directional(new(0f, -1f, 0f), new(1f, 1f, 1f), 1f));

        meshes.Add(lighting);
        system.AddFeature(meshes);

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.1f, 1000f);

        system.SetViews(
            [new("camera") { Stages = opaque.Mask, Position = Vector3.Zero, Frustum = new(view * projection) }]
        );

        return new() { System = system, Opaque = opaque, Meshes = meshes, Lighting = lighting };
    }

    static RenderObjectId AddMesh(Harness h, Vector3 position) =>
        h.System.Objects.Add(
            new() { Bounds = new(position, 1f), Stages = h.Opaque.Mask, FeatureIndex = h.Meshes.Index }
        );

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required RenderStage Opaque { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required ForwardLightingRenderFeature Lighting { get; init; }

        public void Dispose() => System.Dispose();
    }
}
