// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>
///     The renderer that submits lights instead of geometry — doc 06 § VFX pipeline.
/// </summary>
/// <remarks>
///     What is worth checking here is the mapping rather than the loop: which particle attribute
///     becomes which field of a light. Getting that wrong produces an effect that lights the scene
///     with something plausible and wrong — a spark whose pool of light does not fade with it, or one
///     whose reach does not shrink — and neither shows up as a failure anywhere else.
/// </remarks>
public class ParticleLightTests {
    const int Count = 8;

    /// <summary>A system whose particles are lights, warmed to its full population.</summary>
    /// <remarks>
    ///     Stepped twice, because <see cref="VfxSystem.Step" /> updates before it spawns: one step
    ///     leaves the burst's particles freshly initialized and never updated, which is a state no
    ///     frame ever sees and a poor thing to assert against.
    /// </remarks>
    static VfxSystem Lights(float intensity = 2f, float range = 3f, float alpha = 1f, float size = 0.5f) {
        var system = new VfxSystem(
            VfxCompiledGraph.Compile(
                [VfxSpawner.Burst(Count)],
                [
                    new(VfxOpcode.SetPosition, new Vector4(1f, 2f, 3f, 0f)),
                    new(VfxOpcode.SetSize, new Vector4(size, size, 0f, 0f)),
                    new(VfxOpcode.SetColour, new Vector4(0.25f, 0.5f, 0.75f, alpha)),
                    new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
                ],
                [],
                Count,
                VfxRenderer.Light(intensity, range)
            )
        );

        system.Step(1f / 60f);
        system.Step(1f / 60f);

        return system;
    }

    [Fact]
    public void Every_particle_becomes_one_light_where_it_is() {
        using var system = Lights();
        List<RenderLight> lights = [];

        Assert.Equal(0, ParticleLights.Collect(system, lights));
        Assert.Equal(Count, lights.Count);

        Assert.All(lights, light => {
            Assert.Equal(LightKind.Point, light.Kind);
            Assert.Equal(new Vector3(1f, 2f, 3f), light.Position);
            Assert.Equal(0.25f, light.Colour.R);
            Assert.Equal(0.5f, light.Colour.G);
            Assert.Equal(0.75f, light.Colour.B);
        });
    }

    /// <summary>Alpha dims the light and size shortens its reach — the two curves an author writes.</summary>
    [Fact]
    public void A_faded_particle_casts_a_dimmer_and_shorter_light() {
        using var full = Lights(intensity: 2f, range: 3f, alpha: 1f, size: 0.5f);
        using var faded = Lights(intensity: 2f, range: 3f, alpha: 0.25f, size: 0.1f);

        List<RenderLight> bright = [];
        List<RenderLight> dim = [];

        ParticleLights.Collect(full, bright);
        ParticleLights.Collect(faded, dim);

        Assert.Equal(2f, bright[0].Intensity);
        Assert.Equal(1.5f, bright[0].Range);

        Assert.Equal(0.5f, dim[0].Intensity);
        Assert.Equal(0.3f, dim[0].Range, 6);
    }

    /// <summary>The budget is a cap and a report, not a promise.</summary>
    [Fact]
    public void What_does_not_fit_in_the_budget_is_reported() {
        using var system = Lights();
        List<RenderLight> lights = [];

        Assert.Equal(Count - 3, ParticleLights.Collect(system, lights, 3));
        Assert.Equal(3, lights.Count);
    }

    /// <summary>A budget of zero is a system switched off rather than an argument to object to.</summary>
    [Fact]
    public void A_budget_of_none_takes_none() {
        using var system = Lights();
        List<RenderLight> lights = [];

        Assert.Equal(Count, ParticleLights.Collect(system, lights, 0));
        Assert.Empty(lights);
    }

    [Fact]
    public void A_system_drawn_as_billboards_contributes_no_lights() {
        using var system = new VfxSystem(
            VfxCompiledGraph.Compile(
                [VfxSpawner.Burst(Count)],
                [new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))],
                [],
                Count,
                VfxRenderer.Billboard
            )
        );

        system.Step(1f / 60f);
        system.Step(1f / 60f);

        List<RenderLight> lights = [];

        Assert.Equal(0, ParticleLights.Collect(system, lights));
        Assert.Empty(lights);
    }

    /// <summary>A graph with no renderer is a simulation feeding something else, and draws nothing.</summary>
    [Fact]
    public void A_system_with_no_renderer_contributes_no_lights() {
        using var system = new VfxSystem(
            VfxCompiledGraph.Compile(
                [VfxSpawner.Burst(Count)],
                [new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))],
                [],
                Count
            )
        );

        system.Step(1f / 60f);
        system.Step(1f / 60f);

        List<RenderLight> lights = [];

        Assert.Equal(0, ParticleLights.Collect(system, lights));
        Assert.Empty(lights);
    }
}
