// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.IrradianceFields.Tests;

/// <summary>
///     Doc 19 § L2's second exit criterion: a light that moved reaches the field in a bounded number
///     of frames.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two bounds multiplied, and they are worth separating.</b> A frame refills
///         <c>Budget</c> bricks, so a lap over the field takes <c>ceil(BrickCount / Budget)</c> frames
///         — that bound is the round robin's and has nothing to do with light. On top of it, a probe
///         keeps <c>Hysteresis</c> of its previous answer, so what a lap delivers is not the new
///         answer but a step toward it, and the number of laps is the second bound. Conflating them
///         gives one number nobody can act on: a field that lags is either scheduled too thin or
///         damped too hard, and the fix differs.
///     </para>
///     <para>
///         <b>Both are closed forms, so neither is a tolerance around a picture.</b> With no
///         hysteresis a lap replaces every brick outright; with hysteresis <i>h</i> a probe's
///         remaining error after <i>n</i> laps is exactly <i>h^n</i> of what it started with, because
///         the blend is linear and so is the projection it blends.
///     </para>
/// </remarks>
public class IrradianceConvergenceTests {
    /// <summary>A sky whose radiance the test can change, which is what "a light moved" means here.</summary>
    /// <remarks>
    ///     A uniform sky rather than a lamp, for the reason every other filler test uses one: it lights
    ///     every surface with exactly its own radiance whichever way the surface faces, so what a probe
    ///     should hold is a number rather than a shape. Moving a lamp would test the tracer as well as
    ///     the schedule, and the tracer has its own tests.
    /// </remarks>
    sealed class MovingSky : IRadianceSource {
        public float Radiance { get; set; }

        public Vector3 Sky(Vector3 direction) => new(Radiance);

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }

    /// <summary>What every probe of a field reads, brightest and dimmest.</summary>
    static (float Low, float High) Range(IrradianceField field) {
        var low = float.MaxValue;
        var high = float.MinValue;

        foreach (var brick in field.Bricks) {
            for (var z = 0; z < IrradianceBrickPool.BrickResolution; z++) {
                for (var y = 0; y < IrradianceBrickPool.BrickResolution; y++) {
                    for (var x = 0; x < IrradianceBrickPool.BrickResolution; x++) {
                        var lit = field.GetProbe(brick, x, y, z).Irradiance(Vector3.UnitY).X;

                        low = MathF.Min(low, lit);
                        high = MathF.Max(high, lit);
                    }
                }
            }
        }

        return (low, high);
    }

    /// <summary>
    ///     One lap of the round robin is enough, and fewer than one lap is not.
    /// </summary>
    /// <remarks>
    ///     The second half is what makes this a bound rather than an observation. A test that only
    ///     checked the field had caught up after a lap would pass just as well against a filler that
    ///     refilled everything every frame and had no round robin at all.
    /// </remarks>
    [Fact]
    public void ALapIsTheBoundAndPartOfALapIsNotEnough() {
        const int Budget = 2;

        var sky = new MovingSky { Radiance = 1f };
        var field = new IrradianceField(new BoundingBox(new(-4f), new(4f)), new(2));
        var filler = new TracedIrradianceFiller(AnalyticFields.Empty, sky);

        field.AllocateAll();

        // Converged on the old answer first, so what follows is a change rather than a first fill.
        filler.Fill(field);

        var (low, high) = Range(field);

        Assert.Equal(1f, low, 0.02f);
        Assert.Equal(1f, high, 0.02f);

        sky.Radiance = 5f;

        var frames = (field.BrickCount + Budget - 1) / Budget;

        Assert.True(frames > 1, "the fixture gave itself a budget that covers the field in one frame");

        // ⚠ One frame short. Some bricks hold the new answer and some still hold the old, which is the
        // torn state a field is in mid-lap — and asserting it is what says the lap is doing the work.
        for (var frame = 0; frame < frames - 1; frame++) {
            filler.Fill(field, Budget);
        }

        (low, high) = Range(field);

        Assert.Equal(1f, low, 0.02f);
        Assert.Equal(5f, high, 0.02f);

        filler.Fill(field, Budget);

        (low, high) = Range(field);

        Assert.Equal(5f, low, 0.02f);
        Assert.Equal(5f, high, 0.02f);
    }

    /// <summary>
    ///     And with hysteresis, a probe's remaining error after <i>n</i> laps is exactly <i>h^n</i>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What makes the second bound a number a project can choose rather than a feel. Nine
    ///         tenths — the value a filler running every frame wants — leaves a fifth of the change
    ///         outstanding after fifteen laps and a hundredth after forty-four, which is the trade
    ///         doc 19 § L2 states as "lighting that lags a light that moved".
    ///     </para>
    ///     <para>
    ///         Exact rather than approximate because the blend is linear and the projection it blends
    ///         is linear: the coefficients carry the geometric decay with no cross-term to accumulate.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(0.5f, 1)]
    [InlineData(0.5f, 4)]
    [InlineData(0.75f, 2)]
    [InlineData(0.9f, 6)]
    public void HysteresisDecaysTheOldAnswerGeometrically(float hysteresis, int laps) {
        const float Before = 1f;
        const float After = 5f;

        var sky = new MovingSky { Radiance = Before };
        var field = new IrradianceField(new BoundingBox(new(-4f), new(4f)), new(1));

        var filler = new TracedIrradianceFiller(
            AnalyticFields.Empty,
            sky,
            new IrradianceFillSettings { Hysteresis = hysteresis }
        );

        field.AllocateAll();

        // ⚠ Converged, not filled once. A first fill from an empty probe starts at zero, so the decay
        // below would be measured from a state no running field is ever in.
        for (var settle = 0; settle < 200; settle++) {
            filler.Fill(field);
        }

        Assert.Equal(Before, Range(field).Low, 0.01f);

        sky.Radiance = After;

        for (var lap = 0; lap < laps; lap++) {
            filler.Fill(field);
        }

        var expected = After + ((Before - After) * MathF.Pow(hysteresis, laps));
        var (low, high) = Range(field);

        Assert.Equal(expected, low, 0.02f);
        Assert.Equal(expected, high, 0.02f);
    }

    /// <summary>
    ///     <b>And the bound holds on a refined field, where a lap is more bricks than cells.</b>
    /// </summary>
    /// <remarks>
    ///     The cursor walks indirection <i>cells</i> and stops only at the one a brick calls its
    ///     origin, so a field of eight fine bricks and one coarse one is nine stops over a grid of
    ///     more cells than that. A bound derived from the grid rather than from the brick count would
    ///     be wrong in the direction that matters — it would promise a lap sooner than one happens.
    /// </remarks>
    [Fact]
    public void ARefinedFieldStillCatchesUpWithinALap() {
        const int Budget = 3;

        var sky = new MovingSky { Radiance = 2f };
        var field = new IrradianceField(new BoundingBox(new(-4f), new(4f)), new(4), new(new(4)));
        var filler = new TracedIrradianceFiller(AnalyticFields.Empty, sky);

        field.AllocateAll(2);
        field.Refine(new(new(-4f), new(-3f)));
        filler.Fill(field);

        Assert.Equal(2f, Range(field).High, 0.02f);

        sky.Radiance = 7f;

        // The bound is over bricks, and a coarse brick counts once however many cells it covers.
        for (var frame = 0; frame < (field.BrickCount + Budget - 1) / Budget; frame++) {
            filler.Fill(field, Budget);
        }

        var (low, high) = Range(field);

        Assert.Equal(7f, low, 0.02f);
        Assert.Equal(7f, high, 0.02f);
    }
}
