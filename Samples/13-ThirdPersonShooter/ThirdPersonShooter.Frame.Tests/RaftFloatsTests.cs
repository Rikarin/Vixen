// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Water;
using Xunit;

namespace Vixen.Samples.ThirdPersonShooter.Frame.Tests;

/// <summary>The authored raft floats, at the waterline its own numbers predict.</summary>
/// <remarks>
///     <para>
///         <b>Every one of these numbers is in a file a person edits, and none of them is checked by
///         anything else.</b> A raft is a mass, a coefficient and four sphere radii, and every way of
///         getting them wrong draws a frame nobody would call broken: too heavy and it sinks out of
///         sight, too light and it skates on top of the water like a decal, and either one is a
///         picture somebody would have to already know the right answer to doubt.
///     </para>
///     <para>
///         ⚠ <b>The waterline is asserted and the displaced volume deliberately is not.</b> At
///         equilibrium the lift equals the weight <em>by definition</em>, so "the displacement equals
///         <c>RestDisplacement</c>" is true for any coefficient — both sides scale by it — and a test
///         asserting only that passes with the term deleted. <c>BuoyancySystemTests</c> states the
///         same rule about the solver; this states it about the content.
///     </para>
///     <para>
///         ⚠ <b>It asks the shipped functions rather than restating the arithmetic.</b>
///         <see cref="Buoyancy.SubmergedFraction" /> is the exact spherical cap and inverting it is a
///         cubic; a second implementation here would be a test that agrees with itself. It is
///         bisected instead, which is what <c>BuoyancySystemTests.WaterlineOf</c> does and for the
///         same reason.
///     </para>
///     <para>
///         Read out of <c>Arena.vxscene</c> rather than from constants copied here, because a copy is
///         the thing that drifts — the same argument every other test over this file makes.
///     </para>
/// </remarks>
public sealed class RaftFloatsTests {
    /// <summary>What the crate cube is, in metres: a scale is the wanted size over this.</summary>
    /// <remarks>
    ///     <c>crate.obj</c> is a 1.6 m cube with its origin at its <em>base</em>, which is the whole
    ///     reason the deck's collider carries a <c>centre</c> and the pontoons an offset in y.
    /// </remarks>
    const float Cube = 1.6f;

    /// <summary>Four pontoons, not one, and the file has to say so.</summary>
    /// <remarks>
    ///     ⚠ <b>A single pontoon bobs and never rolls</b>, because a force at one point cannot make a
    ///     torque about it — the corners are what tell the solver about the attitude. A hull that
    ///     lost three of its four in an edit still floats, at the right height, perfectly level, for
    ///     ever; nothing about the picture says which one you are looking at.
    /// </remarks>
    [Fact]
    public void The_raft_has_four_pontoons_inside_its_own_deck() {
        var raft = Raft();

        Assert.Equal(4, raft.Pontoons.Count);

        var half = raft.HalfExtents;

        foreach (var pontoon in raft.Pontoons) {
            Assert.True(
                MathF.Abs(pontoon.Offset.X) < half.X && MathF.Abs(pontoon.Offset.Z) < half.Z,
                $"a pontoon at {pontoon.Offset} is outside the deck it is supposed to be holding up"
            );

            Assert.True(pontoon.Radius > 0f, "a pontoon of no radius displaces nothing");
        }
    }

    /// <summary>
    ///     ⚠ The damping is not zero, which is the one zero <c>BuoyancyBody.Settings</c> does not
    ///     rescue.
    /// </summary>
    /// <remarks>
    ///     An unset <c>coefficient</c> becomes one there — see the field's own remarks — and an unset
    ///     <c>damping</c> stays zero, which is a restoring force with no losses, which is a pendulum:
    ///     a hull that oscillates on the surface for ever and reads as the solver being wrong rather
    ///     than as a blank field in a file.
    /// </remarks>
    [Fact]
    public void The_raft_is_damped_and_carries_a_mass() {
        var raft = Raft();

        Assert.True(raft.Damping > 0f, "an undamped hull never settles");
        Assert.True(raft.Mass > 0f, "a zero mass takes the shape's, which for a box is not what was solved for");
        Assert.True(raft.Coefficient > 0f, "a coefficient of zero is a boat with no buoyancy at all");
    }

    /// <summary>
    ///     The deck rests <em>cut by</em> the water: its base under the surface and its top above.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>§ W7's exit criterion applied to content.</b> A raft that floats is not evidence —
    ///         one with twice the lift also floats, higher, and looks entirely convincing. What is
    ///         asserted is where its own numbers put it: the rest displacement over the pontoon
    ///         volume is the submerged fraction, the fraction inverted through the spherical cap is
    ///         the cap depth, and the cap depth places the pontoon centres and therefore the deck.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both bounds matter and they fail differently.</b> A deck whose base is above the
    ///         surface is a raft skating on the water with nothing in it, which is what too little
    ///         mass looks like; a deck whose top is below is a raft awash, which is what too much
    ///         looks like. Between them is the only band that reads, in a picture, as a boat.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_deck_rests_with_the_waterline_across_it() {
        var raft = Raft();

