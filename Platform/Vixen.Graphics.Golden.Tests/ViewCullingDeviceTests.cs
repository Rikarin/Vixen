// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The view culler, compiled from its own source and run on a device.
/// </summary>
/// <remarks>
///     <para>
///         Everything else about GPU culling is asserted on the CPU against a mirror of the shader —
///         the packing, the three rejections, the plane test's rounding slack, the level a Hi-Z
///         rectangle lands in. All of that is arithmetic that agrees with itself.
///         <strong>This is the only test that asks the GPU</strong>, and it is the one that can see
///         the things a mirror structurally cannot:
///     </para>
///     <para>
///         <strong>The record layouts.</strong> <c>CullObject</c> is thirty-two bytes and
///         <c>CullView</c> is two hundred and eight because a unit test says so — which is a test of
///         the host's opinion. What decides whether that opinion is right is how Raven lays the
///         struct out on the other side of the binding, and the only way to ask is to write bytes on
///         one side and read a decision made from them on the other. A member at the wrong offset
///         gives a frustum built out of a stage mask, which culls everything or nothing.
///     </para>
///     <para>
///         <strong>The descriptor plan.</strong> Every CPU test binds through a hand-written provider
///         whose bindings are the ones the host expects, which is a tautology. Here the plan comes
///         from the real reflection, so a set index or a binding order this host guessed wrongly is a
///         validation error rather than a passing test.
///     </para>
///     <para>
///         <strong>The word pairing.</strong> The device answers in 32-bit words and the host packs
///         64-bit ones. Two of them are one of ours, and which half is which is a claim about
///         endianness and shift direction that only a real dispatch settles.
///     </para>
///     <para>
///         The oracle is <see cref="VisibilityGroup" />, which doc 06's testing table already holds
///         to a brute-force oracle of its own. Independent of the shader in the only way that counts:
///         it runs here, and what it is compared against came out of the device.
///     </para>
/// </remarks>
public class ViewCullingDeviceTests {
    /// <summary>
    ///     Every object is visible in every view exactly when the CPU path says it is.
    /// </summary>
    /// <remarks>
    ///     Word for word over both views, not a sample: comparing the bitsets is cheap next to
    ///     opening a device, and "the objects I looked at were right" is how a culler with a
    ///     transposed matrix or a swapped stage-mask half passes.
    /// </remarks>
    [Fact]
    public void EveryObjectAgreesWithTheCpuPath() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var effects = new EffectSystem();
        effects.AddProvider(new Baked(Compiled(device)));

        var pipelines = new ComputePipelineCache(device);

        using var store = new RenderObjectStore();
        using var expected = new VisibilityGroup();
        using var actual = new GpuVisibilityGroup(device) { Effects = effects, Pipelines = pipelines };

        // Enough objects to cross several word boundaries, spread widely enough that all three
        // rejections and the frustum test each decide some of them. Seeded, so a failure is a
        // failure somebody else can reproduce.
        var random = new Random(20260728);

        for (var i = 0; i < 500; i++) {
            store.Add(
                new() {
                    Bounds = new(
                        new(
                            (float)((random.NextDouble() * 200) - 100),
                            (float)((random.NextDouble() * 200) - 100),
                            (float)((random.NextDouble() * 200) - 100)
                        ),
                        (float)(random.NextDouble() * 5)
                    ),
                    // A quarter of them in a stage the second view does not draw, which is the half
                    // of the mask that travels separately.
                    Stages = i % 4 == 0 ? RenderStageMask.Of(40) : RenderStageMask.Of(0),
                    IsAlive = i % 17 != 0
                }
            );
        }

        RenderView[] views = [
            Camera("camera", RenderStageMask.Of(0) | RenderStageMask.Of(40)),
            Camera("cascade", RenderStageMask.Of(0), maximumDistance: 60f)
        ];

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        expected.Cull(store, views);
        actual.Cull(store, views);

        device.EndFrame();
        device.WaitIdle();
        pipelines.Clear();

        Assert.True(actual.CulledOnDevice, "the dispatch did not run, so this compared the CPU with itself");

