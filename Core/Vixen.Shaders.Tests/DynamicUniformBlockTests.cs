// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Which uniform blocks get a dynamic descriptor, now that the shader says so.
/// </summary>
/// <remarks>
///     <para>
///         <b>A block bound at an offset that moves per draw is a dynamic descriptor</b>, and the
///         alternative — a descriptor set per object — is the commonest reason a Vulkan renderer ends
///         up slower than the D3D11 one it replaced. <c>ForwardLightingRenderFeature</c> writes one
///         buffer for the frame and moves an offset; that only works if the layout and the plan the
///         host fills the set from agree, and <c>EffectLoader.KindOf</c> is where they are both
///         decided.
///     </para>
///     <para>
///         ⚠ <b>This was an inference off the set index and nothing tested it.</b> A uniform block in
///         <see cref="DescriptorSetSlot.PerDraw" /> used by any non-compute stage was called dynamic,
///         so one number carried two claims at once — where a binding lives, and whether its contents
///         change between draws — and a shader wanting the first without the second could not say so.
///         Raven's <c>[DynamicOffset]</c> is the declaration, and these are the assertions the
///         inference never had: the fault it was written to fix was found by a GPU fault at a draw.
///     </para>
/// </remarks>
public class DynamicUniformBlockTests {
    /// <summary>An effect of exactly one binding, so the plan and the layout are both one thing.</summary>
    static EffectData Data(EffectBindingData binding) =>
        new() {
            ShaderName = "Lit",
            Target = "spirv",
            SourceHash = "abc",
            Stages = [new(ShaderStage.Fragment, [1, 2, 3, 4])],
            Bindings = [binding]
        };

    /// <summary>A uniform block, in a set, used by a stage — with or without the declaration.</summary>
    static EffectBindingData Block(DescriptorSetSlot slot, ShaderStage stages, bool declared) =>
        new("lights", slot, 0, DescriptorKind.UniformBuffer, stages, DynamicOffset: declared) { Size = 256 };

    static DescriptorKind KindOf(EffectBindingData binding) {
        using var device = new NullDevice();

        return new EffectLoader(device).Load(Data(binding)).BindingOf("lights")!.Value.Kind;
    }

    /// <summary>The shader said the block moves, so its descriptor is a dynamic one.</summary>
    [Fact]
    public void A_block_the_shader_marks_is_dynamic() =>
        Assert.Equal(
            DescriptorKind.DynamicUniformBuffer,
            KindOf(Block(DescriptorSetSlot.PerDraw, ShaderStage.Vertex | ShaderStage.Fragment, declared: true))
        );

    /// <summary>
    ///     ⚠ And a per-draw block the shader did <i>not</i> mark is plain, which is the half the old
    ///     inference could not express.
    /// </summary>
    /// <remarks>
    ///     This is the discriminating assertion. Under the rule this replaces the very same binding
    ///     came back <see cref="DescriptorKind.DynamicUniformBuffer" />, because it stood in set 3 and
    ///     a fragment stage read it — nothing else was consulted, and nothing else could be said.
    /// </remarks>
    [Fact]
    public void A_per_draw_block_the_shader_does_not_mark_is_plain() =>
        Assert.Equal(
            DescriptorKind.UniformBuffer,
            KindOf(Block(DescriptorSetSlot.PerDraw, ShaderStage.Vertex | ShaderStage.Fragment, declared: false))
        );

    /// <summary>
    ///     The compute carve-out is gone, because the declaration made it unnecessary.
    /// </summary>
    /// <remarks>
    ///     The inference needed one: "per draw" is a claim about draws and a dispatch has none, so a
    ///     compute shader marking a block <c>[PerDraw]</c> was using the set index for storage rather
    ///     than saying anything about draws — <c>BindlessProbe</c> exactly. With the claim declared
    ///     instead of read off the index, the stage stops being part of the question: a compute block
    ///     that says it is bound at an offset is, and one that says nothing is not.
    /// </remarks>
    [Fact]
    public void The_stage_is_no_longer_part_of_the_question() {
        Assert.Equal(
            DescriptorKind.UniformBuffer,
            KindOf(Block(DescriptorSetSlot.PerDraw, ShaderStage.Compute, declared: false))
        );

        Assert.Equal(
            DescriptorKind.DynamicUniformBuffer,
            KindOf(Block(DescriptorSetSlot.PerDraw, ShaderStage.Compute, declared: true))
        );
    }

    /// <summary>Nor is the set: a marked block in any set is bound at an offset.</summary>
    /// <remarks>
    ///     The set index is back to meaning one thing — where the binding lives. A per-material block
    ///     that a host offsets per draw is unusual and was previously unsayable; it is now a
    ///     declaration rather than a refusal the author cannot act on.
    /// </remarks>
    [Fact]
    public void Nor_is_the_set() =>
        Assert.Equal(
            DescriptorKind.DynamicUniformBuffer,
            KindOf(Block(DescriptorSetSlot.PerMaterial, ShaderStage.Fragment, declared: true))
        );

    /// <summary>Only a uniform block. A storage buffer has no dynamic form here.</summary>
    [Fact]
    public void Only_a_uniform_block_is_ever_dynamic() {
        using var device = new NullDevice();

        var storage = new EffectBindingData(
            "lights",
            DescriptorSetSlot.PerDraw,
            0,
            DescriptorKind.StorageBuffer,
            ShaderStage.Fragment,
            DynamicOffset: true
        );

        Assert.Equal(DescriptorKind.StorageBuffer, new EffectLoader(device).Load(Data(storage)).BindingOf("lights")!.Value.Kind);
    }

    /// <summary>
    ///     The layout and the plan agree, which is the property the whole arrangement rests on.
    /// </summary>
    /// <remarks>
    ///     ⚠ A layout that says dynamic and a plan that says plain writes the wrong descriptor type
    ///     into a correct layout: no driver checks it, the shader reads whichever it was compiled
    ///     for, and every object draws with the first one's block. That is the fault this decision was
    ///     moved into <c>KindOf</c> to make impossible — both halves are built from one answer — and
    ///     this is the assertion that says so, by writing the plan's own kind through the loader's own
    ///     layout and having the device accept it.
    /// </remarks>
    [Fact]
    public void The_layout_and_the_plan_are_built_from_one_answer() {
        using var device = new NullDevice();

        var effect = new EffectLoader(device)
            .Load(Data(Block(DescriptorSetSlot.PerDraw, ShaderStage.Vertex | ShaderStage.Fragment, declared: true)));

        var binding = effect.BindingOf("lights")!.Value;
        var layout = effect.SetLayouts[(int)DescriptorSetSlot.PerDraw];
        var set = device.CreateDescriptorSet(layout);
        var buffer = device.CreateBuffer(new(1024, BufferUsage.Uniform, MemoryAccess.HostUpload, "Lights"));

        Assert.Equal(DescriptorKind.DynamicUniformBuffer, binding.Kind);

        // The write the plan describes goes through the layout the same load built.
        device.UpdateDescriptorSet(set, [DescriptorWrite.DynamicUniform(binding.Binding, buffer, 0, binding.Size)]);

        // And the other kind is refused, naming both — the loud failure the inference relied on.
        var thrown = Assert.Throws<ArgumentException>(
            () => device.UpdateDescriptorSet(set, [DescriptorWrite.Uniform(binding.Binding, buffer, 0, binding.Size)])
        );

        Assert.Contains("DynamicUniformBuffer", thrown.Message, StringComparison.Ordinal);
    }
}
