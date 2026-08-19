// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Vfx;
using Xunit;

namespace Vixen.Vfx.Gpu.Tests;

/// <summary>
///     One effect, both backends, two pictures — and the dispatch count that says the second one was
///     produced on the device.
/// </summary>
/// <remarks>
///     <para>
///         <b>What this compares, and what it deliberately does not.</b> The device backend that
///         exists is a <em>simulation</em> backend. <see cref="VfxGpuSimulation" /> owns the storage,
///         the descriptors and the dispatches, and there it stops: its buffers are created
///         <c>Storage | CopySource | CopyDestination</c> with no <see cref="BufferUsage.Vertex" />, no
///         shader in <c>Raven/Library/Vfx</c> reads a particle out of a buffer in a vertex stage, and
///         nothing in the tree draws through <see cref="VfxGpuSimulation.DrawArguments" />. So there
///         is no end-to-end device path to photograph, and a test claiming to photograph one would be
///         claiming something the tree cannot do.
///     </para>
///     <para>
///         What it photographs instead is the seam that does exist: the particles are produced twice
///         — once by <see cref="VfxSimulation" /> and once by dispatches on a real device, read back —
///         and then expanded and drawn by <em>identical</em> code. Everything downstream of the
///         simulation cancels, so a difference in the two pictures is a difference in the two
///         backends and nothing else. <c>VfxAgreementTests</c> asks the same question of the numbers;
///         this asks it of the pixels, which is the form that catches a disagreement small enough to
///         pass a tolerance and large enough to see.
///     </para>
///     <para>
///         ⚠ <b>The two pictures are not expected to be bit-identical, and an assertion that they
///         were would be a false one.</b> The agreement is a tolerance rather than an equality —
///         a contracted multiply-add, a transcendental evaluated by two different libraries — so
///         particles land a fraction of a millimetre apart, and a quad edge that falls either side of
///         a pixel centre rasterises differently. The gate is therefore on how far apart a channel
///         gets and on how much of the frame moved at all, both stated below, and the pictures are
///         written out so a failure can be looked at rather than only read.
///     </para>
///     <para>
///         <b>Set <c>VIXEN_VFX_PICTURES</c> to a directory to keep them.</b> Otherwise they go beside
///         the test binary, which is where a CI run's artefacts are collected from.
///     </para>
/// </remarks>
public sealed class VfxBackendPictureTests {
    const int Count = 512;
    const int Side = 256;
    const int Steps = 40;
    const float Dt = 1f / 60f;
    const uint Seed = 7;

    /// <summary>The colour the particles are, at birth and for the whole of this run.</summary>
    static Vector4 Ember => new(1f, 0.45f, 0.08f, 1f);

    /// <summary>Where the emitter is. In front of the camera, and not on any axis.</summary>
    /// <remarks>
    ///     ⚠ Non-zero in every component on purpose. A zero origin is the value that makes a missing
    ///     origin addition invisible — the trap <c>VfxAgreementTests</c> names — and here it would
    ///     additionally put the whole effect behind the camera, which draws two empty frames that
    ///     agree with each other perfectly.
    /// </remarks>
    static Vector3 Origin => new(0.4f, 0.6f, -9f);

    /// <summary>
    ///     The two backends produce the same picture, and the device one really dispatched.
    /// </summary>
    [Fact]
    public void The_two_backends_draw_the_same_picture() {
        VulkanRequirement.Available(VulkanDevice.TryCreate(new(), out var device, out var reason), reason);

        using var owned = device!;
        VulkanDiagnostics.Reset();

        var graph = Graph();

        using var cpu = new VfxSystem(graph, Seed);
        using var gpu = new VfxSystem(graph, Seed);

        Simulate(cpu);
        var dispatches = Dispatch(owned, graph, gpu);

        // ⚠ The counter evidence, and the reason it is an equality rather than a "> 0". A path that
        // recorded an initialize and then quietly skipped every update would read as a device path
        // that ran, and would produce a picture of unmoved particles that this file would then have
        // to catch by eye.
        Assert.Equal(Steps + 1, dispatches);

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "The dispatches produced validation errors: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );

        var camera = RenderCamera.Default with { Position = Vector3.Zero, AspectRatio = 1f };

        var left = ParticlePicture.Render(owned, cpu, camera, out var cpuParticles, Side);
        var right = ParticlePicture.Render(owned, gpu, camera, out var gpuParticles, Side);

        Save("vfx-backend-cpu.png", left);
        Save("vfx-backend-gpu.png", right);

        // ⚠ Both of these before any comparison. Two pictures of nothing agree perfectly, and a
        // clear-coloured frame is what a broken pipeline and an effect with nothing alive in it both
        // produce — so "they match" is worth nothing until something is known to be in them.
        Assert.Equal(Count, cpuParticles);
        Assert.Equal(Count, gpuParticles);

        Assert.True(Lit(left) > Side * Side / 100, $"the CPU picture is essentially the clear: {Lit(left)} lit pixels");
        Assert.True(Lit(right) > Side * Side / 100, $"the GPU picture is essentially the clear: {Lit(right)} lit pixels");

        var (worst, moved) = Compare(left, right);

        // ⚠ **Measured, not guessed: 3/255 and 25 pixels of 65 536 on this machine** — the two
        // backends draw very nearly the same frame, and the bounds are what they are because that
        // was measured before they were written rather than after they failed.
        //
        // The headroom is deliberately large in absolute terms and still tiny against a real
        // disagreement. A particle whose hash differed by one bit is not a rim a pixel wide: it is
        // somewhere else in the emitter's four-metre box, which at this distance moves a whole
        // ten-pixel sprite — eighty pixels for one particle, and forty thousand if the arithmetic
        // rather than the rounding had diverged. So there is no bound between "a rasterised edge"
        // and "the backends disagree" that this could be sitting the wrong side of.
        Assert.True(worst <= 8, $"a channel differs by {worst}/255 between the two backends");

        Assert.True(
            moved <= Side * Side / 100,
            $"{moved} of {Side * Side} pixels differ between the two backends, which is more than a rasterised rim"
        );
    }

