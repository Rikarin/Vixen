// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Water;
using Xunit;

namespace Vixen.Water.Tests;

/// <summary>
///     One evaluator, and the properties the other host will be held to — [docs/plan/35 § D2].
/// </summary>
/// <remarks>
///     <para>
///         The seam test itself needs a device and a Raven module and arrives in W2. What is here is
///         everything about the evaluator that can be stated without one, and the two properties
///         W2's test will rest on: that the surface is a closed-form function of position and time,
///         and that asking for it costs nothing.
///     </para>
///     <para>
///         ⚠ Both are load-bearing for reasons outside rendering. Closed form is what lets a rollback
///         ask where the surface was six ticks ago; allocation-freedom is what lets a hundred pontoons
///         and forty swimming characters ask at all.
///     </para>
/// </remarks>
public sealed class WaterEvaluatorTests {
    static WaterQuery OpenSea(WaterWaveSpectrum? spectrum = null) =>
        new(null, spectrum ?? WaterWaveSpectrum.Default);

    static WaterQuery Lake(float surface = 5f, float ground = 0f, float extent = 64f) {
        var spline = new Spline(
            Spline.SmoothTangents(
                [
                    new(-extent * 0.4f, surface, -extent * 0.4f),
                    new(extent * 0.4f, surface, -extent * 0.4f),
                    new(extent * 0.4f, surface, extent * 0.4f),
                    new(-extent * 0.4f, surface, extent * 0.4f)
                ],
                closed: true,
                tension: 1f
            ),
            closed: true
        );

        var body = new WaterBody(
            WaterBodyKind.Lake,
            spline,
            defaults: new() { Depth = surface - ground }
        ) {
            SurfaceHeight = surface,
            ShoreFalloff = 2f,
            BedRamp = 4f
        };

        var field = new WaterField(new() {
            Origin = new(-extent * 0.5f, -extent * 0.5f),
            Extent = extent,
            Resolution = 65
        });

        field.Rasterize([body], new FlatWaterGround(ground));

        return new(field, WaterWaveSpectrum.Calm);
    }

    // --- Closed form ---------------------------------------------------------

    /// <summary>
    ///     ⚠ The surface at time <c>t</c> is the same whether or not anything simulated up to it.
    /// </summary>
    /// <remarks>
    ///     [docs/plan/35 § Part 4]'s one-assertion row, and it is what makes rollback possible. A
    ///     model whose state depends on the frames that went before it cannot answer "where was the
    ///     surface six ticks ago" without having kept six frames of it — which is why
    ///     [§ D7] puts Gerstner before FFT, and why the ordering is arithmetic rather than taste.
    /// </remarks>
    [Fact]
    public void TheSurfaceAtATimeIsTheSameEvaluatedColdOrWalkedUpTo() {
        var query = OpenSea();
        var position = new Vector2(37.5f, -12.25f);

        var cold = query.ClosedForm(position, 12f);

        for (var step = 0; step < 720; step++) {
            query.ClosedForm(position, step / 60f);
        }

        var afterwards = query.ClosedForm(position, 12f);

        Assert.Equal(cold.Height, afterwards.Height);
        Assert.Equal(cold.Normal, afterwards.Normal);

        // And six ticks into the past costs exactly what the present costs, which is the property a
        // predicted, rewound buoyancy force needs.
        Assert.NotEqual(cold.Height, query.ClosedForm(position, 12f - (6f / 60f)).Height);
    }