        var settings = new BuoyancySettings {
            Coefficient = raft.Coefficient,
            Damping = raft.Damping
        };

        var spheres = raft.Pontoons.ToArray();
        var volume = 0f;

        foreach (var pontoon in spheres) {
            volume += pontoon.Volume;
        }

        var displaced = Buoyancy.RestDisplacement(spheres, raft.Mass, settings);

        // ⚠ Strictly less than the pontoons hold, and with room to spare. RestDisplacement clamps to
        // what is available, so a raft too heavy to float reports the volume it *has* rather than the
        // volume it needed — which reads as a body floating fully submerged rather than as one that
        // sank. Equality is the failure, not the boundary of one.
        Assert.True(
            displaced < volume * 0.9f,
            $"the raft needs {displaced:0.000} m³ of the {volume:0.000} m³ its pontoons hold: it is at or near its limit"
        );

        var fraction = displaced / volume;

        // Every pontoon has the same radius here, so one cap answers for all four.
        var radius = spheres[0].Radius;

        foreach (var pontoon in spheres) {
            Assert.Equal(radius, pontoon.Radius, 4);
        }

        // Where a sphere's centre sits when that much of it is under, with the surface pinned at zero.
        var centre = WaterlineOf(radius, fraction);

        // The pontoons are authored in the deck's own base-origin frame, so subtracting their y is
        // what turns "where the sphere floats" into "where the deck's underside is".
        var height = spheres[0].Offset.Y;

        foreach (var pontoon in spheres) {
            Assert.Equal(height, pontoon.Offset.Y, 4);
        }

        var surface = SurfaceHeight();
        var deckBase = surface + centre - height;
        var deckTop = deckBase + (raft.HalfExtents.Y * 2f);

        Assert.True(
            deckBase < surface,
            $"the deck's base rests at {deckBase:0.000}, above the surface at {surface:0.000}: it is skating on the water"
        );

