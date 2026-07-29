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
///     The light culler, compiled from its own source and run on a device.
/// </summary>
/// <remarks>
///     <para>
///         Everything else about clustered lighting is asserted on the CPU against a mirror of the
///         shader — the grid's constants, the exponential slicing, the handedness, the round trip
///         between a fragment's cluster and the box the culler built for it. All of that is
///         arithmetic that agrees with itself. <strong>This is the only test that asks the GPU.</strong>
///     </para>
///     <para>
///         It matters more here than it usually would, because of what the last bug in this pass
///         looked like: <c>Transform.ViewRay</c> pointed down +Z against a right-handed view space, so
///         every cluster's box was mirrored away from the lights tested against it and every list came
///         back <em>empty</em>. A pass that culls everything renders a scene lit by the sun alone —
///         which is a plausible-looking frame, not a crash, and no CPU test of one side could see it.
///     </para>
///     <para>
///         <strong>The real shader, not a stand-in.</strong> <c>ClusterCulling.rvn</c> is compiled
///         here through the same <see cref="RavenEffectCompiler" /> the content build uses, so what
///         runs is what ships. A hand-written GLSL mirror would have been easier and would have tested
///         the mirror.
///     </para>
///     <para>
///         The oracle is <see cref="ClusterGrid.Bounds" /> and the sphere test spelled out again from
///         the shader's own description — a light reaches a cluster when its range, widened by
///         whatever puts its surface further out than its centre, reaches the nearest point of the
///         box. Independent of the shader in the only way that counts: it is evaluated here, and what
///         it is compared against came out of the device.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class ClusterCullingDeviceTests {
    const float Near = 0.1f;
    const float Far = 1000f;

    /// <summary>The camera the grid is built in — the one both halves have to be given.</summary>
    static RenderCamera Camera => RenderCamera.Default with { Position = new(0f, 0f, 4f) };

    /// <summary>
    ///     Every cluster holds exactly the lights that reach it.
    /// </summary>
    /// <remarks>
    ///     Every cluster, not a sample of them: 3456 comparisons cost nothing next to opening a
    ///     device, and "the ones I looked at were right" is how a culler with a mirrored axis passes.
    /// </remarks>
    [Fact]
    public void EveryClusterHoldsTheLightsThatReachIt() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var effect = Compiled(device);
        var slot = effect.Bindings[0].Set;

        // Three lights that between them cover the interesting cases: one straight ahead and near, so
        // it falls in a handful of clusters; one off to the side and far, so it falls in a different
        // part of the grid entirely; and one behind the camera, which reaches nothing and is the case
        // a mirrored axis would fill the whole grid with.
        RenderLight[] lights = [
            RenderLight.Point(new(0f, 0f, -6f), 4f, new(1f, 1f, 1f), 1f),
            RenderLight.Point(new(9f, -3f, -30f), 12f, new(1f, 1f, 1f), 1f),
            RenderLight.Point(new(0f, 0f, 40f), 5f, new(1f, 1f, 1f), 1f)
        ];

        var gpu = Cull(owned, effect, slot, lights);
        var view = Camera.View;

        var tangents = Tangents();
        var mismatches = new List<string>();

        for (var slice = 0; slice < ClusterGrid.Slices; slice++) {
            for (var y = 0; y < ClusterGrid.TilesY; y++) {
                for (var x = 0; x < ClusterGrid.TilesX; x++) {
                    var bounds = ClusterGrid.Bounds(x, y, slice, tangents, Near, Far);
                    var expected = new List<int>();

                    for (var i = 0; i < lights.Length; i++) {
                        if (Reaches(lights[i], view, bounds)) {
                            expected.Add(i);
                        }
                    }

                    var actual = gpu[ClusterGrid.Index(x, y, slice)];

                    if (!expected.SequenceEqual(actual)) {
                        mismatches.Add(
                            $"({x},{y},{slice}): expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]"
                        );
                    }
                }
            }
        }

        Assert.Equal([], mismatches.Take(8).ToArray());

        // And the culling actually did something. A shader that wrote nothing at all agrees with an
        // oracle that expected nothing, which is exactly the frame the handedness bug produced.
        var filled = gpu.Count(list => list.Length > 0);

        Assert.True(filled > 0, "no cluster holds any light");
        Assert.True(filled < ClusterGrid.Count, "every cluster holds a light, so nothing was culled");
    }

    /// <summary>
    ///     A directional light reaches every cluster, and is the only kind that does.
    /// </summary>
    /// <remarks>
    ///     It has no position to be far from, so <c>Touches</c> short-circuits rather than testing it
    ///     against a box it would always pass. Worth its own fixture because the short-circuit is the
    ///     one path through the culler that no geometry decides — if it were dropped, a scene's sun
    ///     would vanish from the clustered variant and stay in the forward one.
    /// </remarks>
    [Fact]
    public void ADirectionalLightIsInEveryCluster() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var effect = Compiled(owned.Device);

        RenderLight[] lights = [
            RenderLight.Directional(new(-0.4f, -1f, -0.3f), new(1f, 1f, 1f), 1f),
            RenderLight.Point(new(0f, 0f, 400f), 1f, new(1f, 1f, 1f), 1f)
        ];

        var gpu = Cull(owned, effect, effect.Bindings[0].Set, lights);

        Assert.All(gpu, list => Assert.Equal([0], list));
    }

    // --- The device half ----------------------------------------------------

    /// <summary>Compiles <c>ClusterCulling.rvn</c> to SPIR-V and loads it onto the device.</summary>
    static Effect Compiled(VulkanDevice device) {
        var library = Library();

        // What this pass imports and nothing else. The whole library would drag in the material
        // shaders, whose compose slots are bound per material — they do not compile without a
        // composition, and this fixture has no business supplying one.
        string[] sources = [
            .. Directory.GetFiles(Path.Combine(library, "Core"), "*.rvn"),
            .. Directory.GetFiles(Path.Combine(library, "Geometry"), "*.rvn"),
            .. Directory.GetFiles(Path.Combine(library, "Shading"), "*.rvn"),
            Path.Combine(library, "Pipeline", "ClusterCulling.rvn")
        ];

        var compiler = new RavenEffectCompiler(sources);
        var data = compiler.TryGet(EffectKey.Of("ClusterCulling"));

        Assert.NotNull(data);
        return new EffectLoader(device).Load(data!);
    }

    /// <summary>
    ///     Dispatches the culler over the whole grid, through the compositor, and reads every list back.
    /// </summary>
    /// <remarks>
    ///     Through <see cref="ComputeRenderer" /> and the render graph rather than a hand-written
    ///     dispatch, which is the second thing this fixture is for: the barrier between the culler and
    ///     the copy is the graph's to place, and the block is filled from
    ///     <see cref="ComputeRenderer.Parameters" /> at the offsets the compiled shader's own plan
    ///     gives. Recording it by hand would test the shader and leave the path a frame actually takes
    ///     unexercised — which is how a compute node with no way to fill its uniforms went unnoticed.
    /// </remarks>
    static int[][] Cull(Fixture fixture, Effect effect, DescriptorSetSlot slot, RenderLight[] lights) {
        var device = fixture.Device;
        var stride = Marshal.SizeOf<ClusterLights>();
        var size = ClusterGrid.BufferSize;

        var gpu = lights.Select(light => light.ToGpu()).ToArray();
        var input = fixture.Buffer<PunctualLightData>(gpu, BufferUsage.Storage);

        var clusters = device.CreateBuffer(
            new(size, BufferUsage.Storage | BufferUsage.CopySource, MemoryAccess.DeviceLocal, "clusters")
        );

        var readback = device.CreateBuffer(
            new(size, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "clusters readback")
        );

        using var allocator = new DescriptorAllocator(device);
        using var system = new RenderSystem();
        var pipelines = new ComputePipelineCache(device);

        var effects = new EffectSystem();
        effects.AddProvider(new Baked(effect));

        using var culling = new ComputeRenderer {
            Name = "ClusterCulling",
            ShaderName = "ClusterCulling",
            Pipelines = pipelines,
            Groups = ClusterGrid.GroupCount,
            ConstantBinding = effect.Bindings.Single(binding => binding.Kind == DescriptorKind.UniformBuffer).Binding,
            Descriptors = { Allocator = allocator, Slot = slot }
        };

        culling.BufferReads.Add("SceneLights");
        culling.BufferWrites.Add("Clusters");

        // By name against the effect's plan, so the descriptor indices are the shader's own rather
        // than numbers written down here.
        culling.Descriptors.Bindings.Add(new() { Name = "lights", Resource = "SceneLights" });
        culling.Descriptors.Bindings.Add(new() { Name = "clusters", Resource = "Clusters" });

        culling.Parameters.Set(ParameterKeys.New<Matrix4x4>("ClusterCulling.view"), Camera.View);
        culling.Parameters.Set(ParameterKeys.New<int>("ClusterCulling.lightCount"), lights.Length);
        ClusterGrid.Apply(culling.Parameters, Camera, "ClusterCulling");

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = culling
        };

        compositor.BufferImports["SceneLights"] = new(
            input,
            new(gpu.Length * Marshal.SizeOf<PunctualLightData>(), BufferUsage.Storage, MemoryAccess.HostUpload, "SceneLights"),
            ResourceState.ShaderRead,
            ResourceState.ShaderRead
        );

        // Handed back as a copy source, so the graph is the one that transitions it — the barrier
        // between the dispatch and the read is exactly what a hand-written version gets wrong, and
        // getting it wrong reads whatever the buffer held before, which is zeros and looks like a
        // culler that found nothing.
        compositor.BufferImports["Clusters"] = new(
            clusters,
            new(size, BufferUsage.Storage | BufferUsage.CopySource, MemoryAccess.DeviceLocal, "Clusters"),
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        VulkanDiagnostics.Reset();
        allocator.BeginFrame();
        device.BeginFrame();

        fixture.Graph.Reset();
        compositor.Build(fixture.Graph, effects, device);

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "cull")) {
            fixture.Graph.Execute(commands);
            commands.CopyBuffer(clusters, 0, readback, 0, size);
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var bytes = new byte[size];
        device.Read(readback, 0, bytes);

        device.Destroy(readback);
        device.Destroy(clusters);
        pipelines.Clear();

        if (VulkanDiagnostics.ErrorCount > 0) {
            throw new InvalidOperationException(
                "The dispatch produced validation errors, so its output means nothing: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }

        var lists = new int[ClusterGrid.Count][];

        for (var i = 0; i < lists.Length; i++) {
            var list = MemoryMarshal.Read<ClusterLights>(bytes.AsSpan(i * stride));
            var count = (int)Math.Min(list.Count, (uint)ClusterGrid.Capacity);

            lists[i] = new int[count];

            for (var j = 0; j < count; j++) {
                lists[i][j] = (int)list.Indices[j];
            }
        }

        return lists;
    }

    /// <summary>The one variant there is, which is what a baked bundle looks like from here.</summary>
    sealed class Baked(Effect effect) : IEffectProvider {
        public Effect? TryGet(EffectKey key) => effect;
    }

    // --- The oracle ---------------------------------------------------------

    /// <summary>
    ///     Whether a light reaches anywhere inside a cluster, as <c>ClusterCulling.Touches</c> says.
    /// </summary>
    /// <remarks>
    ///     Spelled out again rather than shared, which is the point of an oracle: the reach is the
    ///     range widened by everything that puts a light's <em>surface</em> further out than its
    ///     centre, and the test is against the nearest point of the box.
    /// </remarks>
    static bool Reaches(in RenderLight light, in Matrix4x4 view, in BoundingBox bounds) {
        if (light.Kind == LightKind.Directional) {
            return true;
        }

        var centre = Matrix4x4.TransformPosition(light.Position, view);
        var closest = Vector3.Clamp(centre, bounds.Minimum, bounds.Maximum);
        var offset = centre - closest;
        var reach = light.Range + light.Radius + light.HalfLength;

        return Vector3.Dot(offset, offset) <= reach * reach;
    }

    static Vector2 Tangents() {
        var parameters = new ParameterCollection();
        ClusterGrid.Apply(parameters, Camera, "Oracle");

        return parameters.Get(ParameterKeys.New<Vector2>("Oracle.tanHalfFov"));
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

    /// <summary>Passes when there is no device, unless the environment insists on one.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }
    }
}
