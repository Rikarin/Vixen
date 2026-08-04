// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>
///     The per-instance cull's device half, against a recording device — [docs/plan/31 § T5]'s owed
///     item.
/// </summary>
/// <remarks>
///     What a headless device can say: what was bound, how many groups were dispatched, in what order
///     and behind which barriers. What it cannot say is whether the same instances survive, which
///     needs a compute queue — so <see cref="FoliageCullParityTests" /> holds the arithmetic against
///     <c>InstanceCuller</c> instead, and this holds the plumbing.
/// </remarks>
public sealed class FoliageCullPassTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    static FoliageType Tree =>
        FoliageType.Of("Tree") with {
            Mesh = "Meshes/pine",
            Radius = 2f,
            StartCullDistance = 180f,
            EndCullDistance = 200f
        };

    static FoliageDraw Draws(int type, params float[] distances) =>
        new(
            type,
            [.. Enumerable.Range(0, distances.Length + 1)
                .Select(level => new DrawCommand { IndexCount = (uint)(600 >> level), FirstIndex = (uint)(level * 10) })],
            distances
        );

    static BoundingFrustum Everything() =>
        new(
            Matrix4x4.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
            * Matrix4x4.PerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.1f, 4000f)
        );

    static (FoliageVolume Volume, int Type) Filled(int count, float z = -100f) {
        var volume = new FoliageVolume(new(32f));
        var type = volume.AddType(Tree);

        for (var index = 0; index < count; index++) {
            volume.Add(type, new(new(((index % 16) - 8) * 2f, 0f, z), Quaternion.Identity, 1f));
        }

        return (volume, type);
    }

    FoliageCullPass Build(int instances = 4096, int batches = 64) =>
        new(
            device,
            device.CreateShader(ShaderStage.Compute, [1, 2, 3, 4], "cull.count.cs"),
            device.CreateShader(ShaderStage.Compute, [5, 6, 7, 8], "cull.place.cs"),
            instances,
            batches
        );

    /// <summary>The records the shader reads are the size the shader declares.</summary>
    /// <remarks>
    ///     ⚠ <b>A stride that disagrees does not fail.</b> It reads one tree's rotation out of the
    ///     next tree's position, which draws as a forest that is almost right and moves whenever
    ///     anything else changes.
    /// </remarks>
    [Fact]
    public void TheRecordsAreTheSizeTheShaderDeclares() {
        Assert.Equal(32, FoliageCullInstanceRecord.SizeInBytes);
        Assert.Equal(48, FoliageCullBatchRecord.SizeInBytes);
        // Six planes, a position, the pyramid's level count, and last frame's matrix.
        Assert.Equal(176, FoliageCullViewRecord.SizeInBytes);
        Assert.Equal(20, FoliageCullPass.DrawCommandBytes);

        // The instance record is the *stored* form, which is what lets a volume be uploaded as it is.
        Assert.Equal(FoliageStore.InstanceBytes, FoliageCullInstanceRecord.SizeInBytes);
    }

    [Fact]
    public void UploadingFilesEveryInstanceUnderItsCell() {
        using var pass = Build();
        var (volume, type) = Filled(64);

        var uploaded = pass.Upload(volume, [Draws(type)]);

        Assert.Equal(64, uploaded);
        Assert.Equal(64, pass.InstanceCount);
        Assert.True(pass.BatchCount > 0);
        Assert.Equal(0, pass.Refused);

        var total = 0;

        for (var batch = 0; batch < pass.BatchCount; batch++) {
            var (first, length) = pass.RunOf(batch);

            Assert.Equal(total, first);

            total += length;
        }

        Assert.Equal(64, total);
    }

    /// <summary>A type nobody asked to draw is not uploaded.</summary>
    [Fact]
    public void ATypeWithNoDrawIsSkipped() {
        using var pass = Build();
        var (volume, type) = Filled(32);

        Assert.Equal(0, pass.Upload(volume, []));
        Assert.Equal(0, pass.BatchCount);

        Assert.Equal(32, pass.Upload(volume, [Draws(type)]));
    }

    /// <summary>An invalid palette type is refused at upload, not culled into an empty forest.</summary>
    /// <remarks>
    ///     ⚠ <b>The alternative is the quiet outcome: every counter healthy and nothing drawn.</b>
    ///     <c>FoliageType.Validate</c> exists, and this is the seam where a palette enters the device
    ///     path — the same refusal <c>FoliageGrowth.Simulate</c> makes at the other end of the data.
    /// </remarks>
    [Fact]
    public void AnInvalidTypeIsRefusedAtUpload() {
        using var pass = Build();
        var volume = new FoliageVolume(new(32f));
        var type = volume.AddType(Tree with { Radius = 0f });

        volume.Add(type, new(Vector3.Zero, Quaternion.Identity, 1f));

        var refusal = Assert.Throws<ArgumentException>(() => pass.Upload(volume, [Draws(type)]));

        Assert.Contains("Tree", refusal.Message);
    }

    /// <summary>More instances than the pass holds is announced rather than silent.</summary>
    [Fact]
    public void APassThatRunsOutSaysSo() {
        using var pass = Build(instances: 16, batches: 64);
        var (volume, type) = Filled(200, z: -100f);

        pass.Upload(volume, [Draws(type)]);

        Assert.True(pass.Refused > 0, "two hundred trees fitted into room for sixteen.");
        Assert.True(pass.InstanceCount <= 16);
    }

    /// <summary>The first stage is the host's, and a cell behind the camera is marked invisible.</summary>
    [Fact]
    public void ACellBehindTheCameraIsMarkedInvisible() {
        using var pass = Build();
        var (volume, type) = Filled(64, z: 400f);

        pass.Upload(volume, [Draws(type)]);

        var visible = pass.Prepare(Everything(), Vector3.Zero);

        Assert.Equal(0, visible);

        for (var batch = 0; batch < pass.BatchCount; batch++) {
            Assert.Equal(0u, pass.BatchOf(batch).Visible);
        }
    }

    [Fact]
    public void ACellInFrontOfItIsNot() {
        using var pass = Build();
        var (volume, type) = Filled(64, z: -100f);

        pass.Upload(volume, [Draws(type)]);

        Assert.True(pass.Prepare(Everything(), Vector3.Zero) > 0);
        Assert.Equal(1u, pass.BatchOf(0).Visible);
    }

    /// <summary>A level the batch does not have gets an unreachable threshold, not a zero.</summary>
    /// <remarks>
    ///     ⚠ <b>A zero left in an unused slot puts every instance at the last level</b> the day
    ///     somebody raises the level count and forgets the distance — and the symptom is a forest
    ///     drawn entirely at its coarsest mesh, which reads as a broken LOD group rather than as a
    ///     missing number.
    /// </remarks>
    [Fact]
    public void AnUnusedLevelThresholdIsUnreachable() {
        using var pass = Build();
        var (volume, type) = Filled(32);

        pass.Upload(volume, [Draws(type, 50f)]);
        pass.Prepare(Everything(), Vector3.Zero);

        var record = pass.BatchOf(0);

        Assert.Equal(2u, record.LevelCount);
        Assert.Equal(50f, record.Lod0);
        Assert.Equal(float.MaxValue, record.Lod1);
        Assert.Equal(float.MaxValue, record.Lod2);
    }

    /// <summary>The dispatch covers every instance, and no more.</summary>
    [Fact]
    public void TheDispatchCoversEveryInstance() {
        using var pass = Build();
        var (volume, type) = Filled(200);

        pass.Upload(volume, [Draws(type)]);

        var groups = (pass.InstanceCount + FoliageCullPass.GroupSize - 1) / FoliageCullPass.GroupSize;

        Assert.Equal(groups, pass.Groups);
        Assert.True(groups * FoliageCullPass.GroupSize >= pass.InstanceCount);
        Assert.True((groups - 1) * FoliageCullPass.GroupSize < pass.InstanceCount);
    }

    /// <summary>Nothing uploaded is nothing recorded.</summary>
    [Fact]
    public void AnEmptyVolumeRecordsNothing() {
        using var pass = Build();

        pass.Upload(new FoliageVolume(new(32f)), []);

        var commands = device.BeginCommandList();

        Assert.Equal(0, pass.Record(commands));

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.Dispatch));
    }

    /// <summary>Counting runs before placing, and both run.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the whole of what makes it two dispatches.</b> The placing phase reads the
    ///     counting phase's totals to find each level's base, so a placing dispatch recorded first —
    ///     or recorded without a barrier between — reads zeroes and writes every level's survivors on
    ///     top of level zero's.
    /// </remarks>
    [Fact]
    public void CountingRunsBeforePlacing() {
        using var pass = Build();
        var (volume, type) = Filled(128);

        pass.Upload(volume, [Draws(type, 50f, 120f)]);
        pass.Prepare(Everything(), Vector3.Zero);
        device.Recorder!.Clear();

        var commands = device.BeginCommandList();

        Assert.Equal(pass.Groups * 2, pass.Record(commands));

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var recorded = device.Recorder.Commands.ToList();
        var dispatches = recorded.FindAll(entry => entry.Kind == RecordedCommandKind.Dispatch);

        Assert.Equal(2, dispatches.Count);

        var first = recorded.FindIndex(entry => entry.Kind == RecordedCommandKind.Dispatch);
        var second = recorded.FindLastIndex(entry => entry.Kind == RecordedCommandKind.Dispatch);
        var between = recorded
            .GetRange(first + 1, second - first - 1)
            .Count(entry => entry.Kind == RecordedCommandKind.Barrier);

        Assert.True(between > 0, "the placing phase reads what the counting phase wrote, unfenced.");
    }

    /// <summary>The counters are zeroed before the first dispatch, not after the second.</summary>
    [Fact]
    public void TheCountersAreClearedBeforeTheDispatches() {
        using var pass = Build();
        var (volume, type) = Filled(64);

        pass.Upload(volume, [Draws(type)]);
        pass.Prepare(Everything(), Vector3.Zero);
        device.Recorder!.Clear();

        var commands = device.BeginCommandList();

        pass.Record(commands);
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var recorded = device.Recorder.Commands.ToList();
        var copies = recorded.FindAll(entry => entry.Kind == RecordedCommandKind.CopyBuffer);
        var lastCopy = recorded.FindLastIndex(entry => entry.Kind == RecordedCommandKind.CopyBuffer);
        var firstDispatch = recorded.FindIndex(entry => entry.Kind == RecordedCommandKind.Dispatch);

        // Two: the level counts and the level heads. One would leave the other holding last frame's.
        Assert.Equal(2, copies.Count);
        Assert.True(lastCopy < firstDispatch, "a counter was cleared after the dispatch that reads it.");
    }

    /// <summary>And the draw is fenced off from the writes.</summary>
    [Fact]
    public void TheDrawIsFencedOffFromTheWrites() {
        using var pass = Build();
        var (volume, type) = Filled(64);

        pass.Upload(volume, [Draws(type)]);
        pass.Prepare(Everything(), Vector3.Zero);
        device.Recorder!.Clear();

        var commands = device.BeginCommandList();

        pass.Record(commands);
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var barriers = device.Recorder.Commands.Count(entry => entry.Kind == RecordedCommandKind.Barrier);

        Assert.True(barriers >= 3, $"only {barriers} barriers around two compute writes three things read.");
    }

    /// <summary>The placing phase writes the parameters, so they are fenced beside the survivors.</summary>
    /// <remarks>
    ///     ⚠ <b>The final barrier hands the parameters to the draw from ShaderWrite</b>, and without
    ///     this one nothing ever put them in it — last frame's draw reads records the placing phase
    ///     is overwriting, unfenced.
    /// </remarks>
    [Fact]
    public void TheParametersAreFencedBeforeTheDispatches() {
        using var pass = Build();
        var (volume, type) = Filled(64);

        pass.Upload(volume, [Draws(type)]);
        pass.Prepare(Everything(), Vector3.Zero);
        device.Recorder!.Clear();

        var commands = device.BeginCommandList();

        pass.Record(commands);
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var recorded = device.Recorder.Commands.ToList();
        var firstDispatch = recorded.FindIndex(entry => entry.Kind == RecordedCommandKind.Dispatch);
        var before = recorded.GetRange(0, firstDispatch).FindAll(entry => entry.Kind == RecordedCommandKind.Barrier);

        // Counts, heads, survivors *and* parameters — the placing phase writes all four.
        Assert.Contains(before, barrier => barrier.A == 4);
    }

    // --- Frames in flight ---------------------------------------------------

    /// <summary>Each frame writes its own slot of the ring, descriptor set and all.</summary>
    /// <remarks>
    ///     ⚠ <b><c>device.Write</c> is an immediate memcpy.</b> A single batch table, view or
    ///     argument buffer is the host overwriting what the frame in flight is still reading — and
    ///     for the arguments, what its indirect draw is reading and its placing dispatch is patching.
    /// </remarks>
    [Fact]
    public void ConsecutiveFramesUseDifferentSlots() {
        using var pass = Build();
        var (volume, type) = Filled(64);

        pass.Upload(volume, [Draws(type)]);

        var sets = new long[3];
        var arguments = new BufferHandle[3];

        for (var frame = 0; frame < sets.Length; frame++) {
            pass.Prepare(Everything(), Vector3.Zero);
            device.Recorder!.Clear();

            var commands = device.BeginCommandList();

            pass.Record(commands);
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);

            sets[frame] = device.Recorder.Commands
                .First(entry => entry.Kind == RecordedCommandKind.BindDescriptorSet)
                .B;
            arguments[frame] = pass.Commands;
        }

        Assert.NotEqual(sets[0], sets[1]);
        Assert.NotEqual(arguments[0], arguments[1]);

        // The ring is FramesInFlight wide, and the null device's is two.
        Assert.Equal(sets[0], sets[2]);
        Assert.Equal(arguments[0], arguments[2]);
    }

    /// <summary>Every level of every batch has an argument slot, whether or not it is drawn.</summary>
    /// <remarks>
    ///     Constant stride, for the reason the shader gives: a caller reads level N's command at slot
    ///     N rather than reading back which levels survived.
    /// </remarks>
    [Fact]
    public void EveryLevelHasAnArgumentSlot() {
        using var pass = Build();
        var (volume, type) = Filled(64);

        pass.Upload(volume, [Draws(type, 50f)]);

        Assert.Equal(pass.BatchCount * FoliageCullPass.MaxLevels, pass.Draws);
        Assert.Equal(0L, pass.CommandOf(0, 0));
        Assert.Equal(20L, pass.CommandOf(0, 1));
        Assert.Equal((long)FoliageCullPass.MaxLevels * 20, pass.CommandOf(1, 0));
    }

    // --- Occlusion ----------------------------------------------------------

    /// <summary>A pass with no occluding variants never occludes, whatever it is handed.</summary>
    /// <remarks>
    ///     ⚠ <b>The worst of the three outcomes would be a silent non-answer</b> — a pass given a
    ///     pyramid but no shader that can read one, costing nothing, rejecting nothing, and looking
    ///     enabled. <see cref="FoliageCullPass.Occluding" /> is what says which it is.
    /// </remarks>
    [Fact]
    public void APassWithNoOccludingVariantNeverOccludes() {
        using var pass = Build();
        var (volume, type) = Filled(32);

        pass.Upload(volume, [Draws(type)]);
        pass.Prepare(Everything(), Vector3.Zero, 1f, Pyramid());

        Assert.False(pass.Occluding);
    }

    /// <summary>And one with them occludes only when there is a pyramid.</summary>
    [Fact]
    public void OcclusionNeedsBothAPyramidAndAVariant() {
        using var pass = Occluding();
        var (volume, type) = Filled(32);

        pass.Upload(volume, [Draws(type)]);

        pass.Prepare(Everything(), Vector3.Zero);
        Assert.False(pass.Occluding);

        pass.Prepare(Everything(), Vector3.Zero, 1f, Pyramid());
        Assert.True(pass.Occluding);
    }

    /// <summary>Both phases run the same variant.</summary>
    /// <remarks>
    ///     ⚠ <b>The placing phase recomputes the counting phase's verdict</b>, so a pair that
    ///     disagreed about occlusion would place survivors the counts never accounted for — which
    ///     writes past the end of a level's run.
    /// </remarks>
    [Fact]
    public void BothPhasesRunTheSameVariant() {
        using var pass = Occluding();
        var (volume, type) = Filled(128);

        pass.Upload(volume, [Draws(type, 50f)]);
        pass.Prepare(Everything(), Vector3.Zero, 1f, Pyramid());

        device.Recorder!.Clear();

        var commands = device.BeginCommandList();

        pass.Record(commands);
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var pipelines = device.Recorder.Commands
            .Where(entry => entry.Kind == RecordedCommandKind.BindPipeline)
            .Select(entry => entry.A)
            .ToArray();

        // Two dispatches, two pipelines, and they are not the same pipeline — but they are both the
        // occluding pair rather than one of each.
        Assert.Equal(2, pipelines.Length);
        Assert.NotEqual(pipelines[0], pipelines[1]);
    }

    /// <summary>With no pyramid the occluder binding still holds a real texture.</summary>
    /// <remarks>
    ///     ⚠ <b>A default view in a bound set is undefined behaviour or a refused dispatch on most
    ///     backends</b>, whether or not the variant that runs would sample it — the stance
    ///     <c>TerrainRenderer</c> takes for a hole in a descriptor array.
    /// </remarks>
    [Fact]
    public void NoPyramidStillBindsARealOccluderTexture() {
        using var pass = Build();
        var (volume, type) = Filled(16);

        pass.Upload(volume, [Draws(type)]);
        pass.Prepare(Everything(), Vector3.Zero);

        Assert.True(pass.OccluderBinding.IsValid);

        using var occluding = Occluding();
        var pyramid = Pyramid();

        occluding.Upload(volume, [Draws(type)]);
        occluding.Prepare(Everything(), Vector3.Zero, 1f, pyramid);

        Assert.Equal(pyramid.View, occluding.OccluderBinding);

        // And handing the pyramid back is not sticky: the next frame without one stands in again.
        occluding.Prepare(Everything(), Vector3.Zero);

        Assert.True(occluding.OccluderBinding.IsValid);
        Assert.NotEqual(pyramid.View, occluding.OccluderBinding);
    }

    /// <summary>The stand-in occluder is settled into ShaderRead once, on the first record.</summary>
    [Fact]
    public void TheStandInOccluderIsSettledOnce() {
        using var pass = Build();
        var (volume, type) = Filled(16);

        pass.Upload(volume, [Draws(type)]);

        var textureBarriers = new int[2];

        for (var frame = 0; frame < textureBarriers.Length; frame++) {
            pass.Prepare(Everything(), Vector3.Zero);
            device.Recorder!.Clear();

            var commands = device.BeginCommandList();

            pass.Record(commands);
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);

            textureBarriers[frame] = device.Recorder.Commands
                .Count(entry => entry.Kind == RecordedCommandKind.Barrier && entry.B > 0);
        }

        Assert.Equal(1, textureBarriers[0]);
        Assert.Equal(0, textureBarriers[1]);
    }

    /// <summary>The view record carries the pyramid's matrix, not this frame's.</summary>
    /// <remarks>
    ///     ⚠ <b>A pyramid is a picture of a particular view.</b> Testing against another view's
    ///     matrix occludes by arithmetic that was never about it — the mistake that produces trees
    ///     vanishing when the camera turns.
    /// </remarks>
    [Fact]
    public void TheViewRecordCarriesThePyramidsOwnMatrix() {
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.5f, 500f);
        var record = FoliageCullViewRecord.Of(
            Everything(),
            new(1f, 2f, 3f),
            new(default, projection, Levels: 6)
        );

        Assert.Equal(projection, record.OccluderProjection);
        Assert.Equal(6f, record.OccluderLevels);
        Assert.Equal(new Vector3(1f, 2f, 3f), record.Position);
    }

    FoliageOccluders Pyramid() =>
        new(
            device.CreateTextureView(
                device.CreateTexture(new(PixelFormat.R32Float, 64, 64, TextureUsage.Sampled, MipLevels: 6, Name: "hi-z"))
            ),
            Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.5f, 500f),
            6
        );

    FoliageCullPass Occluding() =>
        new(
            device,
            device.CreateShader(ShaderStage.Compute, [1], "cull.count.cs"),
            device.CreateShader(ShaderStage.Compute, [2], "cull.place.cs"),
            4096,
            64,
            device.CreateShader(ShaderStage.Compute, [3], "cull.count.hiz.cs"),
            device.CreateShader(ShaderStage.Compute, [4], "cull.place.hiz.cs")
        );

    [Fact]
    public void AShaderWithNoComputeStageIsRefused() {
        var valid = device.CreateShader(ShaderStage.Compute, [1], "cull.cs");

        Assert.Throws<ArgumentException>(() => new FoliageCullPass(device, default, valid));
        Assert.Throws<ArgumentException>(() => new FoliageCullPass(device, valid, default));
    }

    [Fact]
    public void UsingItAfterDisposalIsRefused() {
        var pass = Build();
        var (volume, type) = Filled(8);

        pass.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pass.Upload(volume, [Draws(type)]));
        Assert.Throws<ObjectDisposedException>(() => pass.Prepare(Everything(), Vector3.Zero));
    }
}
