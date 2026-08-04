// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Water;

/// <summary>Where the ground is, for a field that has to know what its water sits on.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The terrain is a first-class producer, not a component somebody remembers to
///         attach</b> —
///         [35 § D3](../../docs/plan/35-water.md#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render).
///         Unreal's <c>UWaterTerrainComponent</c> is opt-in per actor, which is why "my water has no
///         depth" is a common question there with a non-obvious answer. Here a zone asks whatever
///         ground it has, and a <em>mesh</em> contributes only if it says so explicitly — the
///         opposite default, because a terrain always wants to be the riverbed and a lamp post never
///         does.
///     </para>
///     <para>
///         An interface rather than a reference to <c>Vixen.Terrain</c>, because the kernel opens no
///         device and takes no dependency it does not need: a test's ground is a lambda, a server's
///         is a heightfield, and neither is the renderer's.
///     </para>
/// </remarks>
public interface IWaterGround {
    /// <summary>How high the ground is at a place, in world units.</summary>
    /// <param name="ground">Where, on the ground plane — world X and Z.</param>
    /// <returns>The height.</returns>
    float HeightAt(Vector2 ground);
}

/// <summary>Ground at one height everywhere.</summary>
/// <param name="Height">That height.</param>
/// <remarks>What a lake in a test sits on, and what an open ocean with no terrain under it sits on.</remarks>
public readonly record struct FlatWaterGround(float Height) : IWaterGround {
    /// <inheritdoc />
    public float HeightAt(Vector2 ground) => Height;
}

/// <summary>The shape of a field: a square window on the ground plane, at a resolution.</summary>
/// <remarks>
///     <para>
///         <b>Always a sliding window</b>
///         ([§ D3](../../docs/plan/35-water.md#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render)).
///         Unreal made local tessellation a mode in 5.3 because the fixed-extent version does not
///         survive an open world, and there is no reason to build the version that does not survive
///         first.
///     </para>
///     <para>
///         ⚠ <b>The origin is snapped, and the snap is to a texel of the <em>coarsest</em> thing that
///         reads the field.</b> Snapping to the window's own grid is not enough once a ripple
///         simulation samples it at a different resolution: the two grids beat against each other and
///         produce a crawl along the shoreline that appears only while the camera moves. That is the
///         same class of bug as the terrain quadtree's morph, and it gets the same treatment — a test
///         on the arithmetic, before there is a renderer to see it in. See <see cref="Snap" />.
///     </para>
/// </remarks>
public readonly record struct WaterFieldDescription {
    /// <summary>The window's low corner, on the ground plane.</summary>
    public Vector2 Origin { get; init; }

    /// <summary>How wide and deep the window is, in metres.</summary>
    public float Extent { get; init; }

    /// <summary>How many texels along each axis.</summary>
    public int Resolution { get; init; }

    /// <summary>A 256-metre window at one metre per texel.</summary>
    public static WaterFieldDescription Default =>
        new() { Origin = Vector2.Zero, Extent = 256f, Resolution = 256 };

    /// <summary>How many metres apart two neighbouring texels are.</summary>
    /// <remarks>
    ///     <para>
    ///         The number a zone panel shows instead of a resolution. A resolution is meaningless and a
    ///         metre per texel is not — an author who can see that a shoreline is being decided at four
    ///         metres a sample knows why it is soft.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Divided by <c>Resolution − 1</c>, because the samples include both edges.</b> The
    ///         window covers <c>[Origin, Origin + Extent]</c> inclusively, exactly as a terrain tile's
    ///         "power of two <em>plus one</em>" samples do and for the same reason — so a 512 m window
    ///         at 257 texels is two metres a texel and not 1.992.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It shipped as <c>Extent / Resolution</c> for a day and that was a real bug, not a
    ///         rounding preference.</b> The rasteriser and the sampler both used the inclusive spacing
    ///         while the panel and the <em>snap</em> used the other one, so a window snapped to a
    ///         four-metre grid was landing on a grid that was not a whole number of texels — and the
    ///         two beat against each other and produced exactly the shoreline crawl § D3 warns about.
    ///         Found by the stability sweep, which is why that sweep exists.
    ///     </para>
    /// </remarks>
    public float MetresPerTexel => Resolution > 1 ? Extent / (Resolution - 1) : 0f;

    /// <summary>How many bytes the four channels and the coverage occupy.</summary>
    public long Bytes => (long)Resolution * Resolution * 5 * sizeof(float);

    /// <summary>Why this shape cannot be rasterised, or <see langword="null" /> if it can.</summary>
    public string? Validate() {
        if (Resolution < 2) {
            return $"A field of {Resolution} texels has no interior to interpolate across.";
        }

        if (!(Extent > 0f)) {
            return "A field of no extent covers nothing.";
        }

        return null;
    }

    /// <summary>The window this one becomes when the view moves, snapped so it does not shimmer.</summary>
    /// <param name="centre">Where the view is, on the ground plane.</param>
    /// <param name="coarsestTexel">
    ///     How many metres a texel of the <em>coarsest</em> consumer covers. Zero or less snaps to
    ///     this field's own texel.
    /// </param>
    /// <returns>The window, centred on that place and snapped.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The whole point is the argument.</b> Snapping to this field's own texel keeps the
    ///         field itself stable and lets anything sampling it at a different rate beat against it —
    ///         so the caller states the coarsest grid in play and everything finer is a whole number
    ///         of steps inside it.
    ///     </para>
    ///     <para>
    ///         Floor rather than round, so that a window scrolling in one direction never steps
    ///         backwards: rounding changes direction at the midpoint, and a window that goes back one
    ///         texel and forward two is a shoreline that stutters rather than one that slides.
    ///     </para>
    /// </remarks>
    public WaterFieldDescription Snap(Vector2 centre, float coarsestTexel = 0f) {
        var step = coarsestTexel > 0f ? coarsestTexel : MetresPerTexel;

        if (!(step > 0f)) {
            return this with { Origin = centre - new Vector2(Extent * 0.5f) };
        }

        var low = centre - new Vector2(Extent * 0.5f);

        return this with {
            Origin = new(MathF.Floor(low.X / step) * step, MathF.Floor(low.Y / step) * step)
        };
    }
}

