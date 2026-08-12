// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The GPU mirror of an irradiance field: what it allocates, what it copies, and what it names.
/// </summary>
/// <remarks>
///     Everything about what the field <i>says</i> is checked without a device in
///     <c>Vixen.Rendering.IrradianceFields.Tests</c>, against closed forms. What is left for a device
///     is allocate, pack, stage, copy and name — so this asserts on the recorded command stream and on
///     the parameter names, because a copy is the only observable thing an upload does.
/// </remarks>
public class IrradianceFieldTextureTests {
    const string Slot = "Deferred.IrradianceFieldProbes";

    [Fact]
    public void FourPoolVolumesAndAnIndexEachGetOneCopy() {
        using var device = new NullDevice(new() { Record = true });
        using var mirror = new IrradianceFieldTexture(Filled());

        var list = device.BeginCommandList();
        mirror.Upload(device, list);
        Submit(device, list);

        Assert.True(mirror.IsCreated);
        Assert.Equal(1, mirror.Uploads);
        Assert.Equal(5, device.Recorder!.CountOf(RecordedCommandKind.CopyBufferToTexture));

        // Four textures, not one resource sliced four ways: a 3D texture cannot be an array layer.
        for (var channel = 0; channel < 4; channel++) {
            Assert.True(mirror.Pool(channel).IsValid);
            Assert.True(mirror.PoolView(channel).IsValid);
        }

        Assert.Equal(4, new[] { mirror.Pool(0), mirror.Pool(1), mirror.Pool(2), mirror.Pool(3) }.Distinct().Count());
        Assert.True(mirror.Indirection.IsValid);

        // Two samplers, because the two volumes are read differently: the pool filters and the index
        // must not — interpolating two slot indices gives a third that means nothing.
        Assert.True(mirror.Sampler.IsValid);
        Assert.True(mirror.PointSampler.IsValid);
        Assert.NotEqual(mirror.Sampler, mirror.PointSampler);
    }

    /// <summary>
    ///     One staging buffer, each volume written at its own offset. Reusing one small region would
    ///     overwrite bytes a copy has been recorded against but not yet run.
    /// </summary>
    [Fact]
    public void VolumesAreStagedAtDistinctOffsetsOfOneBuffer() {
        var field = Filled();
        using var device = new NullDevice(new() { Record = true });
        using var mirror = new IrradianceFieldTexture(field);

        var list = device.BeginCommandList();
        mirror.Upload(device, list);
        Submit(device, list);

        var copies = device.Recorder!.Commands
            .Where(command => command.Kind == RecordedCommandKind.CopyBufferToTexture)
            .ToArray();

        Assert.Equal(5, copies.Length);
        Assert.Single(copies.Select(copy => copy.A).Distinct());
        Assert.Equal(5, copies.Select(copy => copy.B).Distinct().Count());

        var stride = (long)field.Pool.Texels.Length * 4 * sizeof(float);

        Assert.Equal([0L, stride, stride * 2, stride * 3, stride * 4], copies.Select(copy => copy.B).Order());
    }

    [Fact]
    public void TheResourcesAreMadeOnceAndReusedByLaterUploads() {
        using var device = new NullDevice(new() { Record = true });
        using var mirror = new IrradianceFieldTexture(Filled());

        var list = device.BeginCommandList();
        mirror.Upload(device, list);

        var first = mirror.Pool(0);
        var second = device.BeginCommandList();

        mirror.Upload(device, second);
        Submit(device, list);
        Submit(device, second);

        Assert.Equal(first, mirror.Pool(0));
        Assert.Equal(2, mirror.Uploads);
        Assert.Equal(10, device.Recorder!.CountOf(RecordedCommandKind.CopyBufferToTexture));
    }

    /// <summary>
    ///     <b>The volumes are transitioned by whoever owns them, which is this and nothing else.</b>
    ///     They are named into a descriptor set rather than read through the render graph, so the graph
    ///     does not know they exist — and a texture never moved out of UNDEFINED is a validation error
    ///     at the copy, while one left in TRANSFER_DST is one at the draw that samples it. Both were
    ///     real until a frame tried to trace.
    /// </summary>
    [Fact]
    public void TheVolumesAreMovedIntoAndOutOfBeingCopiedInto() {
        using var device = new NullDevice(new() { Record = true });
        using var mirror = new IrradianceFieldTexture(Filled());

        var list = device.BeginCommandList();
        mirror.Upload(device, list);
        Submit(device, list);

        var barriers = device.Recorder!.Commands
            .Where(command => command.Kind == RecordedCommandKind.Barrier)
            .ToArray();

        // The pool and the index separately, before and after: four volumes and one, twice each. They
        // are apart because a GPU-filled pool is transitioned by whoever dispatches and the index
        // never is — see PoolIsWritten.
        Assert.Equal(4, barriers.Length);
        Assert.Equal([1, 1, 4, 4], barriers.Select(barrier => barrier.B).Order());
    }

