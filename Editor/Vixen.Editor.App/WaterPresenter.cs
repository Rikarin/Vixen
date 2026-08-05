// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.SceneView;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Water;
using Vixen.Water;

namespace Vixen.Editor.App;

/// <summary>Draws the water a scene carries into a pane's scene pass.</summary>
/// <remarks>
///     <para>
///         <b>The half of doc 35's toolset the viewport was not showing.</b> <c>WaterMode</c>'s draw
///         gesture writes a real <c>.vxspline</c> and creates a <c>WaterBodyComponent</c> naming it,
///         <c>WaterModule</c> registers the mode, and <c>ScenePresenter</c> had no occurrence of the
///         word "water" in it — so a lake authored in the viewport was an entity in the outliner and
///         dry ground on screen. <see cref="VegetationPresenter" />'s shape, one subsystem over,
///         because this is the same defect and the fix should be the same fix.
///     </para>
///     <para>
///         ⚠ <b>The fold is <c>WaterZoneSystem</c>'s own, run over the scene document's world.</b>
///         [35 § D2](../../docs/plan/35-water.md#d2-one-evaluator-two-hosts-and-the-seam-is-a-test)
///         is a rule about hosts, and the editor is a host: a second fold here would be a second
///         opinion about where the shoreline is, and it would agree with the game's until it stopped.
///         <c>Fold(World)</c> is public for exactly this — the editor runs no system graph, so there
///         is no <c>SystemContext</c> to hand it, and there does not need to be one.
///     </para>
///     <para>
///         ⚠ <b>The surface is evaluated on the CPU, and here that is the honest path rather than the
///         cheap one.</b> The runtime draws it as a displaced quadtree into two planes that
///         <c>!Water</c> composites, which needs a multi-target pass and a screen-space integration a
///         pane with one colour attachment cannot host. What § D2 guarantees is that
///         <see cref="WaterQuery" /> is the <em>same</em> surface that vertex stage samples — so a
///         grid over the window, displaced by the same evaluator at the same water clock, is the
///         picture the game will draw and not an impression of it. Compare the grass, where a CPU
///         preview of a hundred thousand blades would have been a lie and the device scatter was used
///         instead.
///     </para>
///     <para>
///         ⚠ <b>It is translucent and it writes depth, which is why it is recorded last of the solid
///         geometry.</b> <see cref="MeshRenderer" /> blends premultiplied, so the ground and the
///         block-out under a lake have to already be in the target when the water goes over them; and
///         it writes depth so that the grid lines and entity markers below the surface are hidden by
///         it, which is what makes a submerged object read as submerged.
///     </para>
/// </remarks>
sealed class WaterPresenter : IDisposable {
    /// <summary>How many quads across a zone's window the preview grid spans.</summary>
    /// <remarks>
    ///     ⚠ <b>A fixed grid over the window rather than the field's own resolution.</b> A zone's
    ///     field is 257² by default, which is a hundred and thirty thousand triangles per zone per
    ///     frame of closed-form wave arithmetic — a viewport that stops being interactive over a lake.
    ///     Ninety-six quads is a swell whose crests are readable at the range an author works at, and
    ///     it is bounded by the zone count rather than by how finely the zone was authored.
    /// </remarks>
    const int GridQuads = 96;

    /// <summary>What the surface is tinted where it is deep, before the depth fade.</summary>
    /// <remarks>
    ///     Premultiplied on the way into the vertex, because the pipeline blends that way — see
    ///     <see cref="MeshRenderer" />'s colour attachment. A straight alpha here is a lake that
    ///     washes out to white over pale ground.
    /// </remarks>
    static readonly Color4 DeepColour = new(0.05f, 0.17f, 0.30f, 1f);

    /// <summary>And where it is shallow, which is what makes a shoreline visible at all.</summary>
    static readonly Color4 ShallowColour = new(0.24f, 0.55f, 0.58f, 1f);

    /// <summary>How deep the water has to be to be drawn at <see cref="DeepColour" />, in metres.</summary>
    const float ColourDepth = 6f;

    /// <summary>How opaque the surface is over deep water, 0…1.</summary>
    const float DeepAlpha = 0.82f;

