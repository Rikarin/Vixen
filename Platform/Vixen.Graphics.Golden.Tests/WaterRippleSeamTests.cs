// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.RenderGraph;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Water;
using Vixen.Shaders;
using Vixen.Water;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The ripple field's seam — [docs/plan/35 § D12], at a <em>stated non-exact</em> tolerance.
/// </summary>
/// <remarks>
///     <para>
///         <b>The tolerance is the point of this file, not a concession in it.</b> § D2 holds the
///         closed-form wave sum to exact-to-the-float, because it is an <em>expression</em>: a
///         rollback re-asks it at a time it never simulated, two evaluations of one expression agree,
///         and any drift at all is a boat hovering above the crests. A height field is not an
///         expression. Its state <b>is</b> its history — there is nothing to re-evaluate, only a
///         trajectory to re-walk — and two trajectories that begin together diverge at whatever rate
///         the arithmetic differs.
///     </para>
///     <para>
///         Which is exactly why <see cref="IWaterRipples" /> is a separate argument to
///         <see cref="WaterEvaluator" /> rather than part of it: a caller that needs the exact answer
///         — the network path, rolling back six ticks — passes no ripples at all, and the signature
///         is what enforces that rather than a comment asking for it.
///     </para>
///     <para>
///         ⚠ <b>So what is measured here is a bound on the divergence and not its absence.</b>
///         <see cref="Tolerance" /> is stated in metres and the reasoning for its size is written
///         beside it. A test that asserted equality here would be a test that has to be relaxed the
///         first time a driver reorders a multiply-add, and relaxing a tolerance nobody chose is how
///         a seam stops holding anything.
///     </para>
///     <para>
///         Serialised with the rest of the driver tests: <see cref="VulkanDiagnostics" /> is
///         process-wide.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class WaterRippleSeamTests {
    /// <summary>How many texels across the field the seam is measured over.</summary>
    /// <remarks>
    ///     Small on purpose. Divergence accumulates with the number of steps, not with the number of
    ///     texels, and a 65² field walks the same arithmetic as a 257² one at a sixteenth of the
    ///     readback.
    /// </remarks>
    const int Resolution = 65;

    /// <summary>How many steps the two are walked before they are compared.</summary>
    /// <remarks>
    ///     ⚠ <b>Enough for a wave to cross the window twice.</b> One step compares two evaluations of
    ///     one expression and would pass under any arithmetic; the divergence this bounds is a
    ///     <em>trajectory's</em>, and it only exists after the field has propagated.
    /// </remarks>
    const int Steps = 60;

    /// <summary>How far apart the two may be, in metres, after <see cref="Steps" /> steps.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Derived rather than measured, because a tolerance fitted to what the code does is a
    ///         tolerance that holds nothing.</b> A half-float carries eleven significant bits, so at a
    ///         displacement of magnitude <c>m</c> the storage quantum is about <c>m ⁄ 2048</c>. The
    ///         reference peaks near a fifth of a metre here, which is <c>1 × 10⁻⁴ m</c> a step — and a
    ///         wave equation feeds its own output back in, so the roundings accumulate rather than
    ///         cancelling. Sixty steps is <c>6 × 10⁻³ m</c>, and eight millimetres is that with about a
    ///         third in hand for the arithmetic itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It was written as two millimetres first, and that was the estimate being wrong
    ///         rather than the code.</b> The derivation above was done afterwards, with the step count
    ///         left out of the first attempt — worth recording, because "the tolerance was too tight"
    ///         and "the simulations differ" produce the same red test and only one of them is a bug.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What a failure means depends on how badly it fails.</b> A few times this is drift,
    ///         and the question is which term; an order of magnitude is a different <em>simulation</em>
    ///         — the damping applied before the acceleration rather than after, the edge fade in texels
    ///         rather than metres, an injection landing in the height instead of the rate. Those are
    ///         all stable, all plausible, and all a different field. The first run of this file
    ///         diverged by the reference's own amplitude, which was a third thing again: the device
    ///         had been given no injections at all.
    ///     </para>
    /// </remarks>
    const float Tolerance = 0.008f;

    /// <summary>And the same bound as a fraction of the reference's own amplitude.</summary>
    /// <remarks>
    ///     ⚠ <b>Scale-free, and it is the assertion that survives somebody changing the fixture.</b>
    ///     A tolerance in metres is only meaningful beside the amplitude it is a tolerance <em>of</em>
    ///     — halve the injection strength and eight millimetres stops being a bound on anything. This
    ///     one says the two fields agree to within a twentieth of what is happening in them, whatever
    ///     that is.
    /// </remarks>
    const float RelativeTolerance = 0.05f;

    /// <summary>The two walk the same trajectory, to within a stated bound.</summary>
    [Fact]
    public void The_device_field_follows_the_reference_to_within_the_stated_tolerance() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var settings = Settings();
        var reference = Reference(settings);
        var device = Device(owned, settings, out var ran);

        if (!ran) {
            // No pipeline, which on this fixture means the variant did not compile — a picture
            // indistinguishable from a simulation that did nothing, so it is a failure and not a skip.
            Assert.Fail("the ripple step compiled no pipeline, so nothing was dispatched");
        }

        var worst = 0f;
        var at = (0, 0);

        for (var z = 0; z < Resolution; z++) {
            for (var x = 0; x < Resolution; x++) {
                var difference = MathF.Abs(device[(z * Resolution) + x] - reference.At(x, z));

                if (difference > worst) {
                    worst = difference;
                    at = (x, z);
                }
            }
        }

        // ⚠ The negative control first, without which everything below passes on two fields of
        // zeroes. A simulation that dispatched nothing, or wrote nothing, is exactly as flat as one
        // that settled — and flat is what the reference would be compared against.
        Assert.True(reference.Peak > 0.05f, $"the reference never rippled: peak {reference.Peak}");

        Assert.True(
            worst <= Tolerance,
            $"the device field and the reference diverged by {worst:0.#####} m at {at} over {Steps} "
            + $"steps, which is past the derived {Tolerance:0.###} m — see the class remarks for what "
            + "the size of the difference says about the cause"
        );

        Assert.True(
            worst <= reference.Peak * RelativeTolerance,
            $"the two diverged by {worst:0.#####} m against a peak of {reference.Peak:0.###} m, which "
            + $"is more than the {RelativeTolerance:P0} the seam is held to"
        );
    }

    /// <summary>A field the Courant condition refuses is refused on the device too, by name.</summary>
    /// <remarks>
    ///     ⚠ Past the limit an explicit wave equation does not look wrong — it grows without bound in
    ///     a few dozen steps, and on a device that is a whole frame of NaNs with no stack. The CPU
    ///     reference throws with a sentence; the device half has to as well, or the one place the
    ///     refusal is missing is the one that cannot say what happened.
    /// </remarks>
    [Fact]
    public void A_step_past_the_courant_limit_is_refused_on_the_device_too() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var settings = Settings() with { Speed = 400f };

        using var simulation = new WaterRippleSimulation(owned.Device, settings);
        var effects = new EffectSystem();
        var pipelines = new ComputePipelineCache(owned.Device);

        var thrown = Assert.Throws<ArgumentException>(
            () => simulation.Record(owned.Graph, effects, pipelines, 1f / 60f)
        );

        Assert.Contains("Courant", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A small, stable field with an injection budget the fixture stays inside.</summary>
    static WaterRippleSettings Settings() =>
        WaterRippleSettings.Default with {
            Extent = 16f,
            Resolution = Resolution,

            // ⚠ Well under the Courant limit for a 60 Hz step at this spacing, deliberately. A
            // fixture that sat near the limit would be measuring how close two implementations get to
            // instability rather than how far apart they are.
            Speed = 4f,
            EdgeFade = 2f
        };

    /// <summary>The CPU reference, walked the same way the device is.</summary>
    static WaterRipples Reference(in WaterRippleSettings settings) {
        var ripples = new WaterRipples(settings);

        for (var step = 0; step < Steps; step++) {
            Disturb(step, (position, radius, amount) => ripples.Inject(position, radius, amount));
            ripples.Step(1f / 60f);
        }

        return ripples;
    }

    /// <summary>What is injected on a step, so both halves are disturbed identically.</summary>
    /// <remarks>
    ///     ⚠ <b>One function, called by both, rather than two lists that agree.</b> Two schedules that
    ///     drifted apart would be measured as arithmetic divergence — which is the one failure this
    ///     file must not be able to report for the wrong reason.
    /// </remarks>
    static void Disturb(int step, Action<Vector2, float, float> inject) {
        // A splash near the middle at the start, and a second one off-centre part-way through, so the
        // comparison covers a settled ring and a fresh front at once.
        if (step == 0) {
            inject(new(8f, 8f), 1.5f, -6f);
        }

        if (step == 20) {
            inject(new(5f, 10.5f), 1f, 4f);
        }
    }

    /// <summary>The device field, read back as displacement per texel.</summary>
    static float[] Device(Fixture fixture, in WaterRippleSettings settings, out bool ran) {
        var device = fixture.Device;

        // ⚠ Every step of this fixture is recorded into one command list before a single submit, so
        // the descriptor ring has to be at least as long — see StepsPerFrame, whose default is sized
        // for an accumulator catching up rather than for sixty steps at once.
        using var simulation = new WaterRippleSimulation(device, settings, readable: true) { StepsPerFrame = Steps };

        var effects = new EffectSystem();
        var pipelines = new ComputePipelineCache(device);

        effects.AddProvider(
            new Compiling(
                new EffectLoader(device),
                _ => RavenEffects.Only(["Core", "Geometry", "Shading", "Water"], Path.Combine("PostFx", "Fullscreen.rvn"))
            )
        );

        var texels = settings.Resolution * settings.Resolution;
        var bytes = texels * 8;

        var readback = device.CreateBuffer(
            new(bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "ripple readback")
        );

        var recorded = 0;

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "ripples")) {
            // ⚠ The clear is its own graph, executed before the first step rather than declared
            // beside it. Both halves of a pair are imported once per graph — see
            // PingPongTextures.Import — so a clear and a step in one graph import each texture twice,
            // and the second import's entry state is the first's *declaration* rather than its
            // result. Which the validation layer says out loud, once, and is invisible without it.
            fixture.Graph.Reset();
            simulation.Clear(fixture.Graph);
            fixture.Graph.Execute(commands);

            for (var step = 0; step < Steps; step++) {
                fixture.Graph.Reset();

                Disturb(step, (position, radius, amount) => simulation.Inject(position, radius, amount));

                if (simulation.Record(fixture.Graph, effects, pipelines, 1f / 60f)) {
                    recorded++;
                }

                fixture.Graph.Execute(commands);
                simulation.Advance();
            }

            simulation.RecordReadback(commands, readback);
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var raw = new byte[bytes];

        device.Read(readback, 0, raw);
        device.Destroy(readback);

        Assert.Empty(effects.Misses);

        if (VulkanDiagnostics.ErrorCount > 0) {
            Assert.Fail(
                "the ripple step produced validation errors, so what it wrote is meaningless: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }

        ran = recorded == Steps;

        // rgba16f: the displacement is the first half of each eight-byte texel and the rate is the
        // second, which this discards — a rate is not what anything downstream reads. See the
        // simulation's own remarks for why four channels rather than two.
        var field = new float[texels];

        for (var index = 0; index < texels; index++) {
            field[index] = (float)BitConverter.ToHalf(raw, index * 8);
        }

        return field;
    }

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        return false;
    }
}
