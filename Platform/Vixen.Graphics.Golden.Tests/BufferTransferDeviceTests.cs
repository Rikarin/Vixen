// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Bytes into a frame and answers back out of it, on a device.
/// </summary>
/// <remarks>
///     <para>
///         The two nodes are asserted structurally against the recording backend — that the copies are
///         declared, that the upload is ordered ahead of its readers and the readback behind its
///         producer, that the ring rotates. None of that can say whether a byte moved, because the
///         null backend has no memory behind a buffer: it validates a write and drops it, and it
///         answers a read with zeroes. <strong>A readback that always returns zeroes agrees with every
///         structural test there is</strong>, which is the failure this fixture exists to rule out.
///     </para>
///     <para>
///         Nothing here records a command list of its own. That is the second thing being asserted:
///         until these nodes existed, every readback in the tree was a hand-written
///         <c>CopyBuffer</c> after <c>Graph.Execute</c>, with a host-created readback buffer and the
///         barrier between the dispatch and the copy left to whatever the import's exit state happened
///         to be. The whole of that is now two nodes in the tree.
///     </para>
///     <para>
///         Serialised with the rest of the driver tests, for the reason
///         <see cref="GoldenImageTests" /> gives: <see cref="VulkanDiagnostics" /> is process-wide,
///         and a fixture that refused its own answer because of another test's validation error is
///         the least useful failure in the suite.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class BufferTransferDeviceTests {
    const int Count = 64;

    /// <summary>
    ///     What the host uploaded is what the host reads back.
    /// </summary>
    /// <remarks>
    ///     Through a buffer the <em>graph</em> owns, which is the case that could not be written before
    ///     at either end: it is device-local, so the host cannot map it, and it has no handle until the
    ///     graph compiles, so nothing outside a pass can name it. Two passes, one transient, and a
    ///     barrier between them that neither node wrote.
    /// </remarks>
    [Fact]
    public void What_is_uploaded_into_a_transient_is_what_comes_back() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var written = new uint[Count];

        for (var i = 0; i < written.Length; i++) {
            // Distinct, non-zero, and not equal to the index — so a copy that landed at the wrong
            // offset, a length that was short, and a buffer that was never written are three
            // different failures rather than one.
            written[i] = 0xA5000000u + (uint)i * 7u + 1u;
        }

        using var system = new RenderSystem();
        using var upload = new BufferUploadRenderer { Name = "Fill", Buffer = "Scratch" };

        using var readback = new BufferReadbackRenderer {
            Name = "Read",
            Buffer = "Scratch",
            Size = Count * sizeof(uint)
        };

        upload.Set<uint>(written);

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Name = "Frame", Children = { upload, readback } }
        };

        compositor.BufferResources.Add(
            new() {
                Name = "Scratch",
                Size = Count * sizeof(uint),
                Usage = BufferUsage.Storage | BufferUsage.CopySource | BufferUsage.CopyDestination
            }
        );

        VulkanDiagnostics.Reset();
        Run(owned, compositor);

        Assert.True(readback.Fetch(), "the readback had nothing to fetch");
        Assert.Equal(written, readback.As<uint>().ToArray());
        Clean();
    }

    /// <summary>
    ///     A readback sees what a dispatch wrote, not what the buffer held before it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The whole chain, with nothing hand-recorded in it: the light list arrives by upload
    ///         node, <c>ClusterCulling.rvn</c> reads it and writes the cluster lists, and the readback
    ///         node brings those home. Every edge between the three is one the graph derived from what
    ///         they declared.
    ///     </para>
    ///     <para>
    ///         <strong>The barrier before the copy is what is actually under test.</strong> Without
    ///         it, the copy reads the cluster buffer as it was when the dispatch started — which is a
    ///         fresh transient's zeroes, and zeroes are a legitimate-looking answer from a culler:
    ///         "no cluster holds any light" is what the mirrored-axis bug produced. So the assertions
    ///         are that lights <em>were</em> found, that they were not found everywhere, and that the
    ///         one behind the camera was found nowhere.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_readback_node_sees_what_a_dispatch_wrote() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var effect = Compiled(device);
        var slot = effect.Bindings[0].Set;
        var camera = RenderCamera.Default with { Position = new(0f, 0f, 4f) };

        // One in front and near, so it falls in some clusters and not others, and one behind the
        // camera, which reaches nothing at all.
        RenderLight[] lights = [
            RenderLight.Point(new(0f, 0f, -6f), 4f, new(1f, 1f, 1f), 1f),
            RenderLight.Point(new(0f, 0f, 40f), 5f, new(1f, 1f, 1f), 1f)
        ];

        var gpu = lights.Select(light => light.ToGpu()).ToArray();

        using var system = new RenderSystem();
        using var allocator = new DescriptorAllocator(device);
        var pipelines = new ComputePipelineCache(device);

        var effects = new EffectSystem();
        effects.AddProvider(new Baked(effect));

        using var upload = new BufferUploadRenderer { Name = "Lights", Buffer = "SceneLights" };

        using var culling = new ComputeRenderer {
            Name = "ClusterCulling",
            ShaderName = "ClusterCulling",
            Pipelines = pipelines,
            Groups = ClusterGrid.GroupCount,
            ConstantBinding = effect.Bindings.Single(binding => binding.Kind == DescriptorKind.UniformBuffer).Binding,
            Descriptors = { Allocator = allocator, Slot = slot }
        };

        using var readback = new BufferReadbackRenderer { Name = "Clusters", Buffer = "Clusters" };

        upload.Set<PunctualLightData>(gpu);

        culling.BufferReads.Add("SceneLights");
        culling.BufferWrites.Add("Clusters");
        culling.Descriptors.Bindings.Add(new() { Name = "lights", Resource = "SceneLights" });
        culling.Descriptors.Bindings.Add(new() { Name = "clusters", Resource = "Clusters" });

        culling.Parameters.Set(ParameterKeys.New<Matrix4x4>("ClusterCulling.view"), camera.View);
        culling.Parameters.Set(ParameterKeys.New<int>("ClusterCulling.lightCount"), lights.Length);
        ClusterGrid.Apply(culling.Parameters, camera, "ClusterCulling");

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Name = "Frame", Children = { upload, culling, readback } }
        };

        compositor.BufferResources.Add(
            new() {
                Name = "SceneLights",
                Size = gpu.Length * Marshal.SizeOf<PunctualLightData>(),
                Usage = BufferUsage.Storage | BufferUsage.CopyDestination
            }
        );

        compositor.BufferResources.Add(
            new() {
                Name = "Clusters",
                Size = ClusterGrid.BufferSize,
                Usage = BufferUsage.Storage | BufferUsage.CopySource
            }
        );

        VulkanDiagnostics.Reset();
        allocator.BeginFrame();
        Run(owned, compositor, effects);

        Assert.True(readback.Fetch(), "the readback had nothing to fetch");

        var clusters = readback.As<ClusterLights>();
        var lit = 0;
        var behind = 0;

        for (var i = 0; i < ClusterGrid.Count; i++) {
            var list = clusters[i];
            var count = (int)Math.Min(list.Count, (uint)ClusterGrid.Capacity);

            if (count > 0) {
                lit++;
            }

            for (var j = 0; j < count; j++) {
                if (list.Indices[j] == 1) {
                    behind++;
                }
            }
        }

        pipelines.Clear();

        Assert.True(lit > 0, "no cluster holds any light, so the readback saw the buffer before the dispatch");
        Assert.True(lit < ClusterGrid.Count, "every cluster holds a light, so nothing was culled");
        Assert.Equal(0, behind);
        Clean();
    }

    // --- The frame ----------------------------------------------------------

    /// <summary>Builds and runs one frame, and waits for it.</summary>
    /// <remarks>
    ///     The wait is what a <see cref="BufferReadbackRenderer.Latency" /> of zero means: the region
    ///     the copy went into belongs to the frame that has just been submitted, so nothing may read
    ///     it until that frame is done. A host that wanted the value without the stall would set a
    ///     latency instead and take an answer a couple of frames old.
    /// </remarks>
    static void Run(Fixture fixture, GraphicsCompositor compositor, EffectSystem? effects = null) {
        var device = fixture.Device;
        device.BeginFrame();

        fixture.Graph.Reset();
        compositor.Build(fixture.Graph, effects ?? new EffectSystem(), device);

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "transfer")) {
            fixture.Graph.Execute(commands);
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();
    }

    /// <summary>Refuses an answer produced alongside validation errors.</summary>
    static void Clean() {
        if (VulkanDiagnostics.ErrorCount > 0) {
            throw new InvalidOperationException(
                "The frame produced validation errors, so what came back means nothing: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }
    }

    // --- The shader ---------------------------------------------------------

    /// <summary>Compiles <c>ClusterCulling.rvn</c> and loads it onto the device.</summary>
    /// <remarks>
    ///     The real shader for the reason <c>ClusterCullingDeviceTests</c> uses it: a stand-in written
    ///     for this fixture would agree with this fixture. Compiled again here rather than shared,
    ///     because what that fixture is about is the culler and what this one is about is the two
    ///     copies around it — sharing the setup would make either failure look like both.
    /// </remarks>
    static Effect Compiled(VulkanDevice device) {
        var library = Library();

        string[] sources = [
            .. Directory.GetFiles(Path.Combine(library, "Core"), "*.rvn"),
            .. Directory.GetFiles(Path.Combine(library, "Geometry"), "*.rvn"),
            .. Directory.GetFiles(Path.Combine(library, "Shading"), "*.rvn"),
            Path.Combine(library, "Pipeline", "ClusterCulling.rvn")
        ];

        var data = new RavenEffectCompiler(sources).TryGet(EffectKey.Of("ClusterCulling"));

        Assert.NotNull(data);
        return new EffectLoader(device).Load(data!);
    }

    sealed class Baked(Effect effect) : IEffectProvider {
        public Effect? TryGet(EffectKey key) => effect;
    }

    /// <summary>The shader library, found by walking up rather than by counting directories.</summary>
    static string Library() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library");

            if (Directory.Exists(candidate)) {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"Raven/Library was not found above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>Skips when there is no device, unless the environment insists on one.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
    }
}
