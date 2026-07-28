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
