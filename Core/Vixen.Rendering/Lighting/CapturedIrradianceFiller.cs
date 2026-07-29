// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Rendering.IrradianceFields;

namespace Vixen.Rendering.Lighting;

/// <summary>What a capture saw from where a probe stands.</summary>
/// <param name="Radiance">The radiance arriving from every direction.</param>
/// <param name="Validity">How much of it to believe, from zero to one.</param>
/// <param name="SunShadow">How much of the directional light reaches, from zero to one.</param>
/// <remarks>
///     <para>
///         <b>Three things, because only the first is a picture.</b> A cube of radiance says nothing
///         about whether the probe is inside a wall — a probe buried in geometry captures the inside
///         of that geometry and comes back plausibly dark rather than obviously wrong. Whoever
///         rendered the cube has the depth and the normals to answer it, and this filler does not, so
///         the answer travels with the capture.
///     </para>
///     <para>
///         The same split <see cref="TracedIrradianceFiller" /> makes between an <c>IDistanceField</c>
///         and an <c>IRadianceSource</c>: what a probe <i>sees</i> and whether it should be believed
///         are different questions with different owners.
///     </para>
/// </remarks>
public readonly record struct IrradianceCapture(CubeImage Radiance, float Validity, float SunShadow);

/// <summary>Where a cube capture comes from.</summary>
/// <remarks>
///     <para>
///         Rendering a cube per probe needs a scene, a pipeline and a device; projecting one into four
///         coefficients needs none of those. This is the line between them, and it is what lets the
///         projection be checked against arithmetic and against
///         <see cref="TracedIrradianceFiller" /> with nothing rendered at all.
///     </para>
///     <para>
///         <b>It is also where the bounce lives.</b> Doc 19 § L2 describes filler B as two or three
///         passes feeding the previous result back as ambient — which is a property of what the
///         capture renders with, not of how a cube becomes coefficients. A source that shades with the
///         field's current answer produces a second bounce by being called twice.
///     </para>
/// </remarks>
public interface IIrradianceCaptureSource {
    /// <summary>Captures what a probe at a position sees.</summary>
    /// <param name="position">Where the probe stands.</param>
    /// <param name="capture">What it saw.</param>
    /// <returns>False where nothing could be captured, which leaves the probe alone.</returns>
    /// <remarks>
    ///     False rather than an empty capture, because the two mean different things: an empty capture
    ///     is a probe that saw darkness and a failure is a probe nobody asked about. Writing the first
    ///     over a probe that was already filled would darken a field one brick at a time.
    /// </remarks>
    bool TryCapture(Vector3 position, out IrradianceCapture capture);
}

/// <summary>Fills probes by capturing a cube at each one — doc 19 § L2's filler B.</summary>
/// <remarks>
///     <para>
///         <b>The filler for a target with no compute, and the reason § 3 is written the way it is.</b>
///         A brick is a brick whether a compute shader traced rays into it this frame or this captured
///         a cube at build time; nothing above the storage branches on which ran. That is what lets
///         doc 19 § 7 promise WebGL2 the same lighting model as a desktop at a different update rate,
///         and this is the half that makes the promise true rather than stated.
///     </para>
///     <para>
///         <b>It is not a lightmapper.</b> No UV unwrap, no chart packing, no seam fixing, no atlas —
///         the output is the same brick pool the runtime filler writes, addressed the same way. Doc 19
///         § 4 retires the whole lightmap toolchain on exactly this basis, and it only holds because
///         the two fillers share a destination.
///     </para>
///     <para>
///         <b>The projection is the same integral <see cref="TracedIrradianceFiller" /> does</b> — a
///         sum of radiance against the basis, weighted by solid angle — with the samples arriving as
///         cube texels instead of as rays. So the two agree wherever the quadrature does, which is the
///         exit criterion doc 19 § L2 states for this pair and which
///         <c>CapturedIrradianceFillerTests</c> asserts against a directional sky rather than a
///         uniform one: a uniform environment agrees for the trivial reason that both integrate a
///         constant.
///     </para>
/// </remarks>
public sealed class CapturedIrradianceFiller {
    readonly IIrradianceCaptureSource source;

    IrradianceBrickCursor cursor;
    IrradianceBrick[] taken = [];

    /// <summary>Builds a filler over a source of captures.</summary>
    /// <param name="source">Where the cubes come from.</param>
    /// <exception cref="ArgumentNullException">There is no source.</exception>
    public CapturedIrradianceFiller(IIrradianceCaptureSource source) {
        ArgumentNullException.ThrowIfNull(source);

        this.source = source;
    }

    /// <summary>How much of the previous answer survives a fill, from zero to one.</summary>
    /// <remarks>
    ///     <para>
    ///         Zero by default and zero is what a bake wants: a capture is not a noisy estimate the way
    ///         sixty-four rays are, so there is nothing to average away and a blend would only make the
    ///         answer depend on how many times the bake had run.
    ///     </para>
    ///     <para>
    ///         It exists at all because the bounce iteration reuses this filler — a second pass whose
    ///         captures were shaded with the first pass's answer is a genuinely different capture, and
    ///         a project that wants those blended rather than replaced can say so.
    ///     </para>
    /// </remarks>
    public float Hysteresis { get; init; }

    /// <summary>Which indirection cell the next budgeted fill starts at.</summary>
    public int Cursor => cursor.Position;