    /// <summary>
    ///     ⚠ Ten thousand queries allocate nothing.
    /// </summary>
    /// <remarks>
    ///     W1's stated exit criterion. A hundred pontoons and forty swimming characters ask this per
    ///     fixed step, so an allocation per query is six kilobytes a second of garbage on an empty
    ///     scene — which is the shape of bug that never gets attributed to the thing causing it.
    /// </remarks>
    [Fact]
    public void TenThousandQueriesAllocateNothing() {
        var query = Lake();
        var evaluator = query.Evaluator();

        // Warm the paths first: the first call through a method touches its own code, which is not
        // what is being measured.
        for (var step = 0; step < 128; step++) {
            evaluator.Sample(new(step * 0.1f, 0f), step * 0.01f);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var accumulated = 0f;

        for (var step = 0; step < 10_000; step++) {
            var position = new Vector2((step % 100) * 0.2f, (step / 100) * 0.2f);
            var sample = evaluator.Sample(position, step * 0.001f);

            accumulated += sample.Height + sample.Normal.Y + sample.Depth;
            accumulated += evaluator.Height(position, step * 0.001f);
            accumulated += evaluator.Immersion(position, 4f, 1.8f, step * 0.001f);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"ten thousand queries allocated {allocated} bytes");
        Assert.NotEqual(0f, accumulated);
    }

    // --- The wave sum --------------------------------------------------------

    /// <summary>Open water with no sea state is flat, and with one is not.</summary>
    [Fact]
    public void ASeaStateIsWhatMakesTheSurfaceMove() {
        var still = new WaterQuery(null, WaterWaveSpectrum.Default with { AmplitudeScale = 0f });

        Assert.Equal(0f, still.Height(new(10f, 10f), 3f), 1e-6f);

        var moving = OpenSea();

        Assert.NotEqual(0f, moving.Height(new(10f, 10f), 3f));
    }

    /// <summary>The surface stays inside the bound the spectrum states.</summary>
    [Fact]
    public void TheSurfaceStaysInsideItsStatedBound() {
        var query = OpenSea();
        var bound = query.MaximumAmplitude;

        Gen.Float[-500f, 500f]
            .Select(Gen.Float[-500f, 500f], Gen.Float[0f, 600f])
            .Sample(
                triple => {
                    var (x, z, time) = triple;

                    Assert.InRange(query.Height(new(x, z), time), -bound, bound);
                },
                iter: 20_000
            );
    }

    /// <summary>The normal is unit length and points up, everywhere.</summary>
    /// <remarks>
    ///     ⚠ A normal that has folded through itself reaches the lighting as a black pixel that
    ///     persists for as long as the crest does, and the surface folds whenever the steepness is
    ///     pushed past one — which an author is free to do.
    /// </remarks>
    [Fact]
    public void TheNormalIsUnitAndUpwardEvenWhenTheSurfaceFolds() {
        var steep = OpenSea(WaterWaveSpectrum.Default with { Steepness = 1f, AmplitudeScale = 4f });
        var evaluator = steep.Evaluator();

        for (var step = 0; step < 5_000; step++) {
            var position = new Vector2(step * 0.31f, step * -0.17f);

            evaluator.Displace(position, step * 0.013f, 1f, out _, out var normal);

            Assert.Equal(1f, normal.Length(), 1e-4f);
            Assert.True(normal.Y > 0f, $"the normal turned over at {position}");
        }
    }

    /// <summary>Waves die out as the ground rises, so no crest intersects the beach.</summary>
    /// <remarks>
    ///     ⚠ [docs/plan/35 § D7]'s reason for putting attenuation in the evaluator rather than in the
    ///     material: the visible half is easy to fix in a shader, and the project then discovers that
    ///     buoyancy is still using the unattenuated height and a boat in the shallows is bobbing
    ///     through the sand.
    /// </remarks>
    [Fact]
    public void WavesDieOutWhereTheWaterIsShallow() {
        var query = Lake(surface: 5f, ground: 0f);

        query.SetSpectrum(WaterWaveSpectrum.Default with { AmplitudeScale = 2f });
        query.Attenuation = new(3f);

        // In the middle the lake is five metres deep, so the sea state is at full height and the
        // surface moves.
        var deep = 0f;
        var shallow = 0f;

        for (var step = 0; step < 240; step++) {
            deep = MathF.Max(deep, MathF.Abs(query.Height(Vector2.Zero, step / 30f) - 5f));

            // Right at the shoreline the water is a few centimetres deep and the swell is gone.
            var edge = query.Sample(new(0f, 25.6f), step / 30f);

            if (edge.IsWet && edge.Depth < 0.25f) {
                shallow = MathF.Max(shallow, MathF.Abs(edge.Height - 5f));
            }
        }

        Assert.True(deep > 0.05f, $"the swell in deep water only reached {deep} m");
        Assert.True(shallow < deep * 0.2f, $"the swell at the shoreline was {shallow} m against {deep} m");
    }

    /// <summary>
    ///     ⚠ An unset attenuation takes the default, through every door.
    /// </summary>
    /// <remarks>
    ///     Zero is what a defaulted argument and a zeroed struct both hold, and an attenuation of
    ///     zero is a swell lapping over dry sand — the contract is that waves are gone at zero depth.
    ///     The constructor always folded it; the settable property once did not, so zero meant two
    ///     different things depending on which entry path it came in through.
    /// </remarks>
    [Fact]
    public void AnUnsetAttenuationTakesTheDefaultOnEveryPath() {
        var query = new WaterQuery(null, WaterWaveSpectrum.Calm, new WaterAttenuation(0f));

        Assert.Equal(WaterAttenuation.Default, query.Attenuation);

        query.Attenuation = new(3f);
        Assert.Equal(new WaterAttenuation(3f), query.Attenuation);

        query.Attenuation = new(0f);
        Assert.Equal(WaterAttenuation.Default, query.Attenuation);

        query.Attenuation = new(-1f);
        Assert.Equal(WaterAttenuation.Default, query.Attenuation);
    }

    /// <summary>Dry land answers nothing, and says so rather than answering zero.</summary>
    [Fact]
    public void DryLandIsNotWaterAtHeightZero() {
        var query = Lake();
        var outside = query.Sample(new(1_000f, 1_000f), 4f);

        Assert.False(outside.IsWet);
        Assert.Equal(0f, outside.Coverage);
        Assert.Equal(Vector3.UnitY, outside.Normal);
    }

    // --- Immersion -----------------------------------------------------------

    /// <summary>Immersion is monotone in the capsule's height — [docs/plan/35 § Part 4].</summary>
    /// <remarks>
    ///     ⚠ A non-monotone immersion is a character that becomes <em>less</em> submerged by sinking,
    ///     and no amount of hysteresis on the movement mode's thresholds can save an input that goes
    ///     backwards.
    /// </remarks>
    [Fact]
    public void ImmersionIsMonotoneInTheCapsulesHeight() {
        var query = Lake(surface: 5f, ground: 0f);
        var previous = 1f;

        for (var step = 0; step <= 200; step++) {
            var bottom = step * 0.05f;
            var immersion = query.Immersion(Vector2.Zero, bottom, 1.8f, 3f);

            Assert.InRange(immersion, 0f, 1f);
            Assert.True(
                immersion <= previous + 1e-5f,
                $"a capsule that rose from {bottom - 0.05f} to {bottom} became more submerged"
            );

            previous = immersion;
        }

        // Standing on the bed of a five-metre lake, a 1.8 m character is fully under.
        Assert.Equal(1f, query.Immersion(Vector2.Zero, 0f, 1.8f, 3f), 1e-4f);

        // And standing on the surface, not at all.
        Assert.Equal(0f, query.Immersion(Vector2.Zero, 20f, 1.8f, 3f));
    }

    /// <summary>A capsule of no height is not submerged rather than dividing by nothing.</summary>
    [Fact]
    public void AZeroHeightCapsuleIsNotSubmerged() {
        var query = Lake();

        Assert.Equal(0f, query.Immersion(Vector2.Zero, 0f, 0f, 1f));
        Assert.False(float.IsNaN(query.Immersion(Vector2.Zero, 0f, 0f, 1f)));
    }

    // --- Ripples -------------------------------------------------------------

    /// <summary>
    ///     ⚠ The closed form does not see the ripple simulation, and that is what the network path
    ///     asks for.
    /// </summary>
    /// <remarks>
    ///     [docs/plan/35 § D12]. The closed-form part is exact and reproducible at any past time; the
    ///     simulated part is neither, and a rollback that included it would be re-deriving a height
    ///     field it does not have. The signature is what enforces the separation, rather than a
    ///     comment asking for it.
    /// </remarks>
    [Fact]
    public void TheClosedFormIgnoresTheRippleSimulation() {
        var query = Lake();

        query.Ripples = new ConstantRipples(0.5f);

        var withRipples = query.Sample(Vector2.Zero, 2f);
        var closedForm = query.ClosedForm(Vector2.Zero, 2f);

        Assert.Equal(closedForm.Height + 0.5f, withRipples.Height, 1e-5f);

        // Removing the simulation puts Sample back where ClosedForm already was.
        query.Ripples = null;
        Assert.Equal(closedForm.Height, query.Sample(Vector2.Zero, 2f).Height);
    }

    /// <summary>A simulation lifts the surface only where its window reaches.</summary>
    sealed class ConstantRipples(float lift) : IWaterRipples {
        public bool TryDisplacement(Vector2 position, out float displacement) {
            displacement = position.Length() < 20f ? lift : 0f;
            return position.Length() < 20f;
        }
    }
}
