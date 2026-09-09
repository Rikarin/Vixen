// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Geometry.Remeshing;

namespace Vixen.Editor.Blockout;

/// <summary>docs/plan/41 § D1's seven artefacts, on screen.</summary>
/// <remarks>
///     <para>
///         <b>§ D1's argument for making every stage an artefact is that a remesher is judged by a
///         picture, and until this the artefacts were there and the picture was not</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/413">#413</a>). <c>RemeshDump</c> was
///         referenced by its own tests, by a <c>RunawayGuard</c> comment and by nothing under
///         <c>Editor/</c>; the measurements in that document were obtained by parsing written
///         <c>.obj</c> files, which is the workaround for not having this.
///     </para>
///     <para>
///         ⚠ <b>Captured on demand and never per frame.</b> <c>RemeshDump.Capture</c> re-runs stages ①
///         to ⑤ and costs what a remesh costs — seconds — which is exactly why it is legitimate: § D14
///         makes the pipeline deterministic, so re-running gives the artefacts the remesh had. A hook
///         threaded through <c>Remesher</c> to capture them in flight would put a debugging concern in
///         the middle of a pipeline every caller pays for.
///     </para>
///     <para>
///         ⚠ <b>The capture remembers <i>which entity</i> it is of.</b> Every artefact is indexed
///         against the conditioned mesh of one particular solid, so drawing it over a different
///         selection would be a field, a patch partition and a singularity set laid over geometry they
///         say nothing about — which reads as the remesher having produced nonsense.
///     </para>
///     <para>
///         ⚠ <b>Colour is chosen here rather than in <c>Core/</c>, which is what
///         <see cref="RemeshRegion" /> asks for in as many words:</b> "a patch index rather than a
///         colour … the index is the artefact; colouring it is one modulus away". This is the modulus.
///     </para>
/// </remarks>
public sealed class BlockoutRemeshDebug {
    /// <summary>How long a singularity's cross arms are, as a fraction of the model's diagonal.</summary>
    /// <remarks>
    ///     ⚠ <b>A fraction and never a length</b>, for <see cref="RemeshDump.CrossArm" />'s reason: a
    ///     fixed number is a claim about how big a model is, and a millimetre-wide part and a
    ///     kilometre-wide one have to draw the same picture.
    /// </remarks>
    public const float SingularityArm = 0.01f;

    /// <summary>The patch palette: hues of one lightness, so only "which patch" varies.</summary>
    /// <remarks>
    ///     ⚠ <b>Eight rather than a colour per patch, and the wrap is honest.</b> A layout has hundreds
    ///     of patches and no palette distinguishes hundreds of colours; what a reader is asking of this
    ///     drawing is "where does this patch end", which a wrap answers everywhere except between two
    ///     neighbours eight apart — and <c>ProfilerTheme</c>'s flame hues make the same trade for the
    ///     same reason. A chart whose colours varied in brightness would read as though the bright ones
    ///     mattered, so only the hue moves.
    /// </remarks>
    static readonly Color4[] Patches = [
        new(0.90f, 0.35f, 0.35f, 0.85f), new(0.90f, 0.62f, 0.30f, 0.85f),
        new(0.85f, 0.85f, 0.32f, 0.85f), new(0.45f, 0.85f, 0.40f, 0.85f),
        new(0.35f, 0.85f, 0.78f, 0.85f), new(0.38f, 0.62f, 0.92f, 0.85f),
        new(0.60f, 0.45f, 0.92f, 0.85f), new(0.90f, 0.45f, 0.78f, 0.85f)
    ];

    RemeshDump? dump;

    /// <summary>Stage ① — the conditioned triangles, as a wireframe.</summary>
    [Inspector]
    [Tooltip("The triangles conditioning produced, as a wireframe. Dense; usually the last one you turn on.")]
    public bool ShowConditioned { get; set; }

    /// <summary>Stage ② — the feature chains.</summary>
    [Inspector]
    [Tooltip("Every feature polyline the detector found: the edges the remesh is required to reproduce.")]
    public bool ShowFeatures { get; set; } = true;

    /// <summary>Stage ③ — the 4-RoSy field, as a cross per vertex.</summary>
    [Inspector]
    [Tooltip("The direction field, as a cross per vertex. A cross and not an arrow: a 4-RoSy field has no sign.")]
    public bool ShowField { get; set; }

