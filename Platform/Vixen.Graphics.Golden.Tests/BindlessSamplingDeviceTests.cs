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

        Clean();

        var actual = new byte[Slots];

        for (var slot = 0; slot < Slots; slot++) {
            actual[slot] = (byte)BitConverter.ToUInt32(bytes, slot * 4);
        }

        // Distinct, in order, and each one its own slot's. A hoisted descriptor load is sixty-four
        // copies of one value and fails on the first element that is not slot zero's.
        Assert.Equal(expected, actual);
    }

    /// <summary>Which binding the shader gave a name, rather than a number written down here.</summary>
    static uint Binding(Effect effect, string name) =>
        effect.Bindings.Single(binding => binding.Name == name).Binding;

    /// <summary>Compiles <c>BindlessProbe.rvn</c> and loads it onto the device.</summary>
    /// <remarks>
    ///     Its own file beside the fixtures rather than a string in this test, so it goes through the
    ///     same <see cref="RavenEffectCompiler" /> the content build uses and reaches the device the
    ///     way a shipped shader does.
    /// </remarks>
    static Effect Compiled(VulkanDevice device) {
        var path = Path.Combine(AppContext.BaseDirectory, "Shaders", "BindlessProbe.rvn");
        Assert.True(File.Exists(path), $"the probe shader is not beside the binary at {path}");

        var data = new RavenEffectCompiler([path]).TryGet(EffectKey.Of("BindlessProbe"));

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

    /// <summary>Passes when there is no device, unless the environment insists on one.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }
    }
}