/// <summary>What a field says about one place.</summary>
/// <param name="Coverage">How much water is here at all, 0…1.</param>
/// <param name="SurfaceHeight">Where the surface is, in world units.</param>
/// <param name="Flow">How fast it moves, in metres per second, on the ground plane.</param>
/// <param name="GroundHeight">Where the ground under it is, in world units.</param>
/// <remarks>
///     ⚠ <b>Depth is <see cref="Depth" /> and is computed, never stored</b> —
///     [§ D3](../../docs/plan/35-water.md#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render).
///     Storing it would be a third number that can disagree with the two it came from, and the frame
///     it disagrees on is the one where the shoreline is in a different place for the material than
///     for the wave attenuation.
/// </remarks>
public readonly record struct WaterFieldSample(
    float Coverage,
    float SurfaceHeight,
    Vector2 Flow,
    float GroundHeight
) {
    /// <summary>How deep the water is, in metres. Never negative.</summary>
    public float Depth => MathF.Max(SurfaceHeight - GroundHeight, 0f);

    /// <summary>Nothing at all.</summary>
    public static WaterFieldSample None => default;
}

/// <summary>
///     Every body in a region, resolved once into a field over the ground plane.
/// </summary>
/// <remarks>
///     <para>
///         <b>The interchange</b>
///         ([§ D3](../../docs/plan/35-water.md#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render)).
///         Every consumer wants a <em>field</em> over the ground plane rather than a set of bodies:
///         the material wants depth for its colour and its shoreline, the vertex stage wants
///         attenuation, the foam wants the gradient, the ripple simulation wants a boundary, and none
///         of them wants to iterate bodies per pixel.
///     </para>
///     <para>
///         <b>Bodies are rasterised in priority order, which is what resolves a river mouth.</b> Two
///         flow models meet exactly once — where a river runs into a lake — and resolving it here,
///         once, is what lets a tile of the surface carry one material instead of a blend that has to
///         know which two bodies it is between.
///     </para>
///     <para>
///         <b>This is the CPU form.</b> W3 renders the same four channels top-down into a texture;
///         what makes the two agree is that both are this arithmetic. The fifth channel here —
///         coverage — is what the mesh's descent predicate tests, and how it is packed on the device
///         is W3's to decide.
///     </para>
/// </remarks>
public sealed class WaterField {
    readonly float[] surface;
    readonly float[] flowX;
    readonly float[] flowZ;
    readonly float[] ground;
    readonly float[] coverage;

    /// <summary>Creates an empty field of a shape.</summary>
    /// <param name="description">The shape.</param>
    /// <exception cref="ArgumentException">The shape is not one that can be rasterised.</exception>
    public WaterField(in WaterFieldDescription description) {
        if (description.Validate() is { } why) {
            throw new ArgumentException(why, nameof(description));
        }

        Description = description;

        var texels = description.Resolution * description.Resolution;

        surface = new float[texels];
        flowX = new float[texels];
        flowZ = new float[texels];
        ground = new float[texels];
        coverage = new float[texels];
    }

    /// <summary>The window it covers and the rate it covers it at.</summary>
    public WaterFieldDescription Description { get; private set; }

    /// <summary>How many texels any body claimed, after the last rasterisation.</summary>
    /// <remarks>
    ///     The diagnostic that answers "my lake is not there": a body outside the window claims
    ///     nothing, and so does one whose shoreline falloff is zero and whose spline is degenerate.
    /// </remarks>
    public int CoveredTexels { get; private set; }

