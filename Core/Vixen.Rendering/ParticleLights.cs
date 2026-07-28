// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Vfx;

namespace Vixen.Rendering;

/// <summary>
///     A particle system's particles, as lights for the lighting pass.
/// </summary>
/// <remarks>
///     <para>
///         <b>The renderer that submits nothing to draw.</b> The other three turn particles into
///         geometry; this one turns them into <see cref="RenderLight" />s and hands them to whatever
///         is collecting lights this frame — <c>ForwardLightingRenderFeature.Lights</c>, in
///         the pipeline as it stands. A shower of sparks that lights the wall behind it is the case,
///         and it is the one thing an additive quad cannot fake: the quad brightens the sparks, not
///         the wall.
///     </para>
///     <para>
///         <b>Here rather than in <c>Vixen.Vfx</c> because <see cref="RenderLight" /> is here.</b> The
///         particle runtime knows what a light-emitting particle <i>is</i> — that is
///         <see cref="VfxRendererKind.Light" /> and the two numbers beside it — and knows nothing
///         about how this renderer represents one. The same split as <c>VfxGpuSimulation</c>: the
///         decision stays in the runtime and the translation lives where the type does.
///     </para>
///     <para>
///         <b>A budget, not a promise.</b> A light costs every fragment it reaches in every pass that
///         shades one, so a thousand of them is not an effect but an outage. The caller says how many
///         it will accept and gets that many, in buffer order, with the rest reported rather than
///         logged — the same shape as <see cref="VfxSystem.LastRefused" />, and for the same reason:
///         a system at its budget is normal for a deliberate effect and a mistake for an accidental
///         one, and only the author can tell which.
///     </para>
/// </remarks>
public static class ParticleLights {
    /// <summary>Appends a system's particles to a light list.</summary>
    /// <param name="system">The system. Its renderer decides whether it contributes anything.</param>
    /// <param name="lights">Where to put them.</param>
    /// <param name="maximum">The most to append.</param>
    /// <returns>How many were left out because <paramref name="maximum" /> was reached.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="system" /> or <paramref name="lights" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximum" /> is negative.</exception>
    /// <remarks>
    ///     A system whose renderer is not a <see cref="VfxRendererKind.Light" /> one contributes
    ///     nothing and is not an error: a frame that walks every system and calls this is the shape
    ///     the caller wants, and making it ask first would only move the test.
    /// </remarks>
    public static int Collect(VfxSystem system, IList<RenderLight> lights, int maximum = int.MaxValue) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(lights);
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);

        // A graph with no renderer at all is one nothing draws — a simulation feeding something else
        // — and it contributes no lights for the same reason it contributes no quads.
        if (system.Graph.Renderer is not { Kind: VfxRendererKind.Light } renderer) {
            return 0;
        }

        var particles = system.Particles;
        var count = Math.Min(particles.Count, maximum);

        var positions = particles.Position;
        var colours = particles.Colour;
        var sizes = particles.Size;

        for (var index = 0; index < count; index++) {
            var colour = colours[index];

            lights.Add(new() {
                Kind = LightKind.Point,
                Position = positions[index],
                Colour = new(colour.X, colour.Y, colour.Z),

                // Alpha is the fade, and a colour-over-life curve is the usual way an author writes
                // one. Multiplying it in here is what makes a dying spark dim its own pool of light
                // rather than switch it off at the moment it is reaped.
                Intensity = renderer.Intensity * colour.W,

                // Range from size for the same reason: the two curves an author already writes are
                // the two an author expects the light to follow.
                Range = renderer.Range * sizes[index]
            });
        }

        return particles.Count - count;
    }
}