        Assert.True(
            deckTop > surface,
            $"the deck's top rests at {deckTop:0.000}, below the surface at {surface:0.000}: the raft is awash"
        );
    }

    /// <summary>
    ///     ⚠ The mesh's scale and the collider's half-extents describe the same box, in two units.
    /// </summary>
    /// <remarks>
    ///     <c>scale</c> moves the mesh and does not move the collider — <c>Arena.BuildCollision</c>
    ///     reads <c>halfExtents</c> straight into a shape and never looks at a transform — so the two
    ///     are kept in step by hand, exactly as the perimeter walls' are. A deck you can see and one
    ///     you can stand on that are different sizes is a raft with an invisible lip, and the
    ///     pontoons would be holding up neither of them at the height they were solved for.
    /// </remarks>
    [Fact]
    public void The_decks_mesh_and_its_collider_are_the_same_box() {
        var raft = Raft();

        Assert.Equal(raft.Scale.X * Cube / 2f, raft.HalfExtents.X, 3);
        Assert.Equal(raft.Scale.Y * Cube / 2f, raft.HalfExtents.Y, 3);
        Assert.Equal(raft.Scale.Z * Cube / 2f, raft.HalfExtents.Z, 3);

        // And the shape is lifted by half a deck, because the mesh's origin is at its base and a
        // box shape's is at its middle. Without it the collider sits half a deck low and the
        // pontoons, authored in the base frame, float a hull that is not where it is drawn.
        Assert.Equal(raft.HalfExtents.Y, raft.Centre.Y, 3);
    }

    /// <summary>
    ///     ⚠ The raft is inside the ring the lake's spline draws, or it is floating on dry land.
    /// </summary>
    /// <remarks>
    ///     The body's field runs out at the curve — plus a shore falloff that fades it — so a raft
    ///     authored outside it is a body no zone's water reaches. It does not error: it falls, with
    ///     <c>Pontoons</c> counted and <c>WetPontoons</c> at zero, which is the reading
    ///     <c>SampleLog.RaftFloated</c> exists to make visible.
    /// </remarks>
    [Fact]
    public void The_raft_is_inside_the_lake_it_is_floating_on() {
        var raft = Raft();
        var lake = PositionOf("Lake");
        var ring = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Water", "Lake.vxspline"));

        // The ring's radius, taken as how far its furthest control point reaches — read rather than
        // restated, for the reason every other number here is. A 24-gon's points are all at the
        // radius, so the maximum is it.
        var radius = 0f;

        foreach (var raw in ring.Split('\n')) {
            var line = raw.Trim();

            if (!line.StartsWith("- position:", StringComparison.Ordinal)) {
                continue;
            }

            var point = Numbers(line["- position:".Length..]);

            radius = MathF.Max(radius, new Vector2(point[0], point[2]).Length());
        }

        Assert.True(radius > 0f, "the lake's ring has no radius in it");

        var offset = new Vector2(raft.Position.X - lake.X, raft.Position.Z - lake.Z);

        Assert.True(
            offset.Length() < radius,
            $"the raft is {offset.Length():0.0} m from the lake's centre and the water runs out at {radius:0.0} m"
        );
    }

    /// <summary>The centre height of a sphere floating with a given fraction submerged.</summary>
    /// <remarks>
    ///     ⚠ <b>Bisected against the shipped <see cref="Buoyancy.SubmergedFraction" /> rather than
    ///     solved in closed form.</b> Inverting the spherical cap is a cubic, and a second
    ///     implementation of the cap here would be a test that agrees with itself.
    /// </remarks>
    static float WaterlineOf(float radius, float fraction) {
        var low = -radius;
        var high = radius;

        for (var step = 0; step < 60; step++) {
            var middle = (low + high) * 0.5f;

            if (Buoyancy.SubmergedFraction(radius, middle, 0f) < fraction) {
                high = middle;
            } else {
                low = middle;
            }
        }

        return (low + high) * 0.5f;
    }

    // --- Reading the scene --------------------------------------------------

    static string[] Scene() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Scenes", "Arena.vxscene")).Split('\n');

    /// <summary>Every line of one named root, up to the next one.</summary>
    static List<string> Block(string name) {
        var lines = Scene();
        var found = new List<string>();
        var inside = false;

        foreach (var raw in lines) {
            var line = raw.Trim();

            if (line.StartsWith("- name:", StringComparison.Ordinal)) {
                if (inside) {
                    break;
                }

                inside = line["- name:".Length..].Trim() == name;

                continue;
            }

            if (inside) {
                found.Add(line);
            }
        }

        Assert.True(found.Count > 0, $"Arena.vxscene has no root called '{name}'");

        return found;
    }

    static Vector3 PositionOf(string name) {
        foreach (var line in Block(name)) {
            if (line.StartsWith("position:", StringComparison.Ordinal)) {
                var numbers = Numbers(line["position:".Length..]);

                return new(numbers[0], numbers[1], numbers[2]);
            }
        }

        Assert.Fail($"'{name}' has no position");

        return default;
    }

    static float SurfaceHeight() {
        foreach (var line in Block("Lake")) {
            if (line.StartsWith("surfaceHeight:", StringComparison.Ordinal)) {
                return Numbers(line["surfaceHeight:".Length..])[0];
            }
        }

        Assert.Fail("the lake body has no surfaceHeight");

        return 0f;
    }

    static Authored Raft() {
        var block = Block("Raft");
        var raft = new Authored { Position = PositionOf("Raft") };
        var pontoons = new List<BuoyancyPontoon>();
        var offset = default(Vector3?);

        foreach (var line in block) {
            if (line.StartsWith("scale:", StringComparison.Ordinal)) {
                var numbers = Numbers(line["scale:".Length..]);

                raft.Scale = new(numbers[0], numbers[1], numbers[2]);
            } else if (line.StartsWith("- !BoxCollision", StringComparison.Ordinal)) {
                var half = Numbers(After(line, "halfExtents:"));
                var centre = Numbers(After(line, "centre:"));

                raft.HalfExtents = new(half[0], half[1], half[2]);
                raft.Centre = new(centre[0], centre[1], centre[2]);
            } else if (line.StartsWith("mass:", StringComparison.Ordinal)) {
                raft.Mass = Numbers(line["mass:".Length..])[0];
            } else if (line.StartsWith("coefficient:", StringComparison.Ordinal)) {
                raft.Coefficient = Numbers(line["coefficient:".Length..])[0];
            } else if (line.StartsWith("damping:", StringComparison.Ordinal)) {
                raft.Damping = Numbers(line["damping:".Length..])[0];
            } else if (line.StartsWith("- offset:", StringComparison.Ordinal)) {
                var numbers = Numbers(line["- offset:".Length..]);

                offset = new(numbers[0], numbers[1], numbers[2]);
            } else if (line.StartsWith("radius:", StringComparison.Ordinal) && offset is { } at) {
                pontoons.Add(new(at, Numbers(line["radius:".Length..])[0]));
                offset = null;
            }
        }

        raft.Pontoons = pontoons;

        return raft;
    }

    /// <summary>The tail of an inline mapping after a key, up to the next key or the brace.</summary>
    static string After(string line, string key) {
        var at = line.IndexOf(key, StringComparison.Ordinal);

        Assert.True(at >= 0, $"'{line}' has no {key}");

        return line[(at + key.Length)..];
    }

    /// <summary>Every leading number in a fragment, stopping at the first thing that is not one.</summary>
    static float[] Numbers(string text) {
        var found = new List<float>();

        foreach (var token in text.Split([' ', ',', '}', '\r'], StringSplitOptions.RemoveEmptyEntries)) {
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                break;
            }

            found.Add(value);
        }

        Assert.True(found.Count > 0, $"no number in '{text}'");

        return [.. found];
    }

    sealed class Authored {
        public Vector3 Position { get; init; }

        public Vector3 Scale { get; set; } = Vector3.One;

        public Vector3 HalfExtents { get; set; }

        public Vector3 Centre { get; set; }

        public float Mass { get; set; }

        public float Coefficient { get; set; }

        public float Damping { get; set; }

        public IReadOnlyList<BuoyancyPontoon> Pontoons { get; set; } = [];
    }
}
