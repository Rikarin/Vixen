// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;
using Vixen.Core.IO.Watch;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The three inputs a compositor-driven viewport is fed from, before there is one.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here is about what was <em>produced</em>, not about what was called.</b>
///         A test that asserts <c>Extract</c> ran is the same mistake as a counter incremented past the
///         <c>continue</c> that skipped the work — so what is asserted is the number of objects in the
///         frame's store, where their bounds ended up, which material reached the feature that draws
///         them, and what the lighting feature's list contains.
///     </para>
///     <para>
///         On <see cref="NullDevice" />, which records rather than draws. That is enough for all three:
///         the geometry buffer, the suballocation, the descriptor allocators and the Raven compilation
///         are the ones a real frame uses, and only the submission is not.
///     </para>
/// </remarks>
public sealed class EditorWorldRendererTests : IDisposable {
    readonly NullDevice device = new();
    readonly List<IDisposable> owned = [];

    public void Dispose() {
        // ⚠ The sessions before the device: an application going down disposes a renderer that holds
        // buffers, descriptor pools and a bindless table, all of which are this device's.
        for (var index = owned.Count - 1; index >= 0; index--) {
            owned[index].Dispose();
        }

        device.Dispose();
    }

    /// <summary>An editor whose host has handed it a device, one frame in.</summary>
    EditorSession Running() {
        var session = EditorSession.Start();

        owned.Add(session);

        session.Application.GraphicsDevice = device;
        session.Frame();

        return session;
    }

    // ------------------------------------------------------------------ 1: the effects tier

    /// <summary>The device arriving is what builds the variant tier and the renderer.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole of step one, and it was twenty lines nobody had written.</b>
    ///     <see cref="EditorEffects" /> was complete, tested and constructed by nothing but its own
    ///     tests; the property the host writes the device into forwarded to the diagnostics module and
    ///     to nothing else.
    /// </remarks>
    [Fact]
    public void A_device_builds_the_effects_tier_and_the_renderer() {
        var editor = Running().Application;

        Assert.NotNull(editor.Effects);
        Assert.NotNull(editor.Frame);

        Assert.Null(editor.Effects.Refusal);
        Assert.True(editor.Effects.SourceCount > 50, $"only {editor.Effects.SourceCount} shader sources were found.");

        // The renderer resolves through *that* system rather than one of its own, which is what makes
        // a shader edit reach it at all.
        Assert.Same(editor.Effects.System, editor.Frame.Renderer.Materials.Effects);
    }

    /// <summary>The shading pass compiles through the tier the editor's renderer holds.</summary>
    /// <remarks>
    ///     ⚠ <b>The set layouts are the load-bearing half.</b> They are what a pipeline is created
    ///     against and what <c>WorldRenderer.AdoptViewLayout</c> reads set 1's layout out of — a variant
    ///     that came back with stages and no layouts resolves, draws nothing and reports success.
    /// </remarks>
    [Fact]
    public void The_shading_pass_resolves_through_the_renderers_own_system() {
        var editor = Running().Application;

        var resolved = editor.Frame!.Renderer.Materials.Effects!.Resolve(EffectKey.Of("ForwardPlus"));

        Assert.NotNull(resolved);
        Assert.NotEmpty(resolved.Stages);
        Assert.NotEmpty(resolved.SetLayouts);
    }

    /// <summary>Losing the device takes both down, and getting one back builds them again.</summary>
    [Fact]
    public void Releasing_the_device_releases_everything_on_it() {
        var editor = Running().Application;

        editor.GraphicsDevice = null;

        Assert.Null(editor.Effects);
        Assert.Null(editor.Frame);

        editor.GraphicsDevice = device;

        Assert.NotNull(editor.Frame);
        Assert.Null(editor.Effects!.Refusal);
    }

    /// <summary>A shader saved in the project recompiles; anything else saved does not.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half of the drain that reads what changed.</b> The rescan beside it
    ///         deliberately ignores the paths — the database's own scan knows how to repair a sidecar
    ///         and a browser applying one path at a time would be a worse second implementation of it
    ///         — but a rebuild reparses every shader the editor has and throws away every variant the
    ///         session compiled, so doing it because somebody saved a texture is a stall per import.
    ///     </para>
    ///     <para>
    ///         The refusal is asserted rather than only the return value, because a rebuild that
    ///         returned true and left the tier holding the old compiler is exactly the failure the
    ///         identity tests in <see cref="EditorEffectsTests" /> are about.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_saved_shader_recompiles_and_a_saved_texture_does_not() {
        var session = Running();
        var editor = session.Application;