    /// <summary>Stage ③ — where the field's singularities landed.</summary>
    [Inspector]
    [Tooltip("Where the field's singularities landed, as a cross at each one.")]
    public bool ShowSingularities { get; set; } = true;

    /// <summary>Stage ④ — the patch partition, as coloured regions.</summary>
    [Inspector]
    [Tooltip("The patch decomposition, one colour per patch. Grey is a triangle the partition claimed for nothing.")]
    public bool ShowPatches { get; set; } = true;

    /// <summary>Stage ⑤ — the quantized arcs, coloured by how far they moved.</summary>
    [Inspector]
    [Tooltip("Each arc of the layout, red where the integer it got is far from the density field's request.")]
    public bool ShowQuantization { get; set; }

    /// <summary>What the capture is of, or <see cref="Entity.Null" /> when there is none.</summary>
    public Entity Target { get; private set; }

    /// <summary>The artefacts, or <see langword="null" /> when nothing has been captured.</summary>
    public RemeshDump? Dump => dump;

    /// <summary>What the stages complained about, in the order they complained.</summary>
    public IReadOnlyList<string> Warnings => dump?.Warnings ?? [];

    /// <summary>Whether anything would be drawn: a capture, and at least one stage turned on.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because they fail differently and look the same.</b> A capture with every
    ///     stage off and no capture at all both draw nothing; the first is a switch somebody turned off
    ///     and the second is a verb that has not run, and a panel that could not tell them apart would
    ///     report the wrong one.
    /// </remarks>
    public bool IsVisible =>
        dump is not null
        && (ShowConditioned || ShowFeatures || ShowField || ShowSingularities || ShowPatches || ShowQuantization);

    /// <summary>Captures the artefacts for the first selected solid.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="settings">The same settings the verb would run with.</param>
    /// <returns>Whether anything was captured.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> or <paramref name="settings" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>One solid and not the whole selection.</b> Five overlaid cross fields is not five
    ///     answers, it is a picture nobody can read — and the question this exists to answer is
    ///     "which stage went wrong on <i>this</i> mesh".
    /// </remarks>
    public bool Capture(SceneDocument document, RemeshSettings settings) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(settings);

        foreach (var entity in document.Selection.Items) {
            if (document.MeshOf(entity) is not { IsEmpty: false } mesh) {
                continue;
            }

            dump = RemeshDump.Capture(mesh, settings);
            Target = entity;

            return true;
        }

        Clear();

