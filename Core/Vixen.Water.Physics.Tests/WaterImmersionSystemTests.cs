// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Physics.Characters;
using Vixen.Water;
using Vixen.Water.Physics;
using Xunit;
using EcsWorld = Vixen.Ecs.World;

namespace Tests;

/// <summary>
///     A character in a lake gets the immersion its capsule predicts — [docs/plan/35 § D11].
/// </summary>
/// <remarks>
///     <para>
///         <b>The number, and not the mode.</b> <c>CharacterSwimmingTests</c> in
///         <c>Vixen.Physics.Tests</c> already pins every rule that reads
///         <c>CharacterState.Immersion</c> — the two thresholds, the hysteresis, the restoring force.
///         What was never tested is the one thing that <em>writes</em> it, and until this file existed
///         there was no test in the tree that named <c>WaterImmersionSystem</c> at all.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is a fraction and not a height, because the fraction is where the
///         capsule derivation shows.</b> <c>CharacterMovement.ShapeOffset</c> lifts the capsule's
///         centre off the feet, so the capsule's full height is twice it — and a writer that passed
///         the offset itself produces a saturated 1.0 for any character more than shoulder-deep, which
///         reads as a correct swim and is wrong at every depth below it.
///     </para>
/// </remarks>
public sealed class WaterImmersionSystemTests : IDisposable {
    readonly EcsWorld world = new("Test");

