// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.SceneView;
using Vixen.Engine.Renderer;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Materials;
using Vixen.Shaders;

namespace Vixen.Editor.App;

/// <summary>The standard renderer, over a scene document's world, in the editor.</summary>
/// <remarks>
///     <para>
///         <b>The three inputs a compositor-driven viewport needs, and nothing that draws.</b> A
///         <see cref="Engine.Renderer.WorldRenderer" />, the two extraction systems that fill it from a
///         world, and a <see cref="RenderView" /> aimed by an <see cref="EditorCamera" />. What is
///         missing is the frame document and the pane that presents it, which is deliberately somebody
///         else's: every one of these is assertable on its own, and a pane built over inputs nobody had
///         checked is a pane whose first failure is attributed to the pane.
///     </para>
///     <para>
///         ⚠ <b><c>Mount</c> is not called, and the mesh source is set by hand instead.</b> Mounting
///         wants an <c>AssetManager</c>, which resolves addresses through a catalog a *content build*
///         wrote — and <c>ProjectMeshSource</c>'s own remarks give the argument against making the
///         viewport wait for one: "waiting for a build to look at a level would make the viewport a
///         function of the build rather than of the files". So geometry comes from the same import
///         cache the tool renderer already reads, and the two cannot disagree about what a mesh is.
///     </para>
///     <para>
///         ⚠ <b>Which leaves <see cref="Engine.Renderer.WorldRenderer.Painter" /> null, and that is a
///         real degrade rather than a detail.</b> There is no editor-side <see cref="IMaterialSource" />
///         — <c>ProjectSurfaceSource</c> is the tool renderer's tint-and-style source and does not
///         satisfy that interface — so every drawable in the scene is painted with
///         <see cref="Fallback" /> whatever material it names. <see cref="Degraded" /> is the sentence
///         that says so, and it exists because the alternative is a viewport where assigning a material
///         appears to do nothing.
///     </para>
///     <para>
///         ⚠ <b>Not a <c>SceneRenderer.Degrade</c>, and it could not be.</b> That mechanism reports a
///         <em>node</em>'s degradation into <c>GraphicsCompositor.Degradations</c> — <c>Degrade</c> is
///         protected, a node calls it about itself, and the collection walks the frame's nodes. A
///         missing material source is a fact about the host before any document is loaded: there is no
///         node whose condition it is, and every node in the frame would have to repeat it. It belongs
///         where <see cref="EditorEffects.Refusal" /> is, which is a string a panel reads.
///     </para>
/// </remarks>
sealed class EditorWorldRenderer : IDisposable {
    readonly LightExtractionSystem lights;
    bool disposed;

    /// <summary>Builds the renderer and the bridges into it.</summary>
    /// <param name="device">The device everything lives on.</param>
    /// <param name="effects">
    ///     Where variants come from — <see cref="EditorEffects.System" />, which is one object for the
    ///     life of the editor precisely because this constructor keeps it.
    /// </param>
    /// <param name="meshes">
    ///     Where the geometry a <c>MeshRenderable</c> names comes from, or null to draw none of them.
    ///     Null is not an error: a project with no import cache yet has nothing to resolve, and the
    ///     entities wait rather than disappearing — see <see cref="Waiting" />.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public EditorWorldRenderer(IGraphicsDevice device, EffectSystem effects, IMeshSource? meshes = null) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(effects);

        // ⚠ A tenth of the geometry budget a game's default reserves. A scene open in an editor is
        // one level, and the buffers are allocated for real on the device the moment this is built —
        // whereas a game sizes them for whatever it streams. `Meshes.Dropped` is what says a level
        // outgrew them, and it is a number rather than a silent stop.
        Renderer = new(device, effects, vertexCapacity: 1 << 18, indexCapacity: 1 << 19) {
            Source = meshes
        };

        Fallback = CompileFallback();

        // ⚠ Assembled here rather than through `WorldRenderer.Register`, which takes an `EngineLoop`.
        // The editor runs no system graph at all — `TransformSystem` is resolved by hand for the same
        // reason, and that decision is `EditorApplication.ResolveTransforms`' own remarks — so what a
        // loop would give is a scheduler for two calls whose order is already decided by this file.
        Meshes = new(Renderer.Host.System, Renderer.Meshes, Renderer.Transforms, Renderer.Materials, Renderer.Residency) {
            Meshes = meshes,

            // ⚠ And *not* `Materials`, which stays null. See the type's remarks: with no source, a
            // drawable that names a material still draws — in this one — because a host that cannot
            // resolve one should show geometry rather than nothing.
            Material = Fallback
        };