        if (VulkanDiagnostics.ErrorCount > 0) {
            throw new InvalidOperationException(
                "The dispatch produced validation errors, so its output means nothing: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }

        var mismatches = new List<string>();

        for (var view = 0; view < views.Length; view++) {
            for (var i = 0; i < store.Count; i++) {
                var id = new RenderObjectId(i);

                if (expected.IsVisible(view, id) != actual.IsVisible(view, id)) {
                    mismatches.Add(
                        $"{views[view].Name} #{i}: cpu {expected.IsVisible(view, id)}, gpu {actual.IsVisible(view, id)}"
                    );
                }
            }
        }

        Assert.Equal([], mismatches.Take(8).ToArray());

        // And the culling did something. A dispatch that wrote nothing agrees with nothing, and a
        // dispatch that wrote ones agrees with a scene where everything is in front of the camera.
        for (var view = 0; view < views.Length; view++) {
            Assert.True(actual.VisibleCount(view) > 0, $"{views[view].Name} sees nothing at all");
            Assert.True(actual.VisibleCount(view) < store.Count, $"{views[view].Name} culled nothing");
        }
    }

    /// <summary>
    ///     Both variants declare the texture, which is the fact the host has to be built around.
    /// </summary>
    /// <remarks>
    ///     A permutation removes what a shader <em>does</em>, not what it <em>declares</em>: the
    ///     frustum-only variant never samples the pyramid and still asks for it. Only the real
    ///     compiler can say that, and until it did, the host believed the leaner shape a hand-written
    ///     fixture had invented — and would have fallen back to the CPU on every frame that had no
    ///     pyramid to bind, silently, for ever.
    /// </remarks>
    [Fact]
    public void BothVariantsDeclareTheirBindingsInOneSet() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;

        foreach (var effect in (Effect[])[Compiled(owned.Device), Compiled(owned.Device, occlusion: true)]) {
            var objects = Assert.IsType<EffectBinding>(effect.BindingOf("objects"));

            Assert.NotNull(effect.BindingOf("views"));
            Assert.NotNull(effect.BindingOf("visibility"));
            Assert.NotNull(effect.BindingOf("occluders"));

            // One set, because one dispatch binds one set.
            Assert.All(effect.Bindings, binding => Assert.Equal(objects.Set, binding.Set));
        }
    }

    /// <summary>
    ///     Occlusion agrees with a pyramid reduced twice: once on the device, once here.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The half of the culler no unit test can reach. The pyramid is built by a compute
    ///         dispatch from a texture this test filled, the cull samples it through a chain of mip
    ///         views, and the answer comes back through the bitset — so a level sized wrongly, a view
    ///         bound at the wrong mip, or a reduction that took the nearest depth instead of the
    ///         furthest all land here and nowhere else.
    ///     </para>
    ///     <para>
    ///         The oracle reduces the same texels on the CPU, by the rule the shader is supposed to
    ///         follow rather than by asking it — a floored chain, a 3×3 clamped block, the minimum
    ///         because depth is reversed. A reduction that takes the maximum agrees with this
    ///         everywhere the input is flat, which is why the input is not: half the screen is a wall
    ///         at the near plane and half is empty, so the tiles that straddle the join are the ones
    ///         that tell the two apart.
    ///     </para>
    /// </remarks>
    [Fact]
    public void OcclusionAgreesWithAPyramidReducedOnTheCpu() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        const int Side = 64;

