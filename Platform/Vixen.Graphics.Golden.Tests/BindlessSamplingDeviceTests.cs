// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Vulkan;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     A shader indexing a bindless table, compiled from Raven and run on a driver.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The gate <c>docs/plan/23-bindless-materials.md</c> says is not optional.</strong> Every
///         compile-time test of <c>Texture2D[]</c> asserts something structural — a runtime array with
///         no stride, two capabilities, two <c>NonUniform</c> decorations — and all of it can be
///         right while the shader still reads one descriptor for a whole subgroup. That failure has
///         no error and no visible symptom in the case a test would naturally write: a draw using
///         *one* material samples the same slot in every invocation, so the wrong answer and the
///         right answer are the same number.
///     </para>
///     <para>
///         So every invocation here reads a <em>different</em> slot, and the readback is the identity
///         of the texture each one reached. A hoisted descriptor load comes back as sixty-four copies
///         of one value, which is a failure nothing else in the tree can produce.
///     </para>
///     <para>
///         The real compiler and the shader's own descriptor plan, for the reason
///         <see cref="ClusterCullingDeviceTests" /> gives: a stand-in written here would agree with
///         what was written here. The layout the table is allocated from is
///         <see cref="Effect.SetLayouts" />'s — so what the driver is handed is what Raven said,
///         including the <c>Count == 0</c> the RHI turns into an unbounded binding.
///     </para>
///     <para>
///         Serialised with the rest of the driver tests: <see cref="VulkanDiagnostics" /> is
///         process-wide.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class BindlessSamplingDeviceTests {
    /// <summary>How many slots the table is filled with, and how many invocations read one.</summary>
    /// <remarks>
    ///     Past one subgroup on every part worth testing — 32 on NVIDIA, 32 or 64 on AMD, 32 on
    ///     Apple. A count inside one subgroup would let a driver that reads one descriptor per
    ///     subgroup still produce a few distinct values, which is a partial failure that reads as
    ///     noise rather than as the shape of the bug.
    /// </remarks>
    const int Slots = 64;

    /// <summary>
    ///     Each invocation reads its own slot, and gets its own texture back.
    /// </summary>
    [Fact]
    public void Every_invocation_reaches_the_slot_it_asked_for() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        if (!BindlessTable.IsSupportedBy(device)) {
            // MoltenVK below Metal argument-buffer tier 2 (ADR-011). A legitimate no, and the whole
            // reason VulkanFeatures.Bindless asks the features rather than the extension string.
            return;
        }

        VulkanDiagnostics.Reset();

        var effect = Compiled(device);
        var table = effect.SetLayouts[(int)DescriptorSetSlot.PerFrame];
        var draw = effect.SetLayouts[(int)DescriptorSetSlot.PerDraw];

        Assert.True(table.IsValid, "the shader's per-frame set has no layout, so the table is not where it says");

        // The distinct value each slot's texture carries. Non-zero and not equal to the index, so a
        // table that was never written, one read off by one, and one that returned the index rather
        // than the texel are three different failures.
        var expected = new byte[Slots];

        for (var slot = 0; slot < Slots; slot++) {
            expected[slot] = (byte)(11 + (slot * 3));
        }

        var textures = new TextureHandle[Slots];
        var views = new TextureViewHandle[Slots];
        var staging = device.CreateBuffer(
            new(Slots * 4, BufferUsage.CopySource, MemoryAccess.HostUpload, "bindless staging")
        );

        var pixels = new byte[Slots * 4];

        for (var slot = 0; slot < Slots; slot++) {
            textures[slot] = device.CreateTexture(
                new(
                    PixelFormat.Rgba8UNorm,
                    1,
                    1,
                    TextureUsage.Sampled | TextureUsage.CopyDestination,
                    Name: $"bindless {slot}"
                )
            );

            views[slot] = device.CreateTextureView(textures[slot]);
            pixels[slot * 4] = expected[slot];
            pixels[(slot * 4) + 3] = 255;
        }

        device.Write(staging, 0, pixels);

        var results = device.CreateBuffer(
            new(Slots * 4, BufferUsage.Storage | BufferUsage.CopySource, MemoryAccess.DeviceLocal, "bindless results")
        );

        var readback = device.CreateBuffer(
            new(Slots * 4, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "bindless readback")
        );

        // The count, in the per-draw block the shader declares. Filled through the effect's own
        // member offsets rather than at a byte position written down here.
        var block = device.CreateBuffer(
            new(64, BufferUsage.Uniform, MemoryAccess.HostUpload, "bindless constants")
        );

        var declared = effect.BlockOf(DescriptorSetSlot.PerDraw);
        var constants = new byte[Math.Max(4, declared.Size)];
        var offset = declared.Members.Single(member => member.Key.Name.EndsWith(".count", StringComparison.Ordinal));
        BitConverter.TryWriteBytes(constants.AsSpan(offset.Offset), Slots);
        device.Write(block, 0, constants);

        var tableSet = device.CreateDescriptorSet(table, "bindless table");
        var drawSet = device.CreateDescriptorSet(draw, "bindless draw");

        // One write per slot, at the slot's own index — which is what an unbounded binding is for and
        // what a descriptorCount of one would refuse at element 1.
        for (var slot = 0; slot < Slots; slot++) {
            device.UpdateDescriptorSet(tableSet, [DescriptorWrite.Texture(Binding(effect, "textures"), views[slot], slot)]);
        }

        device.UpdateDescriptorSet(
            drawSet,
            [
                DescriptorWrite.Uniform(declared.Binding, block, 0, constants.Length),
                DescriptorWrite.Storage(Binding(effect, "results"), results)
            ]
        );

        var shader = device.CreateShader(
            ShaderStage.Compute,
            effect.Stages.Single(stage => stage.Stage == ShaderStage.Compute).Bytecode.AsSpan(),
            "BindlessProbe"
        );

        // With whatever the layers said, because the driver's own answer is not one. MoltenVK
        // reports a refused pipeline as ErrorInitializationFailed and puts the reason — a Metal
        // compile log naming a line of translated source — through the debug messenger, so a bare
        // `Check` here fails with four words and no way to find out which four mattered.
        PipelineHandle pipeline;

        try {
            pipeline = device.CreateComputePipeline(new(shader, effect.Layout, "BindlessProbe"));
        } catch (VulkanException error) {
            throw new InvalidOperationException(
                $"{error.Message} The layers said: {string.Join(Environment.NewLine, VulkanDiagnostics.Messages)}",
                error
            );
        }

        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Compute, "bindless")) {
            foreach (var texture in textures) {
                commands.Barrier(new([], [new(texture, ResourceState.Undefined, ResourceState.CopyDestination)]));
            }

            for (var slot = 0; slot < Slots; slot++) {
                commands.CopyBufferToTexture(staging, slot * 4, new(textures[slot]), new(1, 1, 1));
            }

            foreach (var texture in textures) {
                commands.Barrier(new([], [new(texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]));
            }

            commands.Barrier(new([new(results, ResourceState.Undefined, ResourceState.ShaderWrite)], []));

            commands.BindPipeline(pipeline);
            commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, tableSet);
            commands.BindDescriptorSet(DescriptorSetSlot.PerDraw, drawSet);
            commands.Dispatch(1, 1, 1);

            commands.Barrier(new([new(results, ResourceState.ShaderWrite, ResourceState.CopySource)], []));
            commands.CopyBuffer(results, 0, readback, 0, Slots * 4);
            commands.Finish();
            device.ComputeQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var bytes = new byte[Slots * 4];
        device.Read(readback, 0, bytes);

        foreach (var view in views) {
            device.Destroy(view);
        }

        foreach (var texture in textures) {
            device.Destroy(texture);
        }

        device.Destroy(pipeline);
        device.Destroy(shader);
        device.Destroy(readback);
        device.Destroy(results);
        device.Destroy(block);
        device.Destroy(staging);

        // ⚠ The two sets as well, and they were the omission. A descriptor set outliving the device
        // is VUID-vkDestroyDevice-device-05137 — reported at the *next* fixture's teardown, since the
        // layers report against the device being destroyed and this file's device is not the one that
        // fails. The message lands in VulkanDiagnostics, which is process-wide, so a test that ran
        // several files later failed with a leak it had not caused.
        device.Destroy(drawSet);
        device.Destroy(tableSet);

        Clean();

        var actual = new byte[Slots];

        for (var slot = 0; slot < Slots; slot++) {
            actual[slot] = (byte)BitConverter.ToUInt32(bytes, slot * 4);
        }

        // Distinct, in order, and each one its own slot's. A hoisted descriptor load is sixty-four
        // copies of one value and fails on the first element that is not slot zero's.
        Assert.Equal(expected, actual);
    }

    /// <summary>
    ///     A colour map in a table samples as linear light, and a linear map beside it does not move.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>The other half of what a table has to get right, and the half nothing
    ///         asserted.</strong> The test above proves an invocation reaches the descriptor it named;
    ///         this proves the number that comes back is the one the surface is entitled to. A
    ///         <c>.meta</c> authors <c>content: Colour</c> for a base colour and <c>Linear</c> for a
    ///         normal or an ORM pack, so exactly one of a material's three maps is sRGB — and a path
    ///         that loses the transfer function loses it on that one alone, which is a level lit
    ///         nearly three times too bright with its normal map and its roughness both visibly
    ///         working.
    ///     </para>
    ///     <para>
    ///         The two formats are asserted in one dispatch, at alternating slots, because "sRGB is
    ///         decoded" and "UNorm is not" are the same claim from two sides: a run where both came
    ///         back at the stored value and a run where both came back decoded are different bugs,
    ///         and a test of one format could not tell them apart.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_colour_map_in_the_table_samples_as_linear_light() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        if (!BindlessTable.IsSupportedBy(device)) {
            return;
        }

        VulkanDiagnostics.Reset();

        var effect = Compiled(device, "BindlessColourProbe");
        var table = effect.SetLayouts[(int)DescriptorSetSlot.PerFrame];
        var draw = effect.SetLayouts[(int)DescriptorSetSlot.PerDraw];

        Assert.True(table.IsValid, "the shader's per-frame set has no layout, so the table is not where it says");

        // One stored byte for every slot, so the only thing that can differ between two answers is
        // the format the texel was read through. 188 is the sRGB encoding of a mid-grey and is far
        // enough from both ends that a decode is unmistakable: 0.737 stored against 0.503 decoded.
        const byte Stored = 188;

        var textures = new TextureHandle[Slots];
        var views = new TextureViewHandle[Slots];
        var srgb = new bool[Slots];

        var staging = device.CreateBuffer(
            new(Slots * 4, BufferUsage.CopySource, MemoryAccess.HostUpload, "colour staging")
        );

        var pixels = new byte[Slots * 4];

        for (var slot = 0; slot < Slots; slot++) {
            srgb[slot] = slot % 2 == 1;

            textures[slot] = device.CreateTexture(
                new(
                    srgb[slot] ? PixelFormat.Rgba8UNormSrgb : PixelFormat.Rgba8UNorm,
                    1,
                    1,
                    TextureUsage.Sampled | TextureUsage.CopyDestination,
                    Name: $"colour {slot}"
                )
            );

            views[slot] = device.CreateTextureView(textures[slot]);
            pixels[slot * 4] = Stored;
            pixels[(slot * 4) + 3] = byte.MaxValue;
        }

        device.Write(staging, 0, pixels);

        var results = device.CreateBuffer(
            new(Slots * 4, BufferUsage.Storage | BufferUsage.CopySource, MemoryAccess.DeviceLocal, "colour results")
        );

        var readback = device.CreateBuffer(
            new(Slots * 4, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "colour readback")
        );

        var block = device.CreateBuffer(
            new(64, BufferUsage.Uniform, MemoryAccess.HostUpload, "colour constants")
        );

        var declared = effect.BlockOf(DescriptorSetSlot.PerDraw);
        var constants = new byte[Math.Max(4, declared.Size)];
        var offset = declared.Members.Single(member => member.Key.Name.EndsWith(".count", StringComparison.Ordinal));
        BitConverter.TryWriteBytes(constants.AsSpan(offset.Offset), Slots);
        device.Write(block, 0, constants);

        // The filter a material table's would be — see WorldRenderer.FilterTextures — because a
        // sampler is the other thing in this path that could turn one texel into a different number.
        var sampler = device.CreateSampler(SamplerDescription.LinearRepeat with { Name = "colour filter" });

        var tableSet = device.CreateDescriptorSet(table, "colour table");
        var drawSet = device.CreateDescriptorSet(draw, "colour draw");

        for (var slot = 0; slot < Slots; slot++) {
            device.UpdateDescriptorSet(
                tableSet,
                [DescriptorWrite.Texture(Binding(effect, "textures"), views[slot], slot)]
            );
        }

        device.UpdateDescriptorSet(
            drawSet,
            [
                DescriptorWrite.Uniform(declared.Binding, block, 0, constants.Length),
                DescriptorWrite.Storage(Binding(effect, "results"), results),
                DescriptorWrite.SamplerAt(Binding(effect, "filter"), sampler)
            ]
        );

        var shader = device.CreateShader(
            ShaderStage.Compute,
            effect.Stages.Single(stage => stage.Stage == ShaderStage.Compute).Bytecode.AsSpan(),
            "BindlessColourProbe"
        );

        PipelineHandle pipeline;

        try {
            pipeline = device.CreateComputePipeline(new(shader, effect.Layout, "BindlessColourProbe"));
        } catch (VulkanException error) {
            throw new InvalidOperationException(
                $"{error.Message} The layers said: {string.Join(Environment.NewLine, VulkanDiagnostics.Messages)}",
                error
            );
        }

        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Compute, "colour")) {
            foreach (var texture in textures) {
                commands.Barrier(new([], [new(texture, ResourceState.Undefined, ResourceState.CopyDestination)]));
            }

            for (var slot = 0; slot < Slots; slot++) {
                commands.CopyBufferToTexture(staging, slot * 4, new(textures[slot]), new(1, 1, 1));
            }

            foreach (var texture in textures) {
                commands.Barrier(new([], [new(texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]));
            }

            commands.Barrier(new([new(results, ResourceState.Undefined, ResourceState.ShaderWrite)], []));

            commands.BindPipeline(pipeline);
            commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, tableSet);
            commands.BindDescriptorSet(DescriptorSetSlot.PerDraw, drawSet);
            commands.Dispatch(1, 1, 1);

            commands.Barrier(new([new(results, ResourceState.ShaderWrite, ResourceState.CopySource)], []));
            commands.CopyBuffer(results, 0, readback, 0, Slots * 4);
            commands.Finish();
            device.ComputeQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var bytes = new byte[Slots * 4];
        device.Read(readback, 0, bytes);

        foreach (var view in views) {
            device.Destroy(view);
        }

        foreach (var texture in textures) {
            device.Destroy(texture);
        }

        device.Destroy(pipeline);
        device.Destroy(shader);
        device.Destroy(sampler);
        device.Destroy(readback);
        device.Destroy(results);
        device.Destroy(block);
        device.Destroy(staging);

        // The same two, for the same reason as the test above.
        device.Destroy(drawSet);
        device.Destroy(tableSet);

        Clean();

        var stored = Stored / 255f;
        var decoded = MathF.Pow(((Stored / 255f) + 0.055f) / 1.055f, 2.4f);

        for (var slot = 0; slot < Slots; slot++) {
            var actual = BitConverter.ToUInt32(bytes, slot * 4) / 65535f;
            var expected = srgb[slot] ? decoded : stored;
            var other = srgb[slot] ? stored : decoded;

            Assert.True(
                MathF.Abs(actual - expected) < 0.01f,
                $"slot {slot} holds a 1×1 {(srgb[slot] ? "Rgba8UNormSrgb" : "Rgba8UNorm")} texel of {Stored}, "
                + $"which samples as {expected:0.0000}; the shader read {actual:0.0000}. "
                + $"The value for the other format is {other:0.0000}, so a read that landed there is the "
                + "transfer function being applied by the wrong half of the pair."
            );
        }
    }

    /// <summary>Which binding the shader gave a name, rather than a number written down here.</summary>
    static uint Binding(Effect effect, string name) =>
        effect.Bindings.Single(binding => binding.Name == name).Binding;

    /// <summary>Compiles one of the probe shaders and loads it onto the device.</summary>
    /// <remarks>
    ///     Its own file beside the fixtures rather than a string in this test, so it goes through the
    ///     same <see cref="RavenEffectCompiler" /> the content build uses and reaches the device the
    ///     way a shipped shader does.
    /// </remarks>
    static Effect Compiled(VulkanDevice device, string name = "BindlessProbe") {
        var path = Path.Combine(AppContext.BaseDirectory, "Shaders", $"{name}.rvn");
        Assert.True(File.Exists(path), $"the probe shader is not beside the binary at {path}");

        var data = new RavenEffectCompiler([path]).TryGet(EffectKey.Of(name));

        Assert.NotNull(data);
        return new EffectLoader(device).Load(data!);
    }

    /// <summary>Refuses an answer produced alongside validation errors.</summary>
    static void Clean() {
        if (VulkanDiagnostics.ErrorCount > 0) {
            throw new InvalidOperationException(
                "The dispatch produced validation errors, so what came back means nothing: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }
    }

    /// <summary>Skips when there is no device, unless the environment insists on one.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
    }
}
