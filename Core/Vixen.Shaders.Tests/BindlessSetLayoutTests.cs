// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The fifth descriptor set, which only a shader that declares a bindless table gets.
/// </summary>
/// <remarks>
///     <para>
///         A table cannot share one of the four. Sets 0 to 3 are written per frame from a
///         content-addressed <c>DescriptorAllocator</c>, so a set whose write list differs by a byte
///         is a different set; a table's descriptors are written once each and there may be
///         thousands, so a table in set 0 would be written out again whenever a uniform block moved.
///         <c>DescriptorSetSlot.Bindless</c> is the answer and this is where it is built.
///     </para>
///     <para>
///         ⚠ <strong>And only for a shader that declares one.</strong> Vulkan guarantees four bound
///         descriptor sets and no more. A loader that gave every pipeline layout a fifth set would
///         refuse to create pipelines on a device perfectly able to run the shader, for a set the
///         shader never mentions — so the count is the shader's, and the four-set case is what it has
///         always been.
///     </para>
/// </remarks>
public class BindlessSetLayoutTests {
    static EffectData Data(params EffectBindingData[] bindings) =>
        new() {
            ShaderName = "Surface",
            Target = "spirv",
            SourceHash = "abc",
            Stages = [new(ShaderStage.Fragment, [1, 2, 3, 4])],
            Bindings = bindings
        };

    static EffectBindingData Block(DescriptorSetSlot slot, uint binding = 0) =>
        new("block", slot, binding, DescriptorKind.UniformBuffer, ShaderStage.Fragment);

    /// <summary>An unbounded array in set 4, as Raven's <c>[Bindless]</c> reports one.</summary>
    static EffectBindingData Table() =>
        new("materialTextures", DescriptorSetSlot.Bindless, 0, DescriptorKind.SampledTexture, ShaderStage.Fragment) {
            Count = 0
        };

    /// <summary>A shader with no table has the four sets it always had.</summary>
    [Fact]
    public void Without_a_table_a_pipeline_binds_four_sets() {
        using var device = new NullDevice();

        Assert.Equal(4, new EffectLoader(device).Load(Data(Block(DescriptorSetSlot.PerMaterial))).SetLayouts.Length);
    }

    /// <summary>A shader with one binds five.</summary>
    [Fact]
    public void With_a_table_it_binds_five() {
        using var device = new NullDevice();
        var effect = new EffectLoader(device).Load(Data(Block(DescriptorSetSlot.PerMaterial), Table()));

        Assert.Equal(5, effect.SetLayouts.Length);
        Assert.True(effect.SetLayouts[(int)DescriptorSetSlot.Bindless].IsValid);
    }

    /// <summary>
    ///     The table's layout is sized by the loader, not by the device's ceiling.
    /// </summary>
    /// <remarks>
    ///     <strong>What a wrong answer costs.</strong> The shader says the array has no length and the
    ///     layout has to state one anyway, because a descriptor pool is sized from it. Falling back to
    ///     the device's ceiling is legal and ruinous: this device reports five hundred thousand, and a
    ///     pool of five hundred thousand sampled-image descriptors is hundreds of megabytes reserved
    ///     to hold a scene's few thousand textures. Nothing fails — which is why it is asserted.
    /// </remarks>
    [Fact]
    public void The_tables_capacity_is_the_loaders_and_not_the_devices() {
        using var device = new NullDevice();

        var loader = new EffectLoader(device) { BindlessCapacity = 64 };
        var set = device.CreateDescriptorSet(loader.Load(Data(Table())).SetLayouts[(int)DescriptorSetSlot.Bindless]);
        var view = View(device);

        // The last slot the loader asked for, and the first one past it. Observed through a write
        // rather than by reading the layout back, because a write is what the capacity is *for*.
        device.UpdateDescriptorSet(set, [DescriptorWrite.Texture(0, view) with { ArrayIndex = 63 }]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => device.UpdateDescriptorSet(set, [DescriptorWrite.Texture(0, view) with { ArrayIndex = 64 }])
        );

        // And the device would have allowed vastly more, which is what makes the number the loader's.
        Assert.True(device.Features.MaxBindlessDescriptors > 64);
    }

    /// <summary>
    ///     Two capacities are two layouts, because they are two different objects.
    /// </summary>
    /// <remarks>
    ///     The layout cache keys on everything a backend builds a layout from. Capacity is one of
    ///     those things — a pool is sized from it — so leaving it out of the key would hand the second
    ///     loader the first one's layout and size its pool for somebody else's table.
    /// </remarks>
    [Fact]
    public void Two_capacities_are_two_layouts() {
        using var device = new NullDevice();
        var loader = new EffectLoader(device) { BindlessCapacity = 64 };

        var small = loader.Load(Data(Table())).SetLayouts[(int)DescriptorSetSlot.Bindless];
        loader.BindlessCapacity = 256;
        var large = loader.Load(Data(Table())).SetLayouts[(int)DescriptorSetSlot.Bindless];

        Assert.NotEqual(small, large);

        var view = View(device);
        var wide = device.CreateDescriptorSet(large);
        device.UpdateDescriptorSet(wide, [DescriptorWrite.Texture(0, view) with { ArrayIndex = 255 }]);

        var narrow = device.CreateDescriptorSet(small);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => device.UpdateDescriptorSet(narrow, [DescriptorWrite.Texture(0, view) with { ArrayIndex = 255 }])
        );
    }

    /// <summary>Something for a table to hold.</summary>
    static TextureViewHandle View(NullDevice device) =>
        device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.Sampled)),
            new()
        );

    /// <summary>And the four ordinary sets still share their layouts across shaders.</summary>
    /// <remarks>
    ///     The control for the key change above. Sharing is not an economy — a descriptor set
    ///     allocated against one layout object cannot be bound to a pipeline built with a
    ///     structurally identical other — so a key that accidentally separated them would be a frame
    ///     of validation errors rather than a slower frame.
    /// </remarks>
    [Fact]
    public void An_ordinary_set_is_still_shared_between_shaders() {
        using var device = new NullDevice();
        var loader = new EffectLoader(device);

        var first = loader.Load(Data(Block(DescriptorSetSlot.PerFrame)));
        var second = loader.Load(Data(Block(DescriptorSetSlot.PerFrame)) with { ShaderName = "Other" });

        Assert.Equal(
            first.SetLayouts[(int)DescriptorSetSlot.PerFrame],
            second.SetLayouts[(int)DescriptorSetSlot.PerFrame]
        );
    }
}
