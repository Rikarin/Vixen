// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.Lighting;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The GPU mirror of the clipmap: what it allocates, what it copies, and what it names.
/// </summary>
/// <remarks>
///     Everything about what the field <i>says</i> is checked without a device in
///     <c>Vixen.Rendering.DistanceFields.Tests</c>, against closed forms. What is left for a device
///     is allocate, stage, copy and name — so this asserts on the recorded command stream rather
///     than on any value, because a copy is the only observable thing an upload does.
/// </remarks>
public class GlobalDistanceFieldTextureTests {
    [Fact]
    public void EachLevelGetsItsOwnVolumeAndOneCopy() {
        using var device = new NullDevice(new() { Record = true });
        using var mirror = new GlobalDistanceFieldTexture(Composited(levels: 3));

        var list = device.BeginCommandList();
        mirror.Upload(device, list);
        Submit(device, list);

        Assert.True(mirror.IsCreated);
        Assert.Equal(1, mirror.Uploads);
        Assert.Equal(3, device.Recorder!.CountOf(RecordedCommandKind.CopyBufferToTexture));

        for (var level = 0; level < 3; level++) {
            Assert.True(mirror.Level(level).IsValid);
        }

        // Three textures, not one resource sliced three ways: a 3D texture cannot be an array layer.
        Assert.NotEqual(mirror.Level(0), mirror.Level(1));
        Assert.NotEqual(mirror.Level(1), mirror.Level(2));
        Assert.True(mirror.Sampler.IsValid);
    }

    /// <summary>
    ///     One staging buffer, each level written at its own offset. Reusing one small region across
    ///     levels would overwrite bytes a copy has been recorded against but not yet run.
    /// </summary>
    [Fact]
    public void LevelsAreStagedAtDistinctOffsetsOfOneBuffer() {
        using var device = new NullDevice(new() { Record = true });
        using var mirror = new GlobalDistanceFieldTexture(Composited(levels: 3, resolution: 8));

        var list = device.BeginCommandList();
        mirror.Upload(device, list);
        Submit(device, list);

        var copies = device.Recorder!.Commands
            .Where(command => command.Kind == RecordedCommandKind.CopyBufferToTexture)
            .ToArray();

        Assert.Equal(3, copies.Length);
        Assert.Single(copies.Select(copy => copy.A).Distinct());
        Assert.Equal(3, copies.Select(copy => copy.B).Distinct().Count());

        var stride = 8L * 8 * 8 * sizeof(float);

        Assert.Equal([0L, stride, stride * 2], copies.Select(copy => copy.B).Order());
    }

    [Fact]
    public void TheResourcesAreMadeOnceAndReusedByLaterUploads() {
        using var device = new NullDevice(new() { Record = true });
        using var mirror = new GlobalDistanceFieldTexture(Composited(levels: 2));

        var list = device.BeginCommandList();
        mirror.Upload(device, list);

        var first = mirror.Level(0);
        var second = device.BeginCommandList();

        mirror.Upload(device, second);
        Submit(device, list);
        Submit(device, second);

        Assert.Equal(first, mirror.Level(0));
        Assert.Equal(2, mirror.Uploads);
        Assert.Equal(4, device.Recorder!.CountOf(RecordedCommandKind.CopyBufferToTexture));
    }

    /// <summary>
    ///     A clipmap nobody composited is a volume of zeroes, and zero is the value that means
    ///     "surface here" — so uploading one would put a wall across the whole world rather than
    ///     render nothing. Better to say so than to ship the frame.
    /// </summary>
    [Fact]
    public void UploadingAClipmapNobodyCompositedIsRejected() {
        using var device = new NullDevice(new() { Record = true });
        using var mirror = new GlobalDistanceFieldTexture(new GlobalDistanceField(8, 4f, 1));

        var list = device.BeginCommandList();

        Assert.Throws<InvalidOperationException>(() => mirror.Upload(device, list));
    }

    [Fact]
    public void TheNamesAShaderReadsCarryEachLevelsPlaceInTheWorld() {
        var field = Composited(levels: 2);
        using var mirror = new GlobalDistanceFieldTexture(field);
        var parameters = new ParameterCollection();

        mirror.Apply(parameters);

        // The level count is deliberately absent: it is the shader's LevelCount permutation, which is
        // what unrolls the level search so every texture index is a literal. A uniform beside it would
        // be a second number free to disagree with the descriptors actually bound.
        Assert.False(parameters.Has(ParameterKeys.New<float>("ForwardPlus.distanceFieldLevelCount")));

        for (var level = 0; level < 2; level++) {
            var slot = $"ForwardPlus.distanceFieldVolumes[{level}]";

            Assert.Equal(
                field.BoundsOf(level).Minimum,
                parameters.Get(ParameterKeys.New<Vector3>($"{slot}.minimum"))
            );

            Assert.Equal(
                field.MaxDistanceOf(level),
                parameters.Get(ParameterKeys.New<float>($"{slot}.maxDistance"))
            );

            // The reciprocal, because a shader multiplies a world offset into a texture coordinate
            // and a divide per level per step is a divide nobody needs.
            Assert.Equal(
                1f / field.CellSizeOf(level),
                parameters.Get(ParameterKeys.New<float>($"{slot}.inverseCellSize")),
                4
            );
        }
    }

    [Fact]
    public void ADisposedMirrorReleasesWhatItMade() {
        using var device = new NullDevice(new() { Record = true });
        var mirror = new GlobalDistanceFieldTexture(Composited(levels: 2));

        var list = device.BeginCommandList();
        mirror.Upload(device, list);
        mirror.Dispose();

        Assert.False(mirror.IsCreated);

        // And a second dispose is not a second round of destruction.
        mirror.Dispose();
    }

    [Fact]
    public void UploadingAfterDisposalIsRejected() {
        using var device = new NullDevice(new() { Record = true });
        var mirror = new GlobalDistanceFieldTexture(Composited(levels: 1));
        var list = device.BeginCommandList();

        mirror.Dispose();

        Assert.Throws<ObjectDisposedException>(() => mirror.Upload(device, list));
    }

    /// <summary>
    ///     A recorded command reaches the recorder when its list is finished and submitted, not when
    ///     it is called — which is the Null backend being honest about what a command list is.
    /// </summary>
    static void Submit(NullDevice device, ICommandList list) {
        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    static GlobalDistanceField Composited(int levels, int resolution = 8) {
        var field = new GlobalDistanceField(resolution, 4f, levels);

        field.Update(Vector3.Zero, [], parallel: false);

        return field;
    }
}