        return false;
    }

    /// <summary>Throws the capture away.</summary>
    public void Clear() {
        dump = null;
        Target = Entity.Null;
    }

    /// <summary>Draws whichever stages are turned on.</summary>
    /// <param name="draw">Where the lines go.</param>
    /// <param name="placement">The entity's world matrix — the artefacts are in its mesh's own space.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draw" /> is null.</exception>
    public void Draw(GizmoDraw draw, in Matrix4x4 placement) {
        ArgumentNullException.ThrowIfNull(draw);

        if (dump is not { } artefacts) {
            return;
        }

        if (ShowConditioned) {
            Wireframe(draw, artefacts, placement);
        }

        if (ShowPatches) {
            Regions(draw, artefacts, placement);
        }

        // ⚠ After the patches, because a feature chain drawn under a patch fill is a feature chain
        // nobody can see — and the whole question these two answer together is "which patch spans
        // which crease", which docs/plan/41's two open technical rows are both diagnosed by eye.
        if (ShowFeatures) {
            foreach (var segment in artefacts.Features) {
                Segment(draw, segment, placement, FeatureColour);
            }
        }

        if (ShowField) {
            foreach (var segment in artefacts.Field) {
                Segment(draw, segment, placement, FieldColour);
            }
        }

        if (ShowSingularities) {
            var arm = SingularityArm * Diagonal(artefacts);

            foreach (var point in artefacts.Singularities) {
                draw.Cross(Matrix4x4.TransformPosition(point, placement), arm, SingularityColour);
            }
        }

        if (ShowQuantization) {
            Arcs(draw, artefacts, placement);
        }
    }

    /// <summary>What a feature chain is drawn in.</summary>
    public Color4 FeatureColour { get; set; } = new(1f, 0.45f, 0.15f, 1f);

    /// <summary>What a field cross is drawn in.</summary>
    public Color4 FieldColour { get; set; } = new(0.45f, 0.75f, 1f, 0.7f);

    /// <summary>What a singularity is drawn in.</summary>
    public Color4 SingularityColour { get; set; } = new(1f, 0.25f, 0.85f, 1f);

    /// <summary>What the conditioned wireframe is drawn in.</summary>
    public Color4 WireColour { get; set; } = new(0.55f, 0.58f, 0.62f, 0.35f);

    /// <summary>What an arc the quantizer had to move a long way is drawn in.</summary>
    public Color4 StrainColour { get; set; } = new(1f, 0.30f, 0.30f, 1f);

    /// <summary>What an arc that got what the density field asked for is drawn in.</summary>
    public Color4 SettledColour { get; set; } = new(0.40f, 0.90f, 0.55f, 1f);

    void Wireframe(GizmoDraw draw, RemeshDump artefacts, in Matrix4x4 placement) {
        var mesh = artefacts.Conditioned;

        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            var (a, b) = mesh.Edges[edge];

            draw.Line(
                Matrix4x4.TransformPosition(mesh.Positions[a], placement),
                Matrix4x4.TransformPosition(mesh.Positions[b], placement),
                WireColour
            );
        }
    }

    void Regions(GizmoDraw draw, RemeshDump artefacts, in Matrix4x4 placement) {
        var mesh = artefacts.Conditioned;

        foreach (var region in artefacts.Layout) {
            if ((uint)region.Triangle >= (uint)mesh.FaceCount) {
                continue;
            }

            var colour = region.Patch < 0 ? WireColour : Patches[region.Patch % Patches.Length];
            var loop = mesh.CornersOf(region.Triangle);

            for (var corner = 0; corner < loop.Length; corner++) {
                draw.Line(
                    Matrix4x4.TransformPosition(mesh.Positions[loop[corner]], placement),
                    Matrix4x4.TransformPosition(mesh.Positions[loop[(corner + 1) % loop.Length]], placement),
                    colour
                );
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>An arc's colour is the distance between what it got and what it wanted, which is what
    ///     the quantization is <i>for</i>.</b> docs/plan/41 § D7's cost is that difference squared,
    ///     summed over arcs, so an arc drawn red is where the consistency system had to spend — and
    ///     drawing the integer alone would say what happened without saying what it cost.
    /// </summary>
    void Arcs(GizmoDraw draw, RemeshDump artefacts, in Matrix4x4 placement) {
        foreach (var arc in artefacts.Quantization) {
            var strain = Math.Clamp(MathF.Abs(arc.Quads - arc.Target), 0f, 1f);

            draw.Line(
                Matrix4x4.TransformPosition(arc.From, placement),
                Matrix4x4.TransformPosition(arc.To, placement),
                Blend(SettledColour, StrainColour, strain)
            );
        }
    }

    static void Segment(GizmoDraw draw, in RemeshSegment segment, in Matrix4x4 placement, Color4 colour) =>
        draw.Line(
            Matrix4x4.TransformPosition(segment.From, placement),
            Matrix4x4.TransformPosition(segment.To, placement),
            colour
        );

    /// <summary>The conditioned mesh's bounding diagonal, which is what a fraction is a fraction of.</summary>
    static float Diagonal(RemeshDump artefacts) {
        var mesh = artefacts.Conditioned;

        if (mesh.PositionCount == 0) {
            return 1f;
        }

        var low = mesh.Positions[0];
        var high = low;

        for (var position = 1; position < mesh.PositionCount; position++) {
            low = Vector3.Min(low, mesh.Positions[position]);
            high = Vector3.Max(high, mesh.Positions[position]);
        }

        var span = (high - low).Length();

        return span > 0f ? span : 1f;
    }

    static Color4 Blend(Color4 from, Color4 to, float amount) =>
        new(
            from.R + ((to.R - from.R) * amount),
            from.G + ((to.G - from.G) * amount),
            from.B + ((to.B - from.B) * amount),
            from.A + ((to.A - from.A) * amount)
        );
}