    /// <summary>How many probes the last fill could not capture.</summary>
    /// <remarks>
    ///     <b>A bake that silently skipped half a field is a field with a dark half.</b> A source
    ///     refusing a position is legitimate — it may be outside the scene, or past a budget — but a
    ///     count nobody can see turns "the capture failed here" into "the lighting is wrong here",
    ///     and those are answered in different files.
    /// </remarks>
    public int Skipped { get; private set; }

    /// <summary>Fills every brick of a field.</summary>
    /// <param name="field">The field to fill.</param>
    /// <returns>How many bricks were filled.</returns>
    /// <exception cref="ArgumentNullException">There is no field.</exception>
    /// <remarks>
    ///     Borders are not touched and are not filled — run <see cref="IrradianceField.Dilate" /> and
    ///     then <see cref="IrradianceField.SyncBorders" /> afterwards, in that order.
    /// </remarks>
    public int Fill(IrradianceField field) {
        ArgumentNullException.ThrowIfNull(field);

        return Fill(field, field.BrickCount);
    }

    /// <summary>Fills a bounded number of bricks, carrying on from where the last call stopped.</summary>
    /// <param name="field">The field to fill.</param>
    /// <param name="budget">How many bricks to fill.</param>
    /// <returns>How many were filled.</returns>
    /// <exception cref="ArgumentNullException">There is no field.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A negative budget.</exception>
    /// <remarks>
    ///     Budgeted like the traced filler and through the same
    ///     <see cref="IrradianceBrickCursor" />, which is not only symmetry: a bake of a large field is
    ///     minutes of rendering, and a build step that can be stopped and resumed a brick at a time is
    ///     one that can report progress.
    /// </remarks>
    public int Fill(IrradianceField field, int budget) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentOutOfRangeException.ThrowIfNegative(budget);

        Skipped = 0;

        if (taken.Length < budget) {
            taken = new IrradianceBrick[budget];
        }

        var count = cursor.Take(field, taken.AsSpan(0, budget));

        for (var index = 0; index < count; index++) {
            var brick = taken[index];

            for (var z = 0; z < IrradianceBrickPool.BrickResolution; z++) {
                for (var y = 0; y < IrradianceBrickPool.BrickResolution; y++) {
                    for (var x = 0; x < IrradianceBrickPool.BrickResolution; x++) {
                        if (!source.TryCapture(field.ProbePosition(brick, x, y, z), out var capture)) {
                            Skipped++;

                            continue;
                        }

                        field.SetProbe(brick, x, y, z, Project(capture, field.GetProbe(brick, x, y, z)));
                    }
                }
            }
        }

        return count;
    }

    /// <summary>One capture as the probe it describes.</summary>
    /// <param name="capture">What was seen.</param>
    /// <param name="previous">What the probe held, which the hysteresis blends against.</param>
    /// <returns>The probe.</returns>
    /// <exception cref="ArgumentNullException">The capture has no radiance.</exception>
    /// <remarks>
    ///     <para>
    ///         Exposed for the same reason <see cref="TracedIrradianceFiller.Trace" /> is: this is the
    ///         whole of the arithmetic, and the tests that matter are about one cube in a known
    ///         environment rather than about a field's addressing.
    ///     </para>
    ///     <para>
    ///         <b>Every texel, weighted by its own solid angle.</b> A cube's texels do not subtend equal
    ///         angles — a corner one covers about a fifth of what a face's centre one does — so summing
    ///         them evenly is an average rather than an integral.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And for an L1 payload alone, cube symmetry would rescue the average anyway</b>,
    ///         which is worth knowing rather than discovering later. Uniform weights sum to 4π by
    ///         construction so the constant band comes out right, and Σ(d·ŷ)² over a cube is a third of
    ///         the texel count by the symmetry that makes Σd·d equal it, so the linear band does too. A
    ///         smooth sky, a linear sky and a face-uniform sky are all blind to the difference; only
    ///         content that varies <i>within</i> a face is not.
    ///     </para>
    ///     <para>
    ///         The weighting stays because it is right, and because the symmetry rescuing it is a
    ///         property of this payload rather than of the projection — an L2 band would have no such
    ///         luck. <c>OneLitTexelIsWorthItsOwnSolidAngle</c> is the test that can tell, and it exists
    ///         because every other one here passed with the weights thrown away.
    ///     </para>
    /// </remarks>
    public IrradianceProbe Project(in IrradianceCapture capture, IrradianceProbe previous) {
        ArgumentNullException.ThrowIfNull(capture.Radiance);

        var cube = capture.Radiance;
        var radiance = SphericalHarmonicsL1.Zero;

        for (var face = 0; face < 6; face++) {
            for (var y = 0; y < cube.Size; y++) {
                for (var x = 0; x < cube.Size; x++) {
                    radiance = radiance.Accumulated(
                        cube.DirectionOf((CubeFace)face, x, y),
                        cube.At((CubeFace)face, x, y),
                        cube.SolidAngleOf(x, y)
                    );
                }
            }
        }

        var fresh = new IrradianceProbe(radiance, capture.Validity, capture.SunShadow);

        return Hysteresis > 0f ? IrradianceProbe.Lerp(previous, fresh, 1f - Hysteresis) : fresh;
    }
}