        // Half a screen of wall pressed against the near plane, half of nothing. Under reverse-Z a
        // surface at the near plane is 1 and an empty pixel is 0.
        var texels = new float[Side * Side];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                texels[(y * Side) + x] = x < Side / 2 ? 1f : 0f;
            }
        }

        var depth = owned.Owned("depth", TextureUsage.Sampled | TextureUsage.CopyDestination, PixelFormat.R32Float, Side, Side);
        var staging = device.CreateBuffer(new(texels.Length * sizeof(float), BufferUsage.CopySource, MemoryAccess.HostUpload, "depth staging"));

        owned.Owns(() => device.Destroy(staging));
        device.Write(staging, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(texels.AsSpan()));

        var effects = new EffectSystem();
        effects.AddProvider(new Compiler(owned.Device));

        var pipelines = new ComputePipelineCache(device);

        using var store = new RenderObjectStore();
        using var frustum = new VisibilityGroup();
        using var pyramid = new HiZPyramid(device) { Effects = effects, Pipelines = pipelines };
        using var actual = new GpuVisibilityGroup(device) { Effects = effects, Pipelines = pipelines, Occluders = pyramid };

        // Spread across the frustum and across the join, at depths either side of anything the wall
        // could hide, so both answers occur and the straddling cases are in there.
        for (var i = 0; i < 60; i++) {
            var angle = i * 0.37f;

            store.Add(
                new() {
                    Bounds = new(new(MathF.Sin(angle) * 12f, MathF.Cos(angle) * 8f, 12f + (i % 6 * 9f)), 1.5f + (i % 4)),
                    Stages = RenderStageMask.Of(0),
                    IsAlive = true
                }
            );
        }

        RenderView[] views = [Camera("camera", RenderStageMask.Of(0))];

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var list = device.BeginCommandList(QueueKind.Graphics, "pyramid")) {
            list.Barrier(new([], [new(depth.Texture, ResourceState.Undefined, ResourceState.CopyDestination)]));
            list.CopyBufferToTexture(staging, 0, new(depth.Texture), new(Side, Side, 1));
            list.Barrier(new([], [new(depth.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]));

            Assert.True(pyramid.Build(list, depth.View, new(Side, Side)), "the pyramid did not build");

            list.Finish();
            device.GraphicsQueue.Submit([list]);
        }

        device.WaitIdle();

        // The first cull has no matrix from the frame the pyramid was built in; the second does.
        actual.Cull(store, views);
        actual.Cull(store, views);
        frustum.Cull(store, views);

        device.EndFrame();
        device.WaitIdle();
        pipelines.Clear();

        Assert.True(actual.CulledOnDevice, "the dispatch did not run");
        Assert.True(actual.OcclusionTested, "the cull did not test against the pyramid");

        if (VulkanDiagnostics.ErrorCount > 0) {
            throw new InvalidOperationException(
                "The dispatch produced validation errors, so its output means nothing: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }

        var levels = Reduce(texels, new(Side, Side), pyramid.Levels);
        var packed = GpuCulling.Pack(views[0], store.Count, GpuCulling.WordsFor(store.Count), views[0].ViewProjection, pyramid.Levels);
        var mismatches = new List<string>();
        var occluded = 0;

        for (var i = 0; i < store.Count; i++) {
            var id = new RenderObjectId(i);

            var hidden = GpuCulling.IsOccluded(
                GpuCulling.Pack(store[id]),
                packed,
                pyramid.Size,
                (x, y, level) => levels[level][(y * GpuCulling.LevelSize(pyramid.Size, level).X) + x]
            );

            occluded += hidden ? 1 : 0;
            var expected = frustum.IsVisible(0, id) && !hidden;

            if (expected != actual.IsVisible(0, id)) {
                mismatches.Add($"#{i}: expected {expected}, got {actual.IsVisible(0, id)} (frustum {frustum.IsVisible(0, id)}, occluded {hidden})");
            }
        }

        Assert.Equal([], mismatches.Take(8).ToArray());

        // And the wall hid something, or this agreed about a test that never ran.
        Assert.True(occluded > 0, "nothing was occluded, so the pyramid decided nothing");
        Assert.True(occluded < frustum.VisibleCount(0), "everything was occluded");
    }

    /// <summary>
    ///     The depth pyramid, reduced the way <c>HiZReduce.rvn</c> is supposed to reduce it.
    /// </summary>
    /// <remarks>
    ///     Level 0 is half the source, floored, and every level after it halves again; each texel is
    ///     the minimum of a 3×3 block clamped to its parent, which is what makes an odd level's
    ///     trailing row belong to somebody. Written out again rather than shared with anything: an
    ///     oracle that called the host's own helper would agree with itself.
    /// </remarks>
    static float[][] Reduce(float[] source, Int2 size, int levels) {
        var chain = new float[levels][];
        var parent = source;
        var parentSize = size;

        for (var level = 0; level < levels; level++) {
            var current = GpuCulling.LevelSize(new(Math.Max(1, size.X / 2), Math.Max(1, size.Y / 2)), level);
            var texels = new float[current.X * current.Y];

            for (var y = 0; y < current.Y; y++) {
                for (var x = 0; x < current.X; x++) {
                    var furthest = 1f;

                    for (var dy = 0; dy < 3; dy++) {
                        for (var dx = 0; dx < 3; dx++) {
                            var sx = Math.Min((x * 2) + dx, parentSize.X - 1);
                            var sy = Math.Min((y * 2) + dy, parentSize.Y - 1);

                            furthest = MathF.Min(furthest, parent[(sy * parentSize.X) + sx]);
                        }
                    }

                    texels[(y * current.X) + x] = furthest;
                }
            }

            chain[level] = texels;
            parent = texels;
            parentSize = current;
        }

        return chain;
    }

    /// <summary>
    ///     The argument pass keeps a visible object's draw and zeroes a culled one's.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The last third of the path, and the one whose output nothing on the host reads in a
    ///         frame: the arguments go straight from a dispatch into a draw call's mouth. So this
    ///         copies them back and looks. What it checks is that the pass edits exactly one field of
    ///         exactly the records culling rejected — a shader that zeroed the wrong field would draw
    ///         a mesh's indices as instances, and one that read the wrong bit would draw the wrong
    ///         half of the scene.
    ///     </para>
    ///     <para>
    ///         Over the culler's own bits rather than a pattern written by hand, because that is the
    ///         pair a frame actually runs and because the two passes agreeing is the claim: one
    ///         writes 32 objects to a word and the other reads one bit of it, and nothing else says
    ///         they agree about which bit.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheArgumentPassZeroesWhatCullingRejected() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var effects = new EffectSystem();
        effects.AddProvider(new Compiler(device));

        var pipelines = new ComputePipelineCache(device);

        using var store = new RenderObjectStore();
        using var visibility = new GpuVisibilityGroup(device) { Effects = effects, Pipelines = pipelines };
        using var arguments = new GpuDrawArguments(device) { Effects = effects, Pipelines = pipelines };

        // Half in front of the camera and half behind it, alternating, so the bits cross the 32-object
        // word boundary in both directions.
        for (var i = 0; i < 40; i++) {
            store.Add(
                new() {
                    Bounds = new(new(0f, 0f, i % 2 == 0 ? 12f : -12f), 1f),
                    Stages = RenderStageMask.Of(0),
                    IsAlive = true
                }
            );
        }

        RenderView[] views = [Camera("camera", RenderStageMask.Of(0))];

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        visibility.Cull(store, views);

        var templates = arguments.Fill(store.Count);

        for (var i = 0; i < store.Count; i++) {
            templates[i] = new() {
                IndexCount = (uint)(3 * (i + 1)),
                InstanceCount = (uint)(1 + (i % 3)),
                FirstIndex = (uint)i,
                VertexOffset = (uint)(2 * i),
                FirstInstance = (uint)(7 * i)
            };
        }

        var size = (long)store.Count * GpuDrawArguments.Stride;
        var readback = device.CreateBuffer(new(size, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "arguments readback"));

        owned.Owns(() => device.Destroy(readback));

        using (var list = device.BeginCommandList(QueueKind.Compute, "arguments")) {
            Assert.True(arguments.Update(list, visibility.Bits, views.Length, store.Count), "the argument pass did not run");

            // From ShaderWrite rather than from the IndirectArgument the pass left it in, and the
            // difference is not pedantry: a barrier names the access whose writes it publishes, and
            // chaining one that only makes the dispatch visible to an *indirect read* leaves a copy
            // reading whatever the buffer held before. That is a test that reports an empty argument
            // buffer and a pass that was working the whole time.
            list.Barrier(new([new(arguments.Commands, ResourceState.ShaderWrite, ResourceState.CopySource)], []));
            list.CopyBuffer(arguments.Commands, 0, readback, 0, size);
            list.Finish();

            device.ComputeQueue.Submit([list]);
        }

        device.EndFrame();
        device.WaitIdle();
        pipelines.Clear();

        if (VulkanDiagnostics.ErrorCount > 0) {
            throw new InvalidOperationException(
                "The dispatch produced validation errors, so its output means nothing: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }

        var bytes = new byte[size];
        device.Read(readback, 0, bytes);

        var written = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, DrawCommand>(bytes);
        var mismatches = new List<string>();
        var drawn = 0;

        for (var i = 0; i < store.Count; i++) {
            var visible = visibility.IsVisible(0, new(i));
            var expected = templates[i];

            if (!visible) {
                expected.InstanceCount = 0;
            }

            drawn += visible ? 1 : 0;

            if (!expected.Equals(written[i])) {
                mismatches.Add($"#{i}: expected {Show(expected)}, got {Show(written[i])}");
            }
        }

        Assert.Equal([], mismatches.Take(8).ToArray());
        Assert.Equal(20, drawn);
    }

    static string Show(in DrawCommand command) =>
        $"({command.IndexCount}, {command.InstanceCount}, {command.FirstIndex}, {command.VertexOffset}, {command.FirstInstance})";

    /// <summary>
    ///     Each of the three culling shaders compiles to a module a device will take.
    /// </summary>
    /// <remarks>
    ///     The smallest question worth asking of a real driver, and the first one: a shader that
    ///     reaches SPIR-V and passes <c>spirv-val</c> can still be one no driver will make a pipeline
    ///     out of. Separate from the tests above so that a failure says <em>which</em> shader rather
    ///     than "culling does not work".
    /// </remarks>
    [Theory]
    [InlineData(GpuCulling.ShaderName)]
    [InlineData(GpuCulling.ReduceShaderName)]
    [InlineData(GpuCulling.ArgumentsShaderName)]
    public void EachShaderMakesAPipeline(string shader) {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;

        VulkanDiagnostics.Reset();

        var pipelines = new ComputePipelineCache(owned.Device);
        var pipeline = pipelines.GetOrCreate(Compiled(owned.Device, shader));

        Assert.True(pipeline.IsValid, $"'{shader}' produced no compute pipeline");

        pipelines.Clear();

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            $"'{shader}' created a pipeline with complaints: {string.Join(Environment.NewLine, VulkanDiagnostics.Messages)}"
        );
    }

    /// <summary>Compiles one of the culling shaders from source, as the content build would.</summary>
    static Effect Compiled(VulkanDevice device, string shader) {
        var library = Library();

        string[] sources = [
            .. Directory.GetFiles(Path.Combine(library, "Core"), "*.rvn"),
            .. Directory.GetFiles(Path.Combine(library, "Geometry"), "*.rvn"),
            Path.Combine(library, "Pipeline", $"{shader}.rvn")
        ];

        var data = new RavenEffectCompiler(sources).TryGet(EffectKey.Of(shader));

        Assert.NotNull(data);
        return new EffectLoader(device).Load(data!);
    }

    /// <summary>Compiles the culler from source, as the content build would.</summary>
    /// <remarks>
    ///     Its own imports and nothing else. The whole library would drag in the material shaders,
    ///     whose compose slots are bound per material and which do not compile without a composition.
    /// </remarks>
    static Effect Compiled(VulkanDevice device, bool occlusion = false) {
        var library = Library();

        string[] sources = [
            .. Directory.GetFiles(Path.Combine(library, "Core"), "*.rvn"),
            .. Directory.GetFiles(Path.Combine(library, "Geometry"), "*.rvn"),
            Path.Combine(library, "Pipeline", "Culling.rvn")
        ];

        var compiler = new RavenEffectCompiler(sources);

        var data = compiler.TryGet(
            EffectKey.Of(GpuCulling.ShaderName, [new(GpuCulling.OcclusionKey, occlusion ? "true" : "false")])
        );

        Assert.NotNull(data);
        return new EffectLoader(device).Load(data!);
    }

    /// <summary>A camera looking down +Z, as the culling tests on the CPU build one.</summary>
    static RenderView Camera(string name, RenderStageMask stages, float maximumDistance = 0f) {
        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        return new(name) {
            Stages = stages,
            Position = Vector3.Zero,
            ViewProjection = view * projection,
            MaximumDistance = maximumDistance
        };
    }

    /// <summary>The one variant there is, which is what a baked bundle looks like from here.</summary>
    sealed class Baked(Effect effect) : IEffectProvider {
        public Effect? TryGet(EffectKey key) => effect;
    }

    /// <summary>
    ///     Compiles whatever is asked for, which is what a run with a shader-compiler service looks
    ///     like from here.
    /// </summary>
    /// <remarks>
    ///     The occlusion fixture needs three shaders and two variants of one of them, so a provider
    ///     holding a single baked effect will not do — and compiling per key is also what proves the
    ///     group asks for the key it means to.
    /// </remarks>
    sealed class Compiler(VulkanDevice device) : IEffectProvider {
        readonly Dictionary<EffectKey, Effect> compiled = [];

        public Effect? TryGet(EffectKey key) {
            if (compiled.TryGetValue(key, out var existing)) {
                return existing;
            }

            var library = Library();

            string[] sources = [
                .. Directory.GetFiles(Path.Combine(library, "Core"), "*.rvn"),
                .. Directory.GetFiles(Path.Combine(library, "Geometry"), "*.rvn"),
                Path.Combine(library, "Pipeline", $"{key.ShaderName}.rvn")
            ];

            var data = new RavenEffectCompiler(sources).TryGet(key);

            if (data is null) {
                return null;
            }

            compiled[key] = new EffectLoader(device).Load(data);
            return compiled[key];
        }
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