    /// <summary>
    ///     Two pictures of an effect that moved are not the same picture — which is what says the
    ///     comparison above can fail at all.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The instrument, checked before its reading is believed.</b> Every assertion in the
    ///     test above is satisfied by a comparison that always passes — by a difference computed over
    ///     the wrong buffer, by two renders of the same system, by a <c>Compare</c> that returns zero.
    ///     This runs the identical comparison over one effect stepped a little further than the other
    ///     and requires it to object, so a green run above means the two backends agree rather than
    ///     that nothing was looked at.
    /// </remarks>
    [Fact]
    public void The_comparison_notices_a_difference() {
        VulkanRequirement.Available(VulkanDevice.TryCreate(new(), out var device, out var reason), reason);

        using var owned = device!;

        var graph = Graph();

        using var early = new VfxSystem(graph, Seed);
        using var late = new VfxSystem(graph, Seed);

        Simulate(early);
        Simulate(late, Steps + 6);

        var camera = RenderCamera.Default with { Position = Vector3.Zero, AspectRatio = 1f };

        var (worst, moved) = Compare(
            ParticlePicture.Render(owned, early, camera, Side),
            ParticlePicture.Render(owned, late, camera, Side)
        );

        Assert.True(worst > 16, $"a tenth of a second of gravity moved no channel by more than {worst}/255");
        Assert.True(moved > Side * Side / 25, $"a tenth of a second of gravity moved only {moved} pixels");
    }

    // --- The two backends ---------------------------------------------------

    /// <summary>Fills a system's buffer with what <see cref="VfxSimulation" /> produces.</summary>
    /// <remarks>
    ///     Spawned and stepped directly rather than through <see cref="VfxSystem.Step" />, so that the
    ///     two sides run the identical sequence — a spawner would hand the device side a different
    ///     population on the frame a rate rounded differently, and the comparison would then be
    ///     between two effects rather than between two backends.
    /// </remarks>
    static void Simulate(VfxSystem system, int steps = Steps) {
        system.Particles.Spawn(Count, out var first);
        VfxSimulation.Initialize(system.Particles, system.Graph.Initializers, first, Count, Seed, Origin);

        var clock = 0f;

        for (var step = 0; step < steps; step++) {
            VfxSimulation.Update(system.Particles, system.Graph.Updaters, Dt, clock);
            clock += Dt;
        }
    }

    /// <summary>Fills a system's buffer with what the device produces, and says how many dispatches it took.</summary>
    static int Dispatch(VulkanDevice device, VfxCompiledGraph graph, VfxSystem system) {
        var shader = VfxShaderEmitter.Emit(graph, "Picture");
        var kernels = RavenKernels.Compile(shader.Source);

        using var simulation = new VfxGpuSimulation(device, shader, Count);

        var initializeModule = device.CreateShader(
            ShaderStage.Compute,
            RavenKernels.Of(kernels, shader.InitializeShader),
            shader.InitializeShader
        );

        var updateModule = device.CreateShader(
            ShaderStage.Compute,
            RavenKernels.Of(kernels, shader.UpdateShader),
            shader.UpdateShader
        );

        var initialize = device.CreateComputePipeline(new(initializeModule, simulation.Layout, shader.InitializeShader));
        var update = device.CreateComputePipeline(new(updateModule, simulation.Layout, shader.UpdateShader));

        system.Particles.Spawn(Count, out _);

        device.BeginFrame();

        using (var list = device.BeginCommandList(QueueKind.Compute, "picture")) {
            // Seeding a device system from a CPU spawn — the one transfer this is for, and the reason
            // the identifiers on the two sides are the same identifiers.
            simulation.Upload(list, system.Particles, Count);
            simulation.Initialize(list, initialize, 0, Count, Seed, 0f, Origin);

            var clock = 0f;

            for (var step = 0; step < Steps; step++) {
                simulation.Update(list, update, Count, Dt, Seed, clock);
                clock += Dt;
            }

            simulation.Download(list, Count);
            list.Finish();
            device.ComputeQueue.Submit([list]);
        }

        device.EndFrame();
        device.WaitIdle();

        simulation.Read(system.Particles, Count);

        device.Destroy(update);
        device.Destroy(initialize);
        device.Destroy(updateModule);
        device.Destroy(initializeModule);

        return simulation.Dispatches;
    }

