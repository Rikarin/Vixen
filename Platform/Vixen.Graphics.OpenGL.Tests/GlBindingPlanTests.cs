// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.OpenGL.Tests;

/// <summary>Four descriptor sets flattened into GL's one namespace per resource class.</summary>
public sealed class GlBindingPlanTests {
    /// <summary>Each resource class counts on its own.</summary>
    /// <remarks>
    ///     GL's namespaces are independent — a uniform buffer at binding 0 and a texture at unit 0 do
    ///     not collide — and a shared counter would waste both and push uniform blocks past the
    ///     driver's limit for no reason.
    /// </remarks>
    [Fact]
    public void CountsEachResourceClassSeparately() {
        var plan = GlBindingPlan.Build(
            [
                (
                    DescriptorSetSlot.PerFrame,
                    [
                        new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex),
                        new(1, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                        new(2, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
                        new(3, DescriptorKind.SampledTexture, ShaderStage.Fragment)
                    ],
                    "frame"
                )
            ],
            0
        );

        Assert.Equal(0u, plan.Resolve(DescriptorSetSlot.PerFrame, 0)!.Value.Index);
        Assert.Equal(0u, plan.Resolve(DescriptorSetSlot.PerFrame, 1)!.Value.Index);
        Assert.Equal(1u, plan.Resolve(DescriptorSetSlot.PerFrame, 2)!.Value.Index);
        Assert.Equal(1u, plan.Resolve(DescriptorSetSlot.PerFrame, 3)!.Value.Index);
    }

    /// <summary>Sets are numbered in slot order however the caller ordered the array.</summary>
    /// <remarks>
    ///     <c>PipelineLayoutDescription</c> says "in slot order" and nothing enforces it, so a plan
    ///     that trusted the array would give the same per-frame set different indices in two
    ///     pipelines — and the bind cache would be wrong only when both are used in one frame.
    /// </remarks>
    [Fact]
    public void NumbersInSlotOrderRatherThanArrayOrder() {
        var forwards = GlBindingPlan.Build(
            [
                (DescriptorSetSlot.PerFrame, [new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex)], "frame"),
                (DescriptorSetSlot.PerDraw, [new(0, DescriptorKind.DynamicUniformBuffer, ShaderStage.Vertex)], "draw")
            ],
            0
        );

        var backwards = GlBindingPlan.Build(
            [
                (DescriptorSetSlot.PerDraw, [new(0, DescriptorKind.DynamicUniformBuffer, ShaderStage.Vertex)], "draw"),
                (DescriptorSetSlot.PerFrame, [new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex)], "frame")
            ],
            0
        );

        Assert.Equal(0u, forwards.Resolve(DescriptorSetSlot.PerFrame, 0)!.Value.Index);
        Assert.Equal(1u, forwards.Resolve(DescriptorSetSlot.PerDraw, 0)!.Value.Index);
        Assert.Equal(0u, backwards.Resolve(DescriptorSetSlot.PerFrame, 0)!.Value.Index);
        Assert.Equal(1u, backwards.Resolve(DescriptorSetSlot.PerDraw, 0)!.Value.Index);
    }

    /// <summary>An array binding takes a contiguous run of units.</summary>
    /// <remarks>Which is what <c>uniform sampler2D atlas[4]</c> occupies: units n through n+3.</remarks>
    [Fact]
    public void ReservesARunForAnArrayBinding() {
        var plan = GlBindingPlan.Build(
            [
                (
                    DescriptorSetSlot.PerMaterial,
                    [
                        new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment, 4),
                        new(1, DescriptorKind.SampledTexture, ShaderStage.Fragment)
                    ],
                    "material"
                )
            ],
            0
        );

        Assert.Equal(0u, plan.Resolve(DescriptorSetSlot.PerMaterial, 0)!.Value.Index);
        Assert.Equal(4u, plan.Resolve(DescriptorSetSlot.PerMaterial, 1)!.Value.Index);
    }

    /// <summary>A binding the layout does not declare resolves to nothing rather than throwing.</summary>
    /// <remarks>
    ///     A per-frame set bound to a pipeline that reads none of it is ordinary — it is the whole
    ///     point of ordering sets by change frequency — not an error.
    /// </remarks>
    [Fact]
    public void ResolvesAnAbsentBindingToNothing() {
        var plan = GlBindingPlan.Build([], 0);
        Assert.Null(plan.Resolve(DescriptorSetSlot.PerDraw, 0));
    }

    /// <summary>Push constants are measured in whole vectors, rounded up.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 1)]
    [InlineData(16, 1)]
    [InlineData(17, 2)]
    [InlineData(128, 8)]
    public void RoundsPushConstantsUpToWholeVectors(int bytes, int vectors) {
        Assert.Equal(vectors, GlBindingPlan.Build([], bytes).PushConstantVectors);
    }

    /// <summary>Dynamic buffers share the counter with their static kind.</summary>
    /// <remarks>
    ///     They are the same GL binding point — a dynamic offset is supplied at
    ///     <c>glBindBufferRange</c> time and is not a property of the declaration — so giving them a
    ///     separate namespace would mean two bindings resolving to the same index.
    /// </remarks>
    [Fact]
    public void TreatsDynamicBuffersAsTheSameClass() {
        var plan = GlBindingPlan.Build(
            [
                (
                    DescriptorSetSlot.PerDraw,
                    [
                        new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex),
                        new(1, DescriptorKind.DynamicUniformBuffer, ShaderStage.Vertex)
                    ],
                    "draw"
                )
            ],
            0
        );

        Assert.Equal(0u, plan.Resolve(DescriptorSetSlot.PerDraw, 0)!.Value.Index);
        Assert.Equal(1u, plan.Resolve(DescriptorSetSlot.PerDraw, 1)!.Value.Index);
    }
}