    /// <summary>And at the shoreline, where the ground has to show through for the edge to read.</summary>
    const float ShallowAlpha = 0.35f;

    /// <summary>The view the zone windows are centred on. One per pane, because cameras differ.</summary>
    readonly RenderView view = new("editor.water");

    /// <summary>The fold, which is the runtime's own object rather than a copy of its rules.</summary>
    readonly WaterZoneSystem zones;

    /// <summary>The triangles, rebuilt every frame because the surface moves every frame.</summary>
    readonly MeshRenderer surface;

    /// <summary>One query per zone, rebuilt when its sea state changed. See <see cref="Query" />.</summary>
    readonly Dictionary<Entity, (WaterWaveSpectrum Spectrum, float Attenuation, WaterQuery Query)> queries = [];

    readonly List<MeshVertex> vertices = [];
    readonly List<uint> indices = [];

    /// <summary>Which grid corners had water in them, reused across zones.</summary>
    readonly List<bool> wet = [];

    /// <summary>Where each grid corner's vertex landed, or -1 for a dry one.</summary>
    readonly List<int> placed = [];

    /// <summary>The water clock. A pane-local stopwatch is the presenter's only frame clock.</summary>
    /// <remarks>
    ///     ⚠ <b>Read into <c>WaterZoneSystem.WaterTime</c> rather than kept beside it.</b> There is
    ///     one water clock — § D2 — and in a game <c>WaterClockSystem</c> is its single writer. The
    ///     editor runs no system graph, so this stands in for it; what must not happen is two
    ///     numbers, which is a surface drawn at one time and queried at another.
    /// </remarks>
    readonly Stopwatch clock = Stopwatch.StartNew();

    bool disposed;

    public WaterPresenter(IGraphicsDevice device, MeshShaders shaders, RenderOutput output) {
        ArgumentNullException.ThrowIfNull(device);

        zones = new(view);
        zones.Splines = new SplineAdapter(this);
        zones.Waves = new WaveAdapter(this);
        zones.Ground = new GroundAdapter(this);

        // The default ring: ninety-six quads is nine and a half thousand vertices and fifty-five
        // thousand indices per zone, which is one zone comfortably and two at the edge — and a third
        // zone is truncated to whole triangles rather than drawn wrong. See MeshRenderer.Dropped.
        surface = new(device, shaders, output);
    }

    /// <summary>Where the splines, the sea states and the ground come from, or null while nothing supplies them.</summary>
    /// <remarks>
    ///     <see cref="ScenePresenter.VegetationScene" />'s seam exactly: null is a viewport with no
    ///     water in it and nothing else different, because a body whose curve nothing can supply is
    ///     what the fold already counts into <c>UnresolvedBodies</c>.
    /// </remarks>
    public IWaterScene? Scene { get; set; }

    /// <summary>The fold, for the panel that shows its counts and the tests that assert them.</summary>
    public WaterZoneSystem Zones => zones;

    /// <summary>How many zones the last upload found a field for.</summary>
    public int ZonesDrawn { get; private set; }

    /// <summary>How many triangles they came to.</summary>
    public int Triangles { get; private set; }

    /// <summary>How many draws the last <see cref="Record" /> issued.</summary>
    public int Draws { get; private set; }

    /// <summary>Folds the scene's zones and stages the surface this frame draws.</summary>
    /// <param name="world">The scene's world, whose transforms have already been resolved.</param>
    /// <param name="camera">The pane's camera, which is what the zone windows follow.</param>
    /// <param name="aspect">Its aspect ratio.</param>
    /// <remarks>
    ///     ⚠ <b>Outside the render pass</b>, like every other upload here: this writes a vertex and
    ///     an index buffer, and a Vulkan command list may not transfer inside a pass.
    /// </remarks>
    public void Upload(World? world, EditorCamera camera, float aspect) {
        ArgumentNullException.ThrowIfNull(camera);
        ObjectDisposedException.ThrowIf(disposed, this);

        vertices.Clear();
        indices.Clear();

        ZonesDrawn = 0;
        Triangles = 0;

        if (world is null || Scene is null) {
            // ⚠ Uploaded empty rather than left alone, for the reason `VegetationPresenter` gives:
            // the renderer draws what its last upload wrote, so a scene that closed would otherwise
            // leave its lake floating in every pane.
            surface.Upload([], []);

            return;
        }

        // The matrix's setter re-derives the frustum, so the two cannot describe different volumes.
        view.Position = camera.Position;
        view.ViewProjection = camera.ViewProjection(aspect);

        // The one clock, written here because the editor has no WaterClockSystem to write it.
        zones.WaterTime = (float) clock.Elapsed.TotalSeconds;
        zones.Fold(world);

        foreach (var (entity, component) in zones.Zones) {
            if (!zones.States.TryGetValue(entity, out var state) || state.Field is not { } field) {
                continue;
            }

            Tessellate(state, field, Query(entity, in component, state), zones.WaterTime);

            ZonesDrawn++;
        }

        Triangles = indices.Count / 3;

        surface.Upload(CollectionsMarshal.AsSpan(vertices), CollectionsMarshal.AsSpan(indices));
    }