        lights = new(Renderer.Lighting);
    }

    /// <summary>The frame, its features, its descriptor pools and its compositor builder.</summary>
    public WorldRenderer Renderer { get; }

    /// <summary>What turns the world's drawables into the frame's objects.</summary>
    public MeshExtractionSystem Meshes { get; }

    /// <summary>What the viewport looks through.</summary>
    /// <remarks>
    ///     Held here rather than made per frame because <see cref="RenderView.PreviousViewProjection" />
    ///     is the one piece of a view that has to outlive a frame — a motion vector is measured against
    ///     it, and a view rebuilt every frame reports no history for ever.
    /// </remarks>
    public RenderView View { get; } = new("Editor");

    /// <summary>What a drawable is painted with, or null when nothing would compile one.</summary>
    /// <remarks>
    ///     ⚠ <b>Null here is a frame in which nothing is drawn at all</b>, not a frame drawn untinted:
    ///     <c>MeshExtractionSystem</c> assigns a material only when it has one, and an object with none
    ///     resolves to no variant and is skipped. It is why <see cref="Degraded" /> distinguishes the
    ///     two cases rather than saying "fallback" in both.
    /// </remarks>
    public Material? Fallback { get; }

    /// <summary>Which stages an extracted object appears in.</summary>
    /// <remarks>
    ///     ⚠ <b>Set it before the first <see cref="Extract" />, not after.</b> A stage mask is copied
    ///     into each render object as it is created and a settled entity is never re-extracted, so a
    ///     mask assigned later reaches the next entity somebody adds and none of the ones already
    ///     there. Zero draws nothing, which is the state a host that has not loaded a frame document
    ///     is honestly in — a stage's index is assigned by the render system when the document
    ///     declares it.
    /// </remarks>
    public RenderStageMask Stages {
        get => Meshes.Stages;
        set => Meshes.Stages = value;
    }

    /// <summary>How many of the world's entities are in the frame's object list.</summary>
    public int ObjectCount => Meshes.ObjectCount;

    /// <summary>How many are waiting for geometry that has not been imported yet.</summary>
    public int Waiting => Meshes.Waiting;

    /// <summary>How many lights the last extraction put in the frame's list.</summary>
    public int LightCount => lights.LightCount;

    /// <summary>Why the picture is not what a game's would be, or null when it is.</summary>
    /// <inheritdoc cref="EditorWorldRenderer" path="/remarks/para[3]" />
    public string? Degraded => Fallback is null
        ? "No material would compile, so nothing in the scene is drawn at all."
        : Renderer.Painter is null
            ? "The editor has no material source, so every mesh is drawn in the fallback material "
            + "rather than the one it names."
            : null;

    /// <summary>Points the view where an editor camera is looking.</summary>
    /// <param name="camera">The pane's camera.</param>
    /// <param name="aspectRatio">Width over height, in the pane's own pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="camera" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two paths, because <see cref="RenderCamera" /> is perspective and an editor camera
    ///         is not always.</b> Assigning <see cref="RenderView.Camera" /> is the better one — it
    ///         sets the position, the matrix and therefore the frustum from one description, and it is
    ///         what a shadow cascade fit needs, since slicing a cone wants the field of view a matrix
    ///         alone cannot give back. An orthographic pane has no cone, so it sets the matrix
    ///         directly and leaves the camera null, which is the same answer a shadow cascade's own
    ///         view gives.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="RenderView.Advance" /> first, before the new matrix.</b> That is what
    ///         makes a motion vector measure this frame against last frame rather than against
    ///         itself — and it has to be called by whoever owns the per-frame update, which here is
    ///         this.
    ///     </para>
    /// </remarks>
    public void Aim(EditorCamera camera, float aspectRatio) {
        ArgumentNullException.ThrowIfNull(camera);
        ObjectDisposedException.ThrowIf(disposed, this);

        View.Advance();

        // A pane one pixel wide during a splitter drag, or measured before the layout pass has run.
        // A zero or negative aspect makes a projection full of infinities and a frustum of NaN planes,
        // which culls the entire scene rather than failing.
        var aspect = aspectRatio > 0f && float.IsFinite(aspectRatio) ? aspectRatio : 1f;

        if (camera.IsOrthographic) {
            View.Camera = null;
            View.Position = camera.Position;
            View.ViewProjection = camera.ViewProjection(aspect);

            return;
        }

        View.Camera = new RenderCamera(
            camera.Position,
            camera.Forward,
            Vector3.UnitY,
            camera.FieldOfView,
            aspect,
            camera.NearPlane,
            camera.FarPlane
        );
    }

    /// <summary>Brings the frame's objects and lights up to date with a world.</summary>
    /// <param name="world">The scene document's world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>After the transforms have been resolved, not before.</b> Both queries want
    ///     <c>WorldTransform</c> and neither computes one: in a game the phase and the declared access
    ///     put them after <c>TransformSystem</c>, and the editor has no graph to do that — so the order
    ///     is <see cref="EditorApplication.ResolveTransforms" /> and then this, and an extraction that
    ///     ran first would place every object where it was last frame.
    /// </remarks>
    public void Extract(World world) {
        ArgumentNullException.ThrowIfNull(world);
        ObjectDisposedException.ThrowIf(disposed, this);

        Meshes.Extract(world);
        lights.Extract(world);
    }

    /// <summary>One grey metal-roughness surface, for everything this cannot paint properly.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a placeholder for a missing material — it is the material every mesh in the editor
    ///     is drawn in</b>, because there is no editor-side <see cref="IMaterialSource" />. A game
    ///     compiles this too and draws it approximately never; here it is the whole picture, which is
    ///     what <see cref="Degraded" /> says out loud.
    /// </remarks>
    static Material? CompileFallback() {
        var compilation = MaterialCompiler.Compile(
            new() {
                ShaderName = "ForwardPlus",
                Features = [
                    new MetalRoughnessFeature {
                        BaseColor = new Vector3(0.62f, 0.63f, 0.66f),
                        Metalness = 0f,
                        Roughness = 0.7f
                    }
                ]
            }
        );

        return compilation.Failed ? null : compilation.Material;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        // ⚠ The claims before the renderer, because a claim is a slice of the geometry buffer the
        // renderer owns and releasing one afterwards would be a release against a disposed pool.
        Meshes.Clear();
        Renderer.Dispose();
    }
}
