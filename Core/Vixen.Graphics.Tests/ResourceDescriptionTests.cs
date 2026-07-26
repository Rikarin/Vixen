// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Tests;

public class ResourceDescriptionTests {
    [Fact]
    public void AValidBufferPassesValidation() =>
        new BufferDescription(1024, BufferUsage.Vertex | BufferUsage.CopyDestination, Name: "Mesh").Validate();

    [Fact]
    public void ABufferWithNoUsageIsRejectedBecauseNothingCouldBindIt() {
        var thrown = Assert.Throws<ArgumentException>(
            () => new BufferDescription(1024, BufferUsage.None, Name: "Orphan").Validate()
        );

        Assert.Contains("Orphan", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyBufferIsRejected() =>
        Assert.Throws<ArgumentException>(() => new BufferDescription(0, BufferUsage.Vertex).Validate());

    /// <summary>
    ///     A readback buffer nothing can copy into is a readback buffer that will always be empty,
    ///     and the failure would show up as a black screenshot rather than as an error.
    /// </summary>
    [Fact]
    public void AReadbackBufferThatIsNotACopyDestinationIsRejected() {
        var thrown = Assert.Throws<ArgumentException>(
            () => new BufferDescription(64, BufferUsage.Storage, MemoryAccess.HostReadback, "Readback").Validate()
        );

        Assert.Contains("copy destination", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AValidTexturePassesValidation() =>
        new TextureDescription(PixelFormat.Rgba8UNormSrgb, 256, 256, TextureUsage.Sampled, Name: "Albedo")
            .Validate();

    [Fact]
    public void AMipCountOfZeroMeansTheWholeChain() {
        var texture = new TextureDescription(PixelFormat.Rgba8UNorm, 256, 256, TextureUsage.Sampled, MipLevels: 0);

        Assert.Equal(9, texture.EffectiveMipLevels);
    }

    /// <summary>
    ///     A full 256×256 RGBA chain is 4/3 of its base level, and the total is what a staging buffer
    ///     has to be — so it is worth asserting against the arithmetic rather than against itself.
    /// </summary>
    [Fact]
    public void TheTotalSizeIsEveryLevelAndEveryLayer() {
        var texture = new TextureDescription(PixelFormat.Rgba8UNorm, 256, 256, TextureUsage.Sampled, MipLevels: 0);

        var expected = 0L;

        for (var level = 0; level < 9; level++) {
            var size = 256 >> level;
            expected += (long)size * size * 4;
        }

        Assert.Equal(expected, texture.TotalSize);
        Assert.Equal(expected * 6, texture with { ArrayLayers = 6 } is var cube ? cube.TotalSize : 0);
    }

    [Fact]
    public void ATextureWithNoFormatOrNoUsageIsRejected() {
        Assert.Throws<ArgumentException>(
            () => new TextureDescription(PixelFormat.Undefined, 4, 4, TextureUsage.Sampled).Validate()
        );

        Assert.Throws<ArgumentException>(
            () => new TextureDescription(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.None).Validate()
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void ASampleCountThatIsNotAPowerOfTwoIsRejected(int samples) =>
        Assert.Throws<ArgumentException>(
            () => new TextureDescription(
                PixelFormat.Rgba8UNorm,
                4,
                4,
                TextureUsage.ColourTarget,
                SampleCount: samples
            ).Validate()
        );

    /// <summary>
    ///     No API allows a multisampled image to have mip levels: it has to be resolved before it
    ///     can be filtered. Catching it here names the problem; letting it through produces a
    ///     validation-layer message about image creation flags.
    /// </summary>
    [Fact]
    public void AMultisampledTextureMayNotHaveMips() {
        var thrown = Assert.Throws<ArgumentException>(
            () => new TextureDescription(
                PixelFormat.Rgba8UNorm,
                256,
                256,
                TextureUsage.ColourTarget,
                MipLevels: 4,
                SampleCount: 4,
                Name: "Msaa"
            ).Validate()
        );

        Assert.Contains("resolved", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACubeTextureNeedsAWholeNumberOfCubes() {
        Assert.Throws<ArgumentException>(
            () => new TextureDescription(
                PixelFormat.Rgba8UNorm,
                64,
                64,
                TextureUsage.Sampled,
                ArrayLayers: 4,
                Dimension: TextureDimension.TextureCube
            ).Validate()
        );

        new TextureDescription(
            PixelFormat.Rgba8UNorm,
            64,
            64,
            TextureUsage.Sampled,
            ArrayLayers: 12,
            Dimension: TextureDimension.TextureCube
        ).Validate();
    }

    [Fact]
    public void ADepthFormatMayNotBeStorage() =>
        Assert.Throws<ArgumentException>(
            () => new TextureDescription(
                PixelFormat.Depth32Float,
                256,
                256,
                TextureUsage.Storage
            ).Validate()
        );

    /// <summary>
    ///     Depth is reversed — near is 1, far is 0 — so a depth attachment must clear to 0 by
    ///     default. Clearing to 1 is the classic mistake and produces a scene that depth-tests away
    ///     entirely, which looks like nothing rendering at all.
    /// </summary>
    [Fact]
    public void ADepthAttachmentClearsToFarWhichIsZero() {
        var attachment = new DepthStencilAttachment(TextureViewHandle.Null);

        Assert.Equal(0f, attachment.ClearDepth);
        Assert.Equal(LoadAction.Clear, attachment.DepthLoad);
    }

    /// <summary>
    ///     A shadow sampler compares with <c>GreaterEqual</c> for the same reason, and clamps to a
    ///     white border so a lookup outside the map reads as lit rather than as shadowed.
    /// </summary>
    [Fact]
    public void TheShadowSamplerMatchesTheReversedDepthConvention() {
        var sampler = SamplerDescription.Shadow;

        Assert.Equal(CompareFunction.GreaterEqual, sampler.Compare);
        Assert.Equal(BorderColour.OpaqueWhite, sampler.Border);
        Assert.Equal(AddressMode.ClampToBorder, sampler.AddressU);
    }

    [Fact]
    public void TheStockSamplersAreWhatTheirNamesSay() {
        Assert.Equal(AddressMode.Repeat, SamplerDescription.LinearRepeat.AddressU);
        Assert.Equal(FilterMode.Linear, SamplerDescription.LinearClamp.MinFilter);
        Assert.Equal(AddressMode.ClampToEdge, SamplerDescription.LinearClamp.AddressV);
        Assert.Equal(FilterMode.Nearest, SamplerDescription.PointClamp.MagFilter);
    }

    /// <summary>
    ///     Handles are typed so that a buffer cannot be passed where a texture is wanted, which is
    ///     the entire reason each one wraps <c>Handle&lt;T&gt;</c> rather than being a bare integer.
    /// </summary>
    [Fact]
    public void ANullHandleIsInvalidAndADistinctHandleIsNot() {
        Assert.False(BufferHandle.Null.IsValid);
        Assert.False(TextureHandle.Null.IsValid);
        Assert.True(new BufferHandle(new(1, 1)).IsValid);
        Assert.NotEqual(new BufferHandle(new(1, 1)), new BufferHandle(new(1, 2)));
    }
}

public class GraphicsDeviceFeaturesTests {
    /// <summary>
    ///     A capability left unset reports absent, so a backend that forgets a line takes the
    ///     fallback path rather than claiming something it cannot do.
    /// </summary>
    [Fact]
    public void TheMinimumClaimsNothingItDoesNotHave() {
        var features = GraphicsDeviceFeatures.Minimum;

        Assert.False(features.HasCompute);
        Assert.False(features.HasBindless);
        Assert.False(features.HasMeshShaders);
        Assert.False(features.HasAsyncCompute);
        Assert.False(features.HasDynamicRendering);
    }

    [Fact]
    public void TheMinimumMeetsTheFloorTheDocumentationStates() {
        var features = GraphicsDeviceFeatures.Minimum;

        Assert.True(features.MaxTextureSize >= 4096);
        Assert.True(features.MaxColourAttachments >= 4);

        // Four sets, because the engine's convention is per-frame, per-view, per-material, per-draw.
        Assert.True(features.MaxDescriptorSets >= 4);
    }

    [Fact]
    public void SampleCountsAreReadOutOfTheMask() {
        var features = GraphicsDeviceFeatures.Minimum with { SupportedSampleCounts = 0b10101 };

        Assert.True(features.SupportsSampleCount(1));
        Assert.False(features.SupportsSampleCount(2));
        Assert.True(features.SupportsSampleCount(4));
        Assert.False(features.SupportsSampleCount(8));
        Assert.True(features.SupportsSampleCount(16));
    }

    [Fact]
    public void ASampleCountThatIsNotAPowerOfTwoIsNeverSupported() {
        var features = GraphicsDeviceFeatures.Minimum with { SupportedSampleCounts = ~0 };

        Assert.False(features.SupportsSampleCount(0));
        Assert.False(features.SupportsSampleCount(3));
        Assert.False(features.SupportsSampleCount(-4));
    }

    [Fact]
    public void ItComparesByValue() {
        var first = GraphicsDeviceFeatures.Minimum with { HasCompute = true };
        var second = GraphicsDeviceFeatures.Minimum with { HasCompute = true };

        Assert.Equal(first, second);
        Assert.NotEqual(first, GraphicsDeviceFeatures.Minimum);
    }
}