    /// <summary>Moves the window without reallocating, and forgets what was in it.</summary>
    /// <param name="description">The new window, which must be the same resolution.</param>
    /// <exception cref="ArgumentException">The resolution changed.</exception>
    /// <remarks>
    ///     What a sliding window does when it has scrolled past its threshold. The contents are not
    ///     shifted: a body may have moved, the ground may have been carved, and copying the overlap
    ///     would carry the stale answer for both.
    /// </remarks>
    public void Move(in WaterFieldDescription description) {
        if (description.Resolution != Description.Resolution) {
            throw new ArgumentException(
                $"The field holds {Description.Resolution}² texels and cannot become "
                + $"{description.Resolution}². Moving a window keeps its memory; changing its "
                + "resolution is a new field.",
                nameof(description)
            );
        }

        if (description.Validate() is { } why) {
            throw new ArgumentException(why, nameof(description));
        }

        Description = description;
        Clear();
    }

    /// <summary>Forgets everything the field holds.</summary>
    public void Clear() {
        Array.Clear(surface);
        Array.Clear(flowX);
        Array.Clear(flowZ);
        Array.Clear(ground);
        Array.Clear(coverage);
        CoveredTexels = 0;
    }

    /// <summary>Rasterises every body and the ground beneath them.</summary>
    /// <param name="bodies">The bodies. Rasterised in priority order, lowest first.</param>
    /// <param name="beneath">Where the ground is.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The ground is filled first and unconditionally</b>, even where there is no water:
    ///         the field is what a shoreline is decided from, and a texel just outside a lake still
    ///         has to say how high the beach is or the falloff has nothing to fall to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The result does not depend on the order the bodies arrive in, and that is a
    ///         stated property rather than a happy accident</b> ([§ Part 4]). Priority decides which
    ///         body is on top; bodies <em>at one priority</em> are averaged by their coverage, which
    ///         is commutative. A field that depended on the order a scene happened to walk its
    ///         entities in is one where moving an unrelated entity changes the shoreline by a texel —
    ///         found months later as "the water flickers near the bridge".
    ///     </para>
    ///     <para>
    ///         <b>Composited highest priority first, as an <em>over</em>.</b> Each priority group
    ///         claims its coverage out of what is left, so a river at a higher priority than the lake
    ///         it runs into crossfades across its own shoreline band rather than cutting — and the
    ///         crossfade happens once, here, instead of per pixel in a material that would have to
    ///         know which two bodies it was between.
    ///     </para>
    ///     <para>
    ///         <b>An island subtracts.</b> It claims its share of the coverage and contributes no
    ///         water, and raises the ground it displaces — the sign-flipped form of everything above.
    ///     </para>
    /// </remarks>
    public void Rasterize(IReadOnlyList<WaterBody> bodies, IWaterGround beneath) {
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(beneath);

        Clear();

        // Highest priority first, because the composite is an *over*. The sort's own stability does
        // not matter and must not: what makes two orderings agree is that everything inside one
        // priority is combined commutatively.
        var ordered = new List<WaterBody>(bodies);
        ordered.Sort(static (left, right) => right.Priority.CompareTo(left.Priority));

        var resolution = Description.Resolution;
        var step = Description.Extent / (resolution - 1);

        for (var z = 0; z < resolution; z++) {
            for (var x = 0; x < resolution; x++) {
                var index = (z * resolution) + x;
                var position = Description.Origin + new Vector2(x * step, z * step);

                var bed = beneath.HeightAt(position);

                // ⚠ Starts below everything rather than at the ground, so that a place no island
                // touches is the bed and nothing else. Seeding it with the ground would mean carving
                // could never cut, because the maximum of the two would always be the ground.
                var raised = float.NegativeInfinity;

                float claimed = 0f, height = 0f, vx = 0f, vz = 0f;
                var remaining = 1f;

                for (var first = 0; first < ordered.Count && remaining > 0f;) {
                    var priority = ordered[first].Priority;
                    var last = first;

                    while (last < ordered.Count && ordered[last].Priority == priority) {
                        last++;
                    }

                    // One priority group, averaged by coverage — the commutative half.
                    float weight = 0f, groupHeight = 0f, groupX = 0f, groupZ = 0f, groupCoverage = 0f;
                    var subtracted = 0f;

                    for (var at = first; at < last; at++) {
                        var body = ordered[at];
                        var contribution = body.Sample(position);

                        if (contribution.Coverage <= 0f) {
                            continue;
                        }

                        if (body.IsSubtractive) {
                            subtracted = MathF.Max(subtracted, contribution.Coverage);
                            raised = MathF.Max(
                                raised,
                                contribution.SurfaceHeight + (contribution.BedDepth * contribution.Coverage)
                            );

                            continue;
                        }

                        weight += contribution.Coverage;
                        groupHeight += contribution.SurfaceHeight * contribution.Coverage;
                        groupX += contribution.Flow.X * contribution.Coverage;
                        groupZ += contribution.Flow.Y * contribution.Coverage;
                        groupCoverage = MathF.Max(groupCoverage, contribution.Coverage);

                        // The bed a body wants, only where it is deeper than what is already there —
                        // a river crossing a lake does not fill the lake in. Taken as a minimum, so
                        // two bodies in one group agree however they were ordered.
                        bed = MathF.Min(
                            bed,
                            contribution.SurfaceHeight - (contribution.BedDepth * contribution.Coverage)
                        );
                    }

                    if (weight > 0f) {
                        var take = groupCoverage * remaining;

                        height += groupHeight / weight * take;
                        vx += groupX / weight * take;
                        vz += groupZ / weight * take;
                        claimed += take;
                        remaining -= take;
                    }

                    if (subtracted > 0f) {
                        // An island takes its share and gives no water back, which is what makes it
                        // hide the bodies underneath rather than blend with them.
                        remaining -= subtracted * remaining;
                    }

                    first = last;
                }

                ground[index] = MathF.Max(bed, raised);

                if (claimed > 0f) {
                    // Normalised, so a place a single body half-covers still reads that body's own
                    // surface height rather than half of it plus half of nothing.
                    surface[index] = height / claimed;
                    flowX[index] = vx / claimed;
                    flowZ[index] = vz / claimed;
                    coverage[index] = WaterMath.Saturate(claimed);
                    CoveredTexels++;
                } else {
                    surface[index] = ground[index];
                }
            }
        }
    }

