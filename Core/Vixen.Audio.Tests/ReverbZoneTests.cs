// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Mixing;
using Vixen.Audio.Spatial;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>Zones, which are arithmetic and deliberately need no physics to be one.</summary>
public sealed class ReverbZoneTests {
    static AudioReverbZone Sphere(string parameter, Vector3 at, float radius, float blend = 0f, int priority = 0) =>
        new() {
            Parameter = parameter,
            Position = at,
            Shape = AudioZoneShape.Sphere,
            Extent = new Vector3(radius, radius, radius),
            Blend = blend,
            Priority = priority
        };

    [Fact]
    public void ASphereContainsWhatIsInsideIt() {
        var zone = Sphere("cave", Vector3.Zero, 10f);

        Assert.Equal(1f, zone.Evaluate(Vector3.Zero));
        Assert.Equal(1f, zone.Evaluate(new Vector3(9.9f, 0f, 0f)));
        Assert.Equal(0f, zone.Evaluate(new Vector3(10.1f, 0f, 0f)));
        Assert.Equal(0f, zone.Evaluate(new Vector3(0f, 0f, 100f)));
    }

    [Fact]
    public void ABoxIsABoxAndNotASphereWithCorners() {
        var zone = new AudioReverbZone {
            Parameter = "corridor",
            Shape = AudioZoneShape.Box,
            Extent = new Vector3(10f, 2f, 3f)
        };

        Assert.Equal(1f, zone.Evaluate(new Vector3(9f, 1f, 2f)));

        // Inside on two axes and outside on the third is outside — which is the whole reason a
        // corridor is a box: a sphere that reached the far end would also reach through the ceiling.
        Assert.Equal(0f, zone.Evaluate(new Vector3(9f, 3f, 2f)));
        Assert.Equal(0f, zone.Evaluate(new Vector3(11f, 1f, 2f)));
    }

    /// <summary>A boundary without a blend is a switch, and nobody walks like that.</summary>
    [Fact]
    public void TheEdgeFadesRatherThanSwitching() {
        var zone = Sphere("hall", Vector3.Zero, 10f, blend: 4f);

        Assert.Equal(0f, zone.Evaluate(new Vector3(10f, 0f, 0f)));
        Assert.Equal(0.25f, zone.Evaluate(new Vector3(9f, 0f, 0f)), 1e-4f);
        Assert.Equal(0.5f, zone.Evaluate(new Vector3(8f, 0f, 0f)), 1e-4f);
        Assert.Equal(1f, zone.Evaluate(new Vector3(6f, 0f, 0f)), 1e-4f);
        Assert.Equal(1f, zone.Evaluate(Vector3.Zero));
    }

    [Fact]
    public void StrengthCapsIt() {
        var zone = Sphere("damp", Vector3.Zero, 10f) with { Strength = 0.3f };

        Assert.Equal(0.3f, zone.Evaluate(Vector3.Zero));
        Assert.Equal(0f, zone.Evaluate(new Vector3(20f, 0f, 0f)));
    }

    // ── The set ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A cupboard inside a cathedral is inside both. Blending them gives a room that is neither,
    ///     so the more specific one wins outright.
    /// </summary>
    [Fact]
    public void TheMoreSpecificZoneWinsRatherThanBlending() {
        var zones = new AudioReverbZones();
        zones.Add(Sphere("reverb", Vector3.Zero, 100f, priority: 0) with { Strength = 1f });
        zones.Add(Sphere("reverb", Vector3.Zero, 5f, priority: 10) with { Strength = 0.2f });

        // Inside both: the cupboard takes it, even though it is the weaker of the two.
        zones.Apply(Vector3.Zero, null);
        Assert.Equal(0.2f, zones.StrengthOf("reverb"), 1e-4f);

        // Inside only the cathedral.
        zones.Apply(new Vector3(50f, 0f, 0f), null);
        Assert.Equal(1f, zones.StrengthOf("reverb"), 1e-4f);
    }

    /// <summary>The bug the whole shape exists to prevent: the room following the player out of it.</summary>
    [Fact]
    public void LeavingEveryZoneDrivesTheParameterBackToZero() {
        var zones = new AudioReverbZones();
        zones.Add(Sphere("cave", Vector3.Zero, 10f));

        zones.Apply(Vector3.Zero, null);
        Assert.Equal(1f, zones.StrengthOf("cave"));

        zones.Apply(new Vector3(1_000f, 0f, 0f), null);
        Assert.Equal(0f, zones.StrengthOf("cave"));
    }

    [Fact]
    public void ZonesDrivingDifferentParametersDoNotInterfere() {
        var zones = new AudioReverbZones();
        zones.Add(Sphere("cave", Vector3.Zero, 10f));
        zones.Add(Sphere("underwater", new Vector3(100f, 0f, 0f), 10f));

        zones.Apply(Vector3.Zero, null);

        Assert.Equal(1f, zones.StrengthOf("cave"));
        Assert.Equal(0f, zones.StrengthOf("underwater"));
    }

    /// <summary>Removing a zone has to release its parameter, not abandon it wherever it was.</summary>
    [Fact]
    public void ARemovedZoneReleasesWhatItWasDriving() {
        var zones = new AudioReverbZones();
        var cave = Sphere("cave", Vector3.Zero, 10f);
        zones.Add(cave);

        zones.Apply(Vector3.Zero, null);
        Assert.Equal(1f, zones.StrengthOf("cave"));

        Assert.True(zones.Remove(cave));
        zones.Apply(Vector3.Zero, null);

        Assert.Equal(0f, zones.StrengthOf("cave"));
    }

    /// <summary>
    ///     Reverb is the room you are standing in, not the room the sound is in. A gunshot fired
    ///     outside a cathedral and heard from inside it gets the cathedral.
    /// </summary>
    [Fact]
    public void ItIsTheListenerThatDecidesAndNotTheSource() {
        var (engine, _) = AudioTestData.Engine();

        using (engine) {
            engine.ReverbZones.Add(Sphere("cave", Vector3.Zero, 10f));

            // The listener inside the zone, the sound a long way outside it.
            engine.SetListener(new AudioListener { Position = Vector3.Zero });

            engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings {
                IsSpatial = true,
                Spatial = new SpatialSettings { Position = new Vector3(500f, 0f, 0f), MaxDistance = 10_000f }
            });

            engine.Update(1f / 60f);
            Assert.Equal(1f, engine.ReverbZones.StrengthOf("cave"));

            // And the other way round: listener out, source in.
            engine.SetListener(new AudioListener { Position = new Vector3(500f, 0f, 0f) });
            engine.Update(1f / 60f);

            Assert.Equal(0f, engine.ReverbZones.StrengthOf("cave"));
        }
    }

    [Fact]
    public void AnEngineWithNoZonesDoesNothing() {
        var (engine, _) = AudioTestData.Engine();

        using (engine) {
            engine.Update(1f / 60f);
            Assert.Equal(0, engine.ReverbZones.Count);
            Assert.Equal(0f, engine.ReverbZones.StrengthOf("anything"));
        }
    }
}