    /// <summary>Records this frame's water. Must be inside the scene's render pass.</summary>
    /// <returns>How many draws were issued.</returns>
    public int Record(ICommandList commands, in Matrix4x4 viewProjection) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        surface.Record(commands, in viewProjection);
        Draws = surface.Draws;

        return Draws;
    }

    /// <summary>One zone's query, summed once and kept until its sea state changes.</summary>
    /// <remarks>
    ///     <c>WaterZoneSystem</c>'s own cache, kept a second time here because its copy is private and
    ///     is reached only through <c>QueryAt</c> — which answers "whichever zone contains this
    ///     point", and a tessellation wants "this zone's". Building one per frame would re-sum
    ///     thirty-two Gerstner waves per zone per pane, which is the allocation
    ///     <see cref="WaterQuery.SetSpectrum" /> exists to avoid.
    /// </remarks>
    WaterQuery Query(Entity entity, in WaterZoneComponent component, WaterZoneState state) {
        var attenuation = component.AttenuationDepth > 0f
            ? component.AttenuationDepth
            : WaterAttenuation.Default.Depth;

        // ⚠ A spectrum that cannot be summed draws a still sea rather than taking the pane down, and
        // that is the runtime's own answer rather than a kindness: `WaterMeshRenderer.Stage` sums
        // zero waves for exactly this case. A zone authored in a file that never wrote a `waves:`
        // block holds a *zeroed* spectrum — minimum wavelength zero, which the dispersion relation
        // divides by — and it is the ordinary state of a lake somebody has just placed.
        var spectrum = component.Waves.Validate() is null ? component.Waves : Still;

        if (queries.TryGetValue(entity, out var cached)
            && cached.Spectrum.Equals(spectrum)
            && cached.Attenuation.Equals(attenuation)) {
            return cached.Query;
        }

        // ⚠ Through the state rather than constructed bare, so the query's `FieldSource` is the zone
        // — a zone reshaped to a new resolution builds a new `WaterField`, and a query holding the
        // old object would keep answering from arrays nothing writes any more.
        var query = state.Query(spectrum, new(attenuation));

        queries[entity] = (spectrum, attenuation, query);

        return query;
    }

    /// <summary>A summable spectrum with no swell in it, for a zone whose own cannot be summed.</summary>
    /// <remarks>
    ///     Zero amplitude rather than <see cref="WaterWaveSpectrum.Calm" />, because inventing a sea
    ///     state for a zone that has none would show an author waves they did not author — and the
    ///     one thing a preview must not do is disagree with the game about what is on the disk.
    /// </remarks>
    static WaterWaveSpectrum Still => WaterWaveSpectrum.Default with { AmplitudeScale = 0f, Steepness = 0f };

    /// <summary>Lays a grid over one zone's window and keeps the quads that have water under them.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A quad is emitted only when all four of its corners are wet</b>, which is what
    ///         gives the sheet an edge at the shoreline instead of a skirt that runs off to the
    ///         window's corner. The cost is that the edge is quantised to the grid — a lake's outline
    ///         is accurate to about a metre at the default extent, which is a preview and says so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The height comes from the query and the coverage from the field, and they are two
    ///         different questions.</b> The field says whether a body reaches here at all; the query
    ///         adds the sea state on top of what the field rasterised, attenuated by the depth. Asking
    ///         the field alone would draw a dead-flat lake — which is exactly what a zone with no
    ///         sea state <em>should</em> look like, and would hide the case where the waves are wired
    ///         wrong.
    ///     </para>
    /// </remarks>
    void Tessellate(WaterZoneState state, WaterField field, WaterQuery query, float waterTime) {
        var window = state.Window;
        var step = window.Extent / GridQuads;
        var stride = GridQuads + 1;
        var first = vertices.Count;

        wet.Clear();
        placed.Clear();

        var evaluator = query.Evaluator();

        for (var z = 0; z <= GridQuads; z++) {
            for (var x = 0; x <= GridQuads; x++) {
                var at = window.Origin + new Vector2(x * step, z * step);
                var sample = field.Sample(at);

                if (sample.Coverage <= 0f) {
                    wet.Add(false);
                    placed.Add(-1);

                    continue;
                }

                var surfaceSample = evaluator.Sample(at, waterTime);

                wet.Add(true);
                placed.Add(vertices.Count);

                vertices.Add(
                    new(
                        new(at.X, surfaceSample.Height, at.Y),
                        surfaceSample.Normal,
                        Tint(sample.Depth, sample.Coverage)
                    )
                );
            }
        }

        if (vertices.Count == first) {
            return;
        }

        for (var z = 0; z < GridQuads; z++) {
            for (var x = 0; x < GridQuads; x++) {
                var a = (z * stride) + x;
                var b = a + 1;
                var c = a + stride;
                var d = c + 1;

                if (!wet[a] || !wet[b] || !wet[c] || !wet[d]) {
                    continue;
                }

                indices.Add((uint) placed[a]);
                indices.Add((uint) placed[c]);
                indices.Add((uint) placed[b]);

                indices.Add((uint) placed[b]);
                indices.Add((uint) placed[c]);
                indices.Add((uint) placed[d]);
            }
        }
    }

    /// <summary>What one vertex is coloured, premultiplied for the pipeline's blend.</summary>
    /// <remarks>
    ///     ⚠ <b>The alpha follows the depth and not the coverage.</b> Coverage is a mask with a
    ///     falloff a body's shore width decides; depth is how far down the ground is, and it is what
    ///     makes a beach read as a beach rather than as a lake with a soft edge drawn over dry sand.
    ///     Coverage still multiplies in, so a body's own falloff fades the sheet out rather than
    ///     ending it in a step.
    /// </remarks>
    static Color4 Tint(float depth, float coverage) {
        var t = Math.Clamp(depth / ColourDepth, 0f, 1f);

        var colour = new Color4(
            float.Lerp(ShallowColour.R, DeepColour.R, t),
            float.Lerp(ShallowColour.G, DeepColour.G, t),
            float.Lerp(ShallowColour.B, DeepColour.B, t),
            1f
        );

        var alpha = float.Lerp(ShallowAlpha, DeepAlpha, t) * Math.Clamp(coverage, 0f, 1f);

        return new(colour.R * alpha, colour.G * alpha, colour.B * alpha, alpha);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        surface.Dispose();
    }

    /// <summary>The editor's answer to "what curve is this", seen as the fold's own seam.</summary>
    /// <remarks>
    ///     Three adapters rather than one interface implemented three times, because the fold asks
    ///     three unrelated questions and <c>Vixen.Editor.SceneView</c> may not name the renderer's
    ///     interfaces — see <see cref="IWaterScene" /> for why that split is the terrain seam's.
    /// </remarks>
    sealed class SplineAdapter(WaterPresenter owner) : IWaterSplineSource {
        /// <inheritdoc />
        public Spline? SplineFor(string name, in Matrix4x4 placement) =>
            owner.Scene?.SplineFor(name, in placement);
    }

    sealed class WaveAdapter(WaterPresenter owner) : IWaterWaveSource {
        /// <inheritdoc />
        public WaterWaveSpectrum? SpectrumFor(string name) => owner.Scene?.SpectrumFor(name);
    }

    sealed class GroundAdapter(WaterPresenter owner) : IWaterGround {
        /// <inheritdoc />
        public float HeightAt(Vector2 ground) => owner.Scene?.GroundAt(ground) ?? 0f;
    }
}