    /// <summary>What the field says about a place, interpolated between its texels.</summary>
    /// <param name="position">Where, on the ground plane.</param>
    /// <returns>The sample, which is <see cref="WaterFieldSample.None" /> outside the window.</returns>
    /// <remarks>
    ///     <para>
    ///         Bilinear, clamped at the edges. ⚠ <b>Clamped rather than answering nothing</b>, because
    ///         a query one texel outside a window that is about to scroll should read the shoreline
    ///         it is next to rather than dry land — a boat at the edge of the window would otherwise
    ///         drop through the surface for the frame before the window caught up.
    ///     </para>
    ///     <para>
    ///         Outside the window <em>entirely</em> it does answer nothing, which is honest: a field
    ///         is a window and what is beyond it is not something the field knows.
    ///     </para>
    /// </remarks>
    public WaterFieldSample Sample(Vector2 position) {
        var resolution = Description.Resolution;
        var step = Description.Extent / (resolution - 1);
        var local = (position - Description.Origin) / step;

        if (local.X < -1f || local.Y < -1f || local.X > resolution || local.Y > resolution) {
            return WaterFieldSample.None;
        }

        var x0 = Math.Clamp((int)MathF.Floor(local.X), 0, resolution - 1);
        var z0 = Math.Clamp((int)MathF.Floor(local.Y), 0, resolution - 1);
        var x1 = Math.Min(x0 + 1, resolution - 1);
        var z1 = Math.Min(z0 + 1, resolution - 1);

        var fx = WaterMath.Saturate(local.X - x0);
        var fz = WaterMath.Saturate(local.Y - z0);

        var a = (z0 * resolution) + x0;
        var b = (z0 * resolution) + x1;
        var c = (z1 * resolution) + x0;
        var d = (z1 * resolution) + x1;

        return new(
            Blend(coverage, a, b, c, d, fx, fz),
            Blend(surface, a, b, c, d, fx, fz),
            new(Blend(flowX, a, b, c, d, fx, fz), Blend(flowZ, a, b, c, d, fx, fz)),
            Blend(ground, a, b, c, d, fx, fz)
        );
    }

    /// <summary>What the field holds at one texel, unfiltered.</summary>
    /// <param name="x">Its X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <returns>The sample.</returns>
    /// <remarks>What an upload walks and what a test asserts against a hand-computed expectation.</remarks>
    public WaterFieldSample At(int x, int z) {
        if ((uint)x >= (uint)Description.Resolution || (uint)z >= (uint)Description.Resolution) {
            return WaterFieldSample.None;
        }

        var index = (z * Description.Resolution) + x;

        return new(coverage[index], surface[index], new(flowX[index], flowZ[index]), ground[index]);
    }

    static float Blend(float[] channel, int a, int b, int c, int d, float fx, float fz) {
        var top = channel[a] + ((channel[b] - channel[a]) * fx);
        var bottom = channel[c] + ((channel[d] - channel[c]) * fx);

        return top + ((bottom - top) * fz);
    }
}