    // --- The comparison -----------------------------------------------------

    /// <summary>The worst channel difference, and how many pixels differ at all.</summary>
    /// <remarks>
    ///     Two numbers rather than a mean, because they fail differently and a mean hides both. One
    ///     sprite in the wrong place is a large <c>worst</c> over a small <c>moved</c>; a systematic
    ///     drift of every particle is a small <c>worst</c> over the whole frame. A mean would be
    ///     unremarkable in both.
    /// </remarks>
    static (int Worst, int Moved) Compare(byte[] left, byte[] right) {
        Assert.Equal(left.Length, right.Length);

        var worst = 0;
        var moved = 0;

        for (var pixel = 0; pixel < left.Length; pixel += 4) {
            var difference = 0;

            for (var channel = 0; channel < 3; channel++) {
                difference = Math.Max(difference, Math.Abs(left[pixel + channel] - right[pixel + channel]));
            }

            if (difference > 0) {
                moved++;
            }

            worst = Math.Max(worst, difference);
        }

        return (worst, moved);
    }

    /// <summary>How many pixels the sprites reached — anything redder than the clear.</summary>
    /// <remarks>
    ///     Red, because the clear has none and the particles are orange. Counting "not the clear"
    ///     instead would count a frame whose blue channel was one off from a rounding difference in
    ///     the target format, which is not what "something was drawn" should mean.
    /// </remarks>
    static int Lit(byte[] picture) {
        var lit = 0;

        for (var pixel = 0; pixel < picture.Length; pixel += 4) {
            if (picture[pixel] > 24) {
                lit++;
            }
        }

        return lit;
    }

    static void Save(string name, byte[] picture) {
        var directory = Environment.GetEnvironmentVariable("VIXEN_VFX_PICTURES") ?? AppContext.BaseDirectory;

        Directory.CreateDirectory(directory);
        PngCodec.Save(Path.Combine(directory, name), new Bitmap(Side, Side, picture));
    }

    // --- The effect ---------------------------------------------------------

    /// <summary>
    ///     An effect with enough in it to be worth photographing, drawn as billboards.
    /// </summary>
    /// <remarks>
    ///     <c>VfxAgreementTests</c>' graph plus the two things a picture needs that a number
    ///     comparison does not: a renderer, without which <c>VfxGeometryBuilder</c> refuses rather
    ///     than producing quads of no size in a colour nobody chose, and particles large enough to
    ///     cover more than a pixel at nine metres.
    /// </remarks>
    static VfxCompiledGraph Graph() =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(Count)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-2f, -2f, -2f, 0f)) { B = new(2f, 2f, 2f, 0f) },
                new(VfxOpcode.VelocityRandomDirection, new Vector4(1f, 3f, 0f, 0f)),
                new(VfxOpcode.SetLifetime, new Vector4(8f, 12f, 0f, 0f)),
                new(VfxOpcode.SetSize, new Vector4(0.25f, 0.5f, 0f, 0f)),

                // Orange, and nothing else in the frame is: the clear is blue and the blend is
                // additive, so a lit pixel is a particle rather than anything else that could go wrong.
                new(VfxOpcode.SetColour, Ember)
            ],
            [
                new(VfxOpcode.Gravity, new Vector4(0f, -9.81f, 0f, 0f)),
                new(VfxOpcode.Drag, new Vector4(0.5f, 0f, 0f, 0f)),
                new(VfxOpcode.Integrate),
                new(VfxOpcode.SizeOverLife, new Vector4(0.4f, 0f, 0f, 0f)),
                // ⚠ Its start colour is the ember rather than white, and that is not decoration.
                // ColourOverLife *overwrites* the colour — A at birth, B at death — so a white A
                // silently discards the SetColour above and draws a frame of white sprites, which is
                // also what an additively blended frame that has blown out looks like. Two failures
                // with one appearance is exactly what a picture should not have.
                new(VfxOpcode.ColourOverLife, Ember) { B = new(1f, 0.1f, 0f, 0f) }
            ],
            Count,
            VfxRenderer.Billboard
        );
}