    /// <summary>
    ///     <b>With the pool written by a compute shader the probes are not copied at all</b>, and the
    ///     index volume still is: allocation and refinement stay a CPU decision, and only the probes
    ///     move. Uploading both would overwrite whatever the dispatch just wrote, one frame after it
    ///     wrote it — lighting that flickers between two answers rather than a mode nobody chose.
    /// </summary>
    [Fact]
    public void AWrittenPoolIsNotCopiedUp() {
        using var device = new NullDevice(new() { Record = true });
        using var mirror = new IrradianceFieldTexture(Filled()) { PoolIsWritten = true };

        var list = device.BeginCommandList();
        mirror.Upload(device, list);
        Submit(device, list);

        Assert.Equal(1, device.Recorder!.CountOf(RecordedCommandKind.CopyBufferToTexture));
    }

    /// <summary>
    ///     <b>The index volume holds integers, and half-precision holds them exactly only to 2048.</b>
    ///     A brick origin that rounds puts every probe of that brick somewhere else, which is lighting
    ///     from the wrong part of the world with nothing to suggest why — so a pool that cannot be
    ///     indexed is refused rather than silently mis-indexed.
    /// </summary>
    [Fact]
    public void APoolTooLargeToIndexInHalfPrecisionIsRefused() {
        var field = new IrradianceField(
            new BoundingBox(new(0f), new(64f)),
            new(4),
            new IrradianceBrickPool(new(410, 1, 1))
        );

        using var device = new NullDevice(new() { Record = true });
        using var mirror = new IrradianceFieldTexture(field);

        var list = device.BeginCommandList();

        Assert.Throws<InvalidOperationException>(() => mirror.Upload(device, list));
    }

    [Fact]
    public void TheNamesAShaderReadsCarryWhereTheFieldIs() {
        var field = Filled();
        using var mirror = new IrradianceFieldTexture(field);
        var parameters = new ParameterCollection();

        field.NormalBias = 0.375f;
        field.ViewBias = 0.625f;
        mirror.Apply(parameters, Slot);

        Assert.Equal(
            field.Bounds.Minimum,
            parameters.Get(ParameterKeys.New<Vector3>($"{Slot}.irradianceField.minimum"))
        );

        Assert.Equal(
            field.Bounds.Maximum,
            parameters.Get(ParameterKeys.New<Vector3>($"{Slot}.irradianceField.maximum"))
        );

        // The reciprocal, because a shader multiplies a world offset into a cell coordinate and a
        // divide per sample is a divide nobody needs.
        Assert.Equal(
            Vector3.One / field.Indirection.CellSize,
            parameters.Get(ParameterKeys.New<Vector3>($"{Slot}.irradianceField.inverseCellSize"))
        );

        // The resolution goes up as it is, because the shader clamps against it rather than dividing.
        Assert.Equal(
            new Vector3(4f, 4f, 4f),
            parameters.Get(ParameterKeys.New<Vector3>($"{Slot}.irradianceField.resolution"))
        );

        var texels = field.Pool.TexelResolution;

        Assert.Equal(
            Vector3.One / new Vector3(texels.X, texels.Y, texels.Z),
            parameters.Get(ParameterKeys.New<Vector3>($"{Slot}.irradianceField.inversePoolSize"))
        );

        // Two different numbers, because the two biases are two members of one block and a mirror
        // that wrote the same value into both would pass any test that used one number for both.
        Assert.Equal(0.375f, parameters.Get(ParameterKeys.New<float>($"{Slot}.irradianceField.normalBias")));
        Assert.Equal(0.625f, parameters.Get(ParameterKeys.New<float>($"{Slot}.irradianceField.viewBias")));
    }

    /// <summary>
    ///     The volumes go in beside the numbers describing them. Writing one without the other is a
    ///     shader told exactly where to look in a texture nothing bound.
    /// </summary>
    [Fact]
    public void TheVolumesThemselvesAreBoundBesideTheNumbers() {
        using var device = new NullDevice(new() { Record = true });
        using var mirror = new IrradianceFieldTexture(Filled());
        var parameters = new ParameterCollection();

        var list = device.BeginCommandList();
        mirror.Upload(device, list);
        Submit(device, list);

        mirror.Apply(parameters, Slot);

        for (var channel = 0; channel < 4; channel++) {
            var view = parameters.Get(
                ParameterKeys.New<TextureViewHandle>(IrradianceFieldTexture.PoolBinding(channel, Slot))
            );

            Assert.True(view.IsValid);
            Assert.Equal(mirror.PoolView(channel), view);
        }

        Assert.Equal(
            mirror.IndirectionView,
            parameters.Get(ParameterKeys.New<TextureViewHandle>(IrradianceFieldTexture.IndirectionBinding(Slot)))
        );

        Assert.Equal(
            mirror.Sampler,
            parameters.Get(ParameterKeys.New<SamplerHandle>(IrradianceFieldTexture.SamplerBinding(Slot)))
        );

        Assert.Equal(
            mirror.PointSampler,
            parameters.Get(ParameterKeys.New<SamplerHandle>(IrradianceFieldTexture.PointSamplerBinding(Slot)))
        );
    }

