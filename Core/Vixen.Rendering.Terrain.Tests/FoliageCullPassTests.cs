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
        Assert.Equal(112, FoliageCullViewRecord.SizeInBytes);
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
