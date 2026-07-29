// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Tests;

/// <summary>
///     What a descriptor set layout is allowed to say, checked where every backend reads it rather
///     than in the one backend that would refuse it.
/// </summary>
public class DescriptorLayoutTests {
    [Fact]
    public void AShadowMapDeclaresDepthOnBothHalves() {
        var layout = new DescriptorSetLayoutDescription(
            DescriptorSetSlot.PerView,
            [
                new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment, SampleType: DescriptorSampleType.Depth),
                new(1, DescriptorKind.Sampler, ShaderStage.Fragment, SampleType: DescriptorSampleType.Depth)
            ],
            "Shadow"
        );

        layout.Validate();

        Assert.True(layout.Bindings[1].IsComparisonSampler);
        Assert.False(layout.Bindings[1].Filters);
        Assert.False(layout.Bindings[0].IsComparisonSampler);
    }

    /// <summary>An unstated sample type is the common case, which is what every layout used to be.</summary>
    [Fact]
    public void ABindingThatSaysNothingIsAFilterableFloat() {
        var binding = new DescriptorBinding(0, DescriptorKind.SampledTexture, ShaderStage.Fragment);

        Assert.Equal(DescriptorSampleType.Float, binding.SampleType);
        Assert.True(binding.Filters);
        Assert.False(binding.IsComparisonSampler);
    }

    /// <summary>
    ///     A storage image declares a format and a buffer declares nothing of the sort, so a sample
    ///     type on either is a caller who meant something no backend can build.
    /// </summary>
    [Theory]
    [InlineData(DescriptorKind.UniformBuffer)]
    [InlineData(DescriptorKind.StorageBuffer)]
    [InlineData(DescriptorKind.StorageTexture)]
    public void ASampleTypeOnSomethingThatIsNotSampledIsRejected(DescriptorKind kind) {
        var layout = new DescriptorSetLayoutDescription(
            DescriptorSetSlot.PerMaterial,
            [new(0, kind, ShaderStage.Compute, SampleType: DescriptorSampleType.Depth)],
            "Odd"
        );

        var thrown = Assert.Throws<ArgumentException>(layout.Validate);

        Assert.Contains("sample type", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Odd", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABindingIndexUsedTwiceIsRejected() {
        var layout = new DescriptorSetLayoutDescription(
            DescriptorSetSlot.PerMaterial,
            [
                new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                new(0, DescriptorKind.Sampler, ShaderStage.Fragment)
            ],
            "Twice"
        );

        Assert.Throws<ArgumentException>(layout.Validate);
    }

    [Fact]
    public void ABindingNoStageSeesIsRejected() {
        var layout = new DescriptorSetLayoutDescription(
            DescriptorSetSlot.PerMaterial,
            [new(0, DescriptorKind.UniformBuffer, ShaderStage.None)],
            "Unread"
        );

        Assert.Throws<ArgumentException>(layout.Validate);
    }
}