    /// <summary>
    ///     Before an upload the numbers go in and the seven handles do not, so the set refuses rather
    ///     than completing over descriptors naming nothing.
    /// </summary>
    /// <remarks>
    ///     <c>GlobalDistanceFieldTexture</c>'s rule and its reason. <c>EffectSetWriter</c> asks whether a
    ///     name is <em>set</em>, not whether what it is set to exists, so a default handle is counted as
    ///     a filled descriptor and the whole set completes — which is how a fixture came to assert that
    ///     seven dead <c>IrradianceFieldProbes.*</c> descriptors were a complete set. Omitting the name
    ///     refuses the set, which names the binding and does it on the first frame.
    /// </remarks>
    [Fact]
    public void BeforeAnUploadTheHandlesAreOmittedRatherThanWrittenDead() {
        using var mirror = new IrradianceFieldTexture(Filled());
        var parameters = new ParameterCollection();

        mirror.Apply(parameters, Slot);

        // The numbers are the field's and need no device, so they go in either way.
        Assert.True(parameters.Has(ParameterKeys.New<Vector3>($"{Slot}.irradianceField.minimum")));

        for (var channel = 0; channel < 4; channel++) {
            Assert.False(
                parameters.Has(ParameterKeys.New<TextureViewHandle>(IrradianceFieldTexture.PoolBinding(channel, Slot))),
                $"pool volume {channel} was named before anything created it"
            );
        }

        Assert.False(
            parameters.Has(ParameterKeys.New<TextureViewHandle>(IrradianceFieldTexture.IndirectionBinding(Slot))),
            "the index volume was named before anything created it"
        );

        Assert.False(
            parameters.Has(ParameterKeys.New<SamplerHandle>(IrradianceFieldTexture.SamplerBinding(Slot))),
            "the sampler was named before anything created it"
        );

        Assert.False(
            parameters.Has(ParameterKeys.New<SamplerHandle>(IrradianceFieldTexture.PointSamplerBinding(Slot))),
            "the point sampler was named before anything created it"
        );
    }

    /// <summary>
    ///     The names are the slot's, not the declaring shader's — the shape the bindings generator
    ///     actually emits, and the mistake the distance-field mirror had to be corrected for.
    /// </summary>
    [Fact]
    public void TheNamesAreTheComposeSlotsRatherThanTheShadersThatDeclaredThem() {
        Assert.Equal($"{Slot}.irradianceL0", IrradianceFieldTexture.PoolBinding(0, Slot));
        Assert.Equal($"{Slot}.irradianceL1R", IrradianceFieldTexture.PoolBinding(1, Slot));
        Assert.Equal($"{Slot}.irradianceL1G", IrradianceFieldTexture.PoolBinding(2, Slot));
        Assert.Equal($"{Slot}.irradianceL1B", IrradianceFieldTexture.PoolBinding(3, Slot));
        Assert.Equal($"{Slot}.irradianceIndirection", IrradianceFieldTexture.IndirectionBinding(Slot));
        Assert.Equal($"{Slot}.irradianceSampler", IrradianceFieldTexture.SamplerBinding(Slot));
        Assert.Equal($"{Slot}.irradiancePointSampler", IrradianceFieldTexture.PointSamplerBinding(Slot));
    }

    /// <summary>
    ///     Half storage halves the staging buffer, and it is a choice with a price on it — see the
    ///     type's remarks. The index volume is half either way, because it never filters.
    /// </summary>
    [Fact]
    public void HalfStorageHalvesThePool() {
        var field = Filled();

        using var device = new NullDevice(new() { Record = true });
        using var wide = new IrradianceFieldTexture(field);
        using var narrow = new IrradianceFieldTexture(field) { Format = PixelFormat.Rgba16Float };

        var first = device.BeginCommandList();
        wide.Upload(device, first);
        Submit(device, first);

        var second = device.BeginCommandList();
        narrow.Upload(device, second);
        Submit(device, second);

        var offsets = device.Recorder!.Commands
            .Where(command => command.Kind == RecordedCommandKind.CopyBufferToTexture)
            .Select(command => command.B)
            .ToArray();

        // The second volume of each upload starts at one volume's worth of bytes, and the narrow one
        // is half of that.
        Assert.Equal(offsets[1], offsets[6] * 2);
    }

    /// <summary>A field of one brick, filled with something distinguishable from zero.</summary>
    static IrradianceField Filled() {
        var field = new IrradianceField(new BoundingBox(new(-2f), new(2f)), new(4));

        field.AllocateAll(4);
        field.SyncBorders();

        return field;
    }

    static void Submit(NullDevice device, ICommandList list) {
        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }
}