    /// <inheritdoc />
    public void Dispose() {
        world.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Still water at a height over a square extent, and dry land outside it.</summary>
    /// <remarks>
    ///     ⚠ <b>A rasterised field rather than a fake that answers a height.</b> A fake surface would
    ///     let the join pass while the thing a game actually holds did not — the same argument
    ///     <c>BuoyancySystemTests.Lake</c> makes, and the reason both files build a real
    ///     <see cref="WaterQuery" />.
    /// </remarks>
    sealed class Lake(float height, float extent = 200f, WaterWaveSpectrum? sea = null) : IWaterSurface {
        readonly WaterQuery query = Build(height, extent, sea);

        public float WaterTime { get; set; }

        public WaterQuery? QueryAt(Vector2 position) =>
            MathF.Abs(position.X) <= extent * 0.5f && MathF.Abs(position.Y) <= extent * 0.5f
                ? query
                : null;

        static WaterQuery Build(float height, float extent, WaterWaveSpectrum? sea) {
            var field = new WaterField(
                new() { Origin = new(-extent * 0.5f, -extent * 0.5f), Extent = extent, Resolution = 65 }
            );

            var half = extent * 0.4f;

            var lake = new WaterBody(
                WaterBodyKind.Lake,
                new Spline(
                    Spline.SmoothTangents(
                        [
                            new(-half, height, -half), new(half, height, -half),
                            new(half, height, half), new(-half, height, half)
                        ],
                        closed: true,
                        tension: 1f
                    ),
                    closed: true
                ),
                defaults: new() { Depth = 20f }
            ) {
                SurfaceHeight = height,
                ShoreFalloff = 2f
            };

            field.Rasterize([lake], new FlatWaterGround(height - 20f));

            // A dead calm unless a test asked for a sea, so an immersion is a depth and not a depth
            // plus wherever in its cycle the swell happened to be.
            return new(field, sea ?? WaterWaveSpectrum.Calm with { WindSpeed = 0f, AmplitudeScale = 0f });
        }
    }

    /// <summary>A character standing at a position, with the shipped tuning.</summary>
    Entity Character(Vector3 at, float shapeOffset = 0.9f) {
        var entity = world.Create();

        world.Add(entity, CharacterMovement.Default with { ShapeOffset = new(0f, shapeOffset, 0f) });
        world.Add(entity, new CharacterState());
        world.Add(entity, LocalTransform.At(at));

        return entity;
    }

    // --- The capsule ---------------------------------------------------------

    /// <summary>
    ///     A character up to its shoulders reads the fraction of its <em>capsule</em> that is under.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The assertion that pins the doubling.</b> The shipped offset is 0.9 m, so the capsule
    ///     is 1.8 m and feet 1.5 m under the surface are 1.5 / 1.8 = 0.833 submerged. A writer that
    ///     passed <c>ShapeOffset.Y</c> as the height reads 1.5 / 0.9, saturated to <b>1.0</b> — fully
    ///     under, which is a plausible-looking answer and is wrong for every depth over 0.9 m. The
    ///     depth is deliberately one where the two disagree and both are still "swimming", so the test
    ///     cannot be satisfied by the mode.
    /// </remarks>
    [Fact]
    public void ACharacterReadsTheFractionOfItsCapsuleThatIsUnder() {
        var surface = new Lake(0f);
        var immersion = new WaterImmersionSystem(surface);
        var character = Character(new(0f, -1.5f, 0f));

        immersion.Step(world);

        Assert.Equal(1.5f / 1.8f, world.Read<CharacterState>(character).Immersion, 0.01f);
        Assert.Equal(1, immersion.Swimming);
    }

    /// <summary>Ankle-deep is ankle-deep, and it is nowhere near swimming.</summary>
    /// <remarks>
    ///     The other end of the same claim. Without it, a writer that always answered 1.0 would pass
    ///     the test above by accident of the depth chosen there.
    /// </remarks>
    [Fact]
    public void ACharacterInTheShallowsIsBarelyImmersedAndDoesNotSwim() {
        var surface = new Lake(0f);
        var character = Character(new(0f, -0.2f, 0f));

        new WaterImmersionSystem(surface).Step(world);

        Assert.Equal(0.2f / 1.8f, world.Read<CharacterState>(character).Immersion, 0.01f);
    }

    /// <summary>A character whose capsule has no height still gets a number and not a NaN.</summary>
    /// <remarks>
    ///     ⚠ <b>The floor under the height is load-bearing, and its absence is silent.</b> A
    ///     <c>ShapeOffset</c> left at its zero — which is what a hand-built <c>CharacterMovement</c>
    ///     that forgot it has — makes the capsule zero metres tall, and the evaluator answers 0 for a
    ///     non-positive height. So the character would read bone dry at the bottom of a lake rather
    ///     than erroring, and the symptom is one character in a scene that never swims.
    /// </remarks>
    [Fact]
    public void ACharacterWithNoAuthoredShapeStillMeasuresAsSubmerged() {
        var surface = new Lake(0f);
        var character = Character(new(0f, -1.5f, 0f), shapeOffset: 0f);

        new WaterImmersionSystem(surface).Step(world);

        var measured = world.Read<CharacterState>(character).Immersion;

        Assert.False(float.IsNaN(measured), "the immersion is not a number");
        Assert.Equal(1f, measured, 0.001f);
    }

    // --- Leaving the water ---------------------------------------------------

    /// <summary>
    ///     ⚠ A character that walks out of every zone reads dry, rather than keeping its last answer.
    /// </summary>
    /// <remarks>
    ///     <b>The negative control for the whole file.</b> Null from <c>QueryAt</c> is "no zone claims
    ///     this position", which is dry — and it is not the same as a zero left over from last step. A
    ///     writer that simply skipped an unclaimed character keeps its old immersion for ever, and the
    ///     symptom is a player who swims across the car park after leaving the lake.
    /// </remarks>
    [Fact]
    public void ACharacterThatLeavesEveryZoneReadsDry() {
        var surface = new Lake(0f, extent: 20f);
        var immersion = new WaterImmersionSystem(surface);
        var character = Character(new(0f, -1.5f, 0f));

        immersion.Step(world);
        Assert.True(world.Read<CharacterState>(character).Immersion > 0.5f, "it never got wet");
        Assert.Equal(1, immersion.Swimming);

        world.Set(character, LocalTransform.At(new(500f, -1.5f, 0f)));
        immersion.Step(world);

        Assert.Equal(0f, world.Read<CharacterState>(character).Immersion);
        Assert.Equal(0, immersion.Swimming);
    }

    // --- The clock -----------------------------------------------------------

    /// <summary>
    ///     ⚠ The swell a character bobs on is the surface's clock, not the frame's.
    /// </summary>
    /// <remarks>
    ///     <b>There is one water time and <c>WaterClockSystem</c> is its only writer.</b> A writer that
    ///     reached for <c>GameTime</c> would be a second definition of "when", and a swimmer bobbing on
    ///     a different swell from the one drawn under them is exactly the disagreement the one-clock
    ///     rule is against. Advancing only the surface's time has to change the answer; if the system
    ///     had its own clock, this would read the same twice.
    /// </remarks>
    [Fact]
    public void TheSwellComesFromTheSurfacesOwnClock() {
        var surface = new Lake(0f, sea: WaterWaveSpectrum.Calm);
        var character = Character(new(0f, -0.9f, 0f));

        new WaterImmersionSystem(surface) { Surface = surface }.Step(world);
        var atRest = world.Read<CharacterState>(character).Immersion;

        var moved = false;

        // Somewhere in a swell's cycle the surface is at a different height. One sample could land on
        // the crossing, so the assertion is over the cycle rather than at one instant.
        for (var step = 1; step <= 240 && !moved; step++) {
            surface.WaterTime = step * (1f / 60f);
            new WaterImmersionSystem(surface).Step(world);

            moved = MathF.Abs(world.Read<CharacterState>(character).Immersion - atRest) > 1e-4f;
        }

        Assert.True(moved, "the water time never reached the measurement");
    }
}