        Assert.False(editor.ReloadShaders([new FileChange(new VirtualPath("/Rock.png"), FileChangeKind.Changed)]));
        Assert.Null(editor.Effects!.Refusal);

        File.WriteAllText(Path.Combine(session.Project.Paths.Assets, "Broken.rvn"), "not a shader {{{");

        Assert.True(editor.ReloadShaders([new FileChange(new VirtualPath("/Broken.rvn"), FileChangeKind.Created)]));
        Assert.NotNull(editor.Effects.Refusal);

        // ⚠ Through the reference the renderer took in its constructor, which is the only one that
        // matters: a tier that answered a rebuild with a new system would leave this one compiling
        // the shader that was there before the edit, for ever, silently.
        Assert.Null(editor.Frame!.Renderer.Materials.Effects!.Resolve(EffectKey.Of("ForwardPlus")));
    }

    /// <summary>An overflowed watcher rebuilds, because the drained list describes nothing.</summary>
    [Fact]
    public void Losing_the_change_list_rebuilds_rather_than_assuming_no_shader_moved() =>
        Assert.True(Running().Application.ReloadShaders([]));

    // ------------------------------------------------------------------ 2: the renderer

    /// <summary>Nothing paints materials, so everything is painted with one, and it says so.</summary>
    /// <remarks>
    ///     ⚠ <b>A sentence rather than a silent fallback, and it is not a
    ///     <c>SceneRenderer.Degrade</c>.</b> That reports a <em>node</em>'s condition into
    ///     <c>GraphicsCompositor.Degradations</c>; a missing material source is the host's, before any
    ///     document is loaded, and no node could report it. What must not happen is a viewport where
    ///     assigning a material appears to do nothing and nothing anywhere says why.
    /// </remarks>
    [Fact]
    public void A_null_painter_is_reported_rather_than_being_a_grey_scene_nobody_explains() {
        var frame = Running().Application.Frame!;

        Assert.Null(frame.Renderer.Painter);
        Assert.NotNull(frame.Fallback);
        Assert.NotNull(frame.Degraded);
        Assert.Contains("fallback material", frame.Degraded, StringComparison.Ordinal);
    }

    /// <summary>The geometry comes from the import cache, which is where the tool renderer reads it.</summary>
    /// <remarks>
    ///     ⚠ <b>Not from an <c>AssetManager</c> over a catalog.</b> This used to say "a content
    ///     build's catalog" and that using <c>EditorContent</c> would make the viewport a function of
    ///     the last content build — which is wrong, because <c>LooseContent</c> is not a build and
    ///     reads this same import cache. The reason that holds is that the catalog resolves
    ///     <em>less</em>: an excluded asset gets no address and so no entry, silently, which
    ///     <c>EditorContentTests.An_excluded_asset_is_absent_from_the_catalog_and_still_in_the_import_cache</c>
    ///     demonstrates. Two sources would be two opinions about what a mesh is, and the catalog's
    ///     opinion is the one that omits what a project chose not to ship.
    /// </remarks>
    [Fact]
    public void The_mesh_source_is_the_one_the_tool_renderer_already_reads() {
        var editor = Running().Application;

        Assert.Same(editor.SceneGeometry, editor.Frame!.Renderer.Source);
    }

    // ------------------------------------------------------------------ 3: extraction

    /// <summary>A frame of the seeded scene puts its shapes and its light in the frame's lists.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The counts are the weakest thing asserted here and they are asserted last.</b> An
    ///         object in the store whose bounds are at the origin is an entity extracted before its
    ///         transform was resolved; an object with no material assigned resolves to no variant and
    ///         is skipped by the feature that would draw it. Both report a healthy
    ///         <c>ObjectCount</c>.
    ///     </para>
    ///     <para>
    ///         The seeded scene is two primitives — a crate at (1.5, 0.5, 0) and a barrel at
    ///         (−2, 0.5, 1) — under an empty at the origin, and one directional light.
    ///     </para>
    /// </remarks>
    [Fact]
    public void One_frame_extracts_the_scenes_shapes_and_its_light() {
        var frame = Running().Application.Frame!;

        Assert.Equal(0, frame.Waiting);
        Assert.Equal(2, frame.ObjectCount);

        var objects = frame.Renderer.Host.System.Objects;

        Assert.Equal(2, objects.LiveCount);

        // ⚠ Where they ended up, not how many there are. `MeshExtractionSystem` places an object from
        // `WorldTransform`, which nothing in the editor writes except the transform pass — so an
        // extraction that ran in the wrong order leaves both of these at the origin with every
        // counter green.
        var centres = Centres(frame);

        Assert.Contains(centres, centre => Near(centre, new Vector3(1.5f, 0.5f, 0f)));
        Assert.Contains(centres, centre => Near(centre, new Vector3(-2f, 0.5f, 1f)));

        // And that they are painted at all. A drawable with no material is assigned none, resolves to
        // no variant, and is skipped by `MeshRenderFeature.Draw` — which is a store of two objects
        // and a picture of nothing. Slot zero of the feature's table is the "none" sentinel, so the
        // index is the assertion and the presence of the material in the list is not: a material
        // registered and pointed at by nothing would satisfy the second.
        var painted = objects.Data.Data(frame.Renderer.Materials.MaterialIndex);

        for (var index = 0; index < objects.Count; index++) {
            Assert.NotEqual(0, painted[index]);
            Assert.Same(frame.Fallback, frame.Renderer.Materials.Materials[painted[index]]);
        }

        // The light, and what is in it rather than that there is one. A `Light` with no colour
        // authored is black, which emits nothing however many lumens it declares.
        var light = Assert.Single(frame.Renderer.Lighting.Lights);

        Assert.Equal(1, frame.LightCount);
        Assert.Equal(LightKind.Directional, light.Kind);
        Assert.True(light.Colour.R > 0f || light.Colour.G > 0f || light.Colour.B > 0f, "the sun is black.");
        Assert.True(light.Direction.LengthSquared() > 0.5f, "the sun points nowhere.");
    }

    /// <summary>A second frame does not extract the same entities twice.</summary>
    /// <remarks>
    ///     ⚠ <b>The reconciliation, which is what makes this affordable once a frame.</b> A render
    ///     object holds a residency claim and a slot in every feature's parallel array; an extraction
    ///     that created rather than reconciled would walk the geometry buffer up until a scene stopped
    ///     drawing, and would do it at the frame rate.
    /// </remarks>
    [Fact]
    public void Extracting_every_frame_does_not_grow_the_frame() {
        var session = Running();
        var frame = session.Application.Frame!;

        session.Frames(8);

        Assert.Equal(2, frame.ObjectCount);
        Assert.Equal(2, frame.Renderer.Host.System.Objects.LiveCount);
        Assert.Single(frame.Renderer.Lighting.Lights);
        Assert.Equal(0, frame.Meshes.Dropped);
    }

    /// <summary>An entity added to the scene reaches the frame on the next update.</summary>
    /// <remarks>
    ///     What makes this a viewport rather than a snapshot: the whole point of extracting every
    ///     frame is that the frame follows the document.
    /// </remarks>
    [Fact]
    public void An_entity_added_to_the_scene_appears_in_the_frame() {
        var session = Running();
        var frame = session.Application.Frame!;

        session.Scene.CreateShape(PrimitiveKind.Sphere, LocalTransform.At(new Vector3(0f, 4f, 0f)));
        session.Frame();

        Assert.Equal(3, frame.ObjectCount);

        Assert.Contains(Centres(frame), centre => Near(centre, new Vector3(0f, 4f, 0f)));
    }

    /// <summary>An entity moved this frame is in the frame where it is now.</summary>
    /// <remarks>
    ///     ⚠ <b>The ordering assertion, and it is the reason extraction sits immediately after
    ///     <c>ResolveTransforms</c>.</b> Both extraction queries read <c>WorldTransform</c> and neither
    ///     writes one; in a game the phase and the declared access put them after
    ///     <c>TransformSystem</c>, and the editor has no graph to do that. An extraction that ran first
    ///     places every object where it was last frame — which is a gizmo drag the viewport follows one
    ///     frame late, and every counter reports a healthy frame throughout.
    /// </remarks>
    [Fact]
    public void An_entity_moved_this_frame_is_extracted_where_it_is_now() {
        var session = Running();
        var frame = session.Application.Frame!;

        var entity = session.Scene.CreateShape(PrimitiveKind.Sphere, LocalTransform.At(new Vector3(0f, 4f, 0f)));

        session.Frame();

        Assert.Equal(3, frame.ObjectCount);

        session.Scene.World.Get<LocalTransform>(entity).Position = new Vector3(0f, 12f, 0f);
        session.Frame();

        Assert.Contains(Centres(frame), centre => Near(centre, new Vector3(0f, 12f, 0f)));
        Assert.DoesNotContain(Centres(frame), centre => Near(centre, new Vector3(0f, 4f, 0f)));
    }

    /// <summary>The stage mask a host sets before extracting is the one the objects carry.</summary>
    /// <remarks>
    ///     ⚠ <b>Which is why it has to be set first.</b> The mask is copied into each render object as
    ///     it is created and a settled entity is never re-extracted, so a mask assigned after the first
    ///     frame reaches the next entity somebody adds and none of the ones already there — a scene
    ///     where everything placed before the viewport opened is invisible and everything placed after
    ///     it is not.
    /// </remarks>
    [Fact]
    public void Objects_carry_the_stage_mask_that_was_set_before_they_were_extracted() {
        using var device2 = new NullDevice();
        var project = new Editor.Core.EditorProject(new Editor.Core.ProjectPaths(Temporary()));

        using var effects = new EditorEffects(device2, project);
        using var frame = new EditorWorldRenderer(device2, effects.System);
        using var world = new Ecs.World();

        var stage = frame.Renderer.Host.System.AddStage(new RenderStage("Opaque"));

        frame.Stages = stage.Mask;

        var entity = world.Create();

        world.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });
        Rendering.Ecs.PrimitiveShapes.Attach(world, entity, PrimitiveKind.Cube);

        frame.Extract(world);

        var single = Assert.Single(frame.Renderer.Host.System.Objects.All.ToArray());

        Assert.Equal(stage.Mask, single.Stages);
    }

    // ------------------------------------------------------------------ the view

    /// <summary>The view is aimed where the pane's camera is looking.</summary>
    /// <remarks>
    ///     The frustum is derived from the matrix rather than set beside it, so asserting that the
    ///     pivot the camera orbits is inside it is an assertion about both at once.
    /// </remarks>
    [Fact]
    public void A_perspective_camera_fills_the_view_with_a_frustum_that_contains_what_it_looks_at() {
        var frame = Running().Application.Frame!;
        var camera = new EditorCamera { Pivot = new Vector3(3f, 1f, -2f), Distance = 8f };

        frame.Aim(camera, 16f / 9f);

        Assert.Equal(camera.Position, frame.View.Position);
        Assert.NotNull(frame.View.Camera);
        Assert.True(frame.View.Frustum.Contains(camera.Pivot), "the pivot is outside the view's own frustum.");

        // Behind the eye, which a frustum whose planes came from the wrong matrix would happily hold.
        Assert.False(
            frame.View.Frustum.Contains(camera.Position - (camera.Forward * 5f)),
            "a point behind the camera is inside its frustum."
        );
    }

    /// <summary>An orthographic pane sets the matrix itself, because a render camera has no ortho.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="RenderCamera" /> describes a perspective frustum and nothing else.</b>
    ///     Assigning one for a top view would put the whole scene behind a cone it is not being drawn
    ///     through — geometry culled that is on screen, which reads as objects vanishing when the
    ///     viewport is switched to a plan view.
    /// </remarks>
    [Fact]
    public void An_orthographic_camera_is_not_described_as_a_perspective_one() {
        var frame = Running().Application.Frame!;
        var camera = new EditorCamera { IsOrthographic = true, Distance = 10f };

        frame.Aim(camera, 1.5f);

        Assert.Null(frame.View.Camera);
        Assert.Equal(camera.ViewProjection(1.5f), frame.View.ViewProjection);

        // The matrix an orthographic projection has and a perspective one does not: no perspective
        // divide, so the w row is (0, 0, 0, 1).
        Assert.Equal(1f, frame.View.ViewProjection.M44, 5);
    }

    /// <summary>The previous matrix is last frame's rather than this frame's.</summary>
    /// <remarks>
    ///     A view nobody advances reports a previous matrix of zero, which reads as "no history" — and
    ///     one advanced after the write reports this frame's, which is a motion vector of nearly zero
    ///     for a camera that moved.
    /// </remarks>
    [Fact]
    public void Aiming_moves_the_last_matrix_into_the_previous_one() {
        var frame = Running().Application.Frame!;
        var camera = new EditorCamera { Distance = 5f };

        frame.Aim(camera, 1f);

        var first = frame.View.ViewProjection;

        camera.Pivot = new Vector3(20f, 0f, 0f);
        frame.Aim(camera, 1f);

        Assert.Equal(first, frame.View.PreviousViewProjection);
        Assert.NotEqual(first, frame.View.ViewProjection);
    }

    /// <summary>Reloading the viewport's frame releases the tree it replaced.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The path where a leaked compositor node costs the most.</b> <c>ExternalEdits</c>
    ///         routes a change to <c>Frame.vxcompositor</c> here, so this runs whenever somebody saves
    ///         the document they are authoring — and a compositor node may own device memory a frame
    ///         cannot, because a cached shadow atlas has to outlive the frame and every
    ///         <c>!Resource</c> in a document is transient. Sample 13's two are 94 MiB together, and
    ///         every reload used to abandon them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Built and adopted first, released second</b>, which is what <see cref="EditorWorldRenderer.Reload" />
    ///         does and why: a document that fails to bind has to leave the working frame whole, and a
    ///         frame whose nodes were freed first is not whole. That ordering is asserted here only in
    ///         the sense that the probe is disposed by the <em>second</em> reload rather than the
    ///         first.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Reloading_the_frame_disposes_the_tree_it_replaced() {
        using var device2 = new NullDevice();
        var project = new Editor.Core.EditorProject(new Editor.Core.ProjectPaths(Temporary()));

        using var effects = new EditorEffects(device2, project);
        using var frame = new EditorWorldRenderer(device2, effects.System);
        using var world = new Ecs.World();

        var probe = new DisposeProbe();

        frame.Renderer.Host.Builder.Factories.Add(new ProbeFactory(probe));

        var editors = frame.Compositor;

        frame.Reload(new() { Game = new ProbeAsset { Name = "Probe" } }, world);

        Assert.NotSame(editors, frame.Compositor);
        Assert.Equal(0, probe.Disposals);

        // And back to the editor's own document, which is what closing a `StandardFrameDocument` does.
        frame.Reload(null, world);

        Assert.Equal(1, probe.Disposals);
    }

    /// <summary>A node that counts how many times it was told to release.</summary>
    sealed class DisposeProbe : Rendering.Compositor.SceneRenderer, IDisposable {
        public int Disposals { get; private set; }

        /// <inheritdoc />
        public void Dispose() => Disposals++;
    }

    /// <summary>A node kind the builder's switch does not know, so the extension arm builds it.</summary>
    sealed record ProbeAsset : Rendering.Compositor.ISceneRendererAsset {
        /// <inheritdoc />
        public string Name { get; init; } = string.Empty;

        /// <inheritdoc />
        public bool Enabled { get; init; } = true;
    }

    sealed class ProbeFactory(DisposeProbe probe) : Rendering.Compositor.ISceneRendererFactory {
        /// <inheritdoc />
        public Rendering.Compositor.SceneRenderer? Create(
            Rendering.Compositor.ISceneRendererAsset declared,
            Rendering.Compositor.CompositorBuilder builder
        ) =>
            declared is ProbeAsset ? probe : null;
    }

    // ------------------------------------------------------------------ helpers

    static bool Near(Vector3 left, Vector3 right) => (left - right).Length() < 0.25f;

    /// <summary>Where the frame's objects actually are, which is what an ordering bug moves.</summary>
    static List<Vector3> Centres(EditorWorldRenderer frame) {
        var centres = new List<Vector3>();

        foreach (ref var live in frame.Renderer.Host.System.Objects.All) {
            centres.Add(live.Bounds.Center);
        }

        return centres;
    }

    string Temporary() {
        var root = Path.Combine(Path.GetTempPath(), "vixen-frame-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "Library"));
        owned.Add(new Scratch(root));

        return root;
    }

    sealed class Scratch(string root) : IDisposable {
        public void Dispose() {
            try {
                if (Directory.Exists(root)) {
                    Directory.Delete(root, recursive: true);
                }
            } catch (IOException) {
                // A cache the test wrote and the OS has not let go of. Not what is under test.
            }
        }
    }
}
