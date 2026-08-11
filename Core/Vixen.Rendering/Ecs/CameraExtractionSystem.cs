// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;

namespace Vixen.Rendering.Ecs;

/// <summary>Turns the scene's <see cref="Camera" /> entity into the frame's <see cref="RenderView" />.</summary>
/// <remarks>
///     <para>
///         <b>The last unwired half of "a world is drawn".</b> <see cref="Camera" /> was a component a
///         scene could place, <see cref="CameraMath" /> built the two matrices it implies, and
///         <see cref="RenderView" /> was what every pass culls and draws against — and nothing anywhere
///         read the first and wrote the last. A level with a camera in it rendered from wherever the host
///         had last poked the view by hand, which is why every sample steers its own.
///     </para>
///     <para>
///         <b>One view, not one per camera.</b> The view is handed in rather than created here, because a
///         view's name is what a compositor document binds a node to (<c>view: Camera</c>) and its stage
///         mask is the host's — a document with two cameras in it is a document with two named views, and
///         two of these. What this decides is only which entity fills the one it was given.
///     </para>
///     <para>
///         <b>Lowest <see cref="Camera.Order" /> wins, ties going to the first the world walks.</b> That
///         is <see cref="Camera.Order" />'s documented meaning — "which camera renders first" — read as a
///         priority, and it is the same rule the editor's scene list sorts by. A scene with no camera at
///         all leaves the view exactly as it was and says so through <see cref="Found" />: a frame drawn
///         from a stale matrix is a picture, and a frame drawn from a zeroed one is a black screen that
///         looks like the renderer is broken.
///     </para>
///     <para>
///         <b>It also carries the sub-pixel offset temporal antialiasing needs</b> — see
///         <see cref="JitterTarget" />. That makes this the one place a frame's projection is decided,
///         and the audit of who is allowed to see the offset is worth writing down, because the
///         dangerous half of applying it is not applying it:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Immune, because they read scalars and not a matrix.</b> The shadow cascade fit
///             (<c>ShadowCascades.Split</c>, <c>Sphere</c>, <c>Fit</c> take position, forward, field of
///             view, aspect and the planes), the froxel grid (<c>ClusterGrid.Apply</c> writes
///             <c>tanHalfFov</c> and the planes, deliberately not the matrix), the volumetric fog's own
///             froxel volume, and <see cref="RenderView.ScreenHeightScale" /> for LOD.
///         </item>
///         <item>
///             <b>Safe because they invert this frame's matrix to unproject this frame's depth</b>, so
///             the offset cancels: the deferred world-position reconstruction, SSAO (which inverts
///             <see cref="RenderCamera.Projection" /> — hence the offset living there too), SSR, the
///             screen-probe gather's placement and its host readback of depth and normals, the
///             distance-field AO, the sky's per-pixel ray, the water and underwater passes, and the
///             virtual-shadow page marking.
///         </item>
///         <item>
///             <b>Safe because both matrices carry their own frame's offset</b>: motion vectors, and
///             the terrain, grass and foliage velocity passes. The vector between a jittered current
///             and a jittered previous is exactly where the history <em>texel</em> is, which is what
///             the resolve samples — so no separate un-jittering is needed anywhere.
///         </item>
///         <item>
///             <b>Moved by half a pixel and judged acceptable</b>: the culling frustum, which
///             <see cref="RenderView.ViewProjection" /> re-derives (a jittered camera really does see a
///             half-pixel-shifted volume); the Hi-Z occlusion test, which uses the previous frame's
///             matrix against the previous frame's depth and so is self-consistent; and the fog's
///             temporal reprojection, whose previous matrix is jittered and whose froxels are two
///             orders of magnitude wider than the offset.
///         </item>
///         <item>
///             ⚠ <b>The one consumer that would rather not have it: <c>MotionBlurRenderer</c>.</b> A
///             still camera now produces a velocity of up to one pixel — the difference between two
///             consecutive offsets — where the truth is zero. Its <c>MinimumRadius</c> of half a pixel
///             is what keeps that a copy rather than a smear, since the shutter fraction halves it
///             again; a frame that lowers that threshold would be buying a permanent sub-pixel blur.
///         </item>
///     </list>
///     <para>
///         <b>In <see cref="SystemPhase.PreRender" />, ordered by its declared access</b> — the placement
///         <see cref="LightExtractionSystem" /> explains: <c>TransformSystem</c> writes
///         <see cref="WorldTransform" /> in the same phase and this reads it, so a camera moved this frame
///         is rendered from where it now is rather than from where it was.
///     </para>
/// </remarks>
/// <param name="view">The view to fill.</param>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class CameraExtractionSystem(RenderView view) : SystemBase, IDeclaredAccess {
    readonly QueryDescription cameras = new QueryDescription().WithAll<Camera, WorldTransform>();

    /// <summary>The camera extracted last, or a zeroed one when there was none.</summary>
    /// <remarks>
    ///     Kept so a host can hand it to the passes that want more than a matrix — the tonemap's
    ///     exposure, the defocus, the motion blur's shutter — without walking the world a second time.
    /// </remarks>
    public Camera Chosen { get; private set; }

    /// <summary>The view this fills.</summary>
    public RenderView View { get; } = view ?? throw new ArgumentNullException(nameof(view));

    /// <summary>
    ///     Width over height of what is being rendered into, for a camera whose own is zero.
    /// </summary>
    /// <remarks>
    ///     The host's, set from the frame's size, because <see cref="Camera.AspectRatio" /> of zero means
    ///     "ask the target" and a component cannot know what it is being drawn into. A camera that names
    ///     its own ratio ignores this — which is what a letterboxed cutscene wants.
    /// </remarks>
    public float AspectRatio { get; set; }

    /// <summary>
    ///     The size of what the frame is drawn into, in pixels, or zero for no sub-pixel jitter.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>What turns temporal antialiasing from a blur into a supersampler.</b> The resolve
    ///         averages samples taken at different points inside the pixel; taking them is this
    ///         system's job, because it is the only thing that builds the projection. Set it to the
    ///         frame's size when the tree has a <c>!TemporalAntialiasing</c> node in it and leave it at
    ///         zero otherwise — a jittered camera with nothing accumulating it is a frame that shakes
    ///         by half a pixel and buys nothing for it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Pixels, so that the offset means the same thing at every resolution.</b> The
    ///         sequence is in pixels and the matrix wants normalised device coordinates, which is
    ///         <c>2 × pixels / size</c> — and the frame's size is the only place that conversion can
    ///         honestly be done, because a camera does not know what it is being drawn into any more
    ///         than it knows its own aspect ratio. Hence the field beside
    ///         <see cref="AspectRatio" />, and the same reasoning.
    ///     </para>
    /// </remarks>
    public Int2 JitterTarget { get; set; }

    /// <summary>How many offsets the sequence uses before it repeats. Eight is the usual.</summary>
    /// <remarks>
    ///     ⚠ <b>A cycle rather than an ever-advancing sequence, and the difference shows up on a
    ///     still camera.</b> A history at <c>feedback: 0.9</c> holds roughly twenty frames; if every
    ///     one of those carried an offset the resolve had never seen before, the average never
    ///     reaches a fixed point and one-pixel geometry keeps flickering by a percent or so. Eight
    ///     repeating offsets converge to an exact answer, which is what a screenshot of a stationary
    ///     scene should be. Zero or less means "do not wrap", for a caller that wants the raw Halton.
    /// </remarks>
    public int JitterPeriod { get; set; } = 8;

    /// <summary>The offset the last extraction applied, in pixels.</summary>
    /// <remarks>Zero when <see cref="JitterTarget" /> is, which is how a diagnostic tells "no TAA in
    ///     this tree" from "TAA that is not being fed".</remarks>
    public Vector2 Jitter { get; private set; }

    /// <summary>How many jittered frames have been extracted, which indexes the sequence.</summary>
    int jitterFrame;

    /// <summary>Whether the last pass found a camera to render from.</summary>
    /// <remarks>
    ///     What says "the level has no camera in it" out loud. Without it the two failures that look
    ///     identical on screen — no camera, and a camera pointing at nothing — take the same afternoon
    ///     to tell apart.
    /// </remarks>
    public bool Found { get; private set; }

    /// <summary>How many cameras the last pass saw, of which one was used.</summary>
    public int CameraCount { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <see cref="LightExtractionSystem" /> gives:
    ///     naming a component type in a generic call is what assigns it an id, and on the first frame an
    ///     attribute would have nothing to look up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<WorldTransform>()
        .Read<Camera>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Extract(context.World);
        return dependency;
    }

    /// <summary>Points the view at the scene's camera.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The chosen camera's aspect ratio is zero and so is <see cref="AspectRatio" /> — neither the
    ///     camera nor the host said what shape the frame is.
    /// </exception>
    /// <remarks>Public so a test, a tool or an editor can aim a view without standing up a runner.</remarks>
    public void Extract(World world) {
        ArgumentNullException.ThrowIfNull(world);

        CameraCount = 0;
        Found = false;

        var chosen = default(Camera);
        var placement = default(WorldTransform);

        foreach (var chunk in world.Chunks(cameras)) {
            var authored = chunk.ReadValues<Camera>();
            var transforms = chunk.ReadValues<WorldTransform>();
            var entities = chunk.Entities;

            for (var i = 0; i < chunk.Count; i++) {
                CameraCount++;

                if (Found && authored[i].Order >= chosen.Order) {
                    continue;
                }

                chosen = authored[i];
                placement = transforms[i];
                Found = true;
            }
        }

        if (!Found) {
            return;
        }

        Apply(chosen, placement);
    }

    /// <summary>Writes one camera's placement into the view.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="RenderView.Camera" /> is set first and the matrix second, and the order is
    ///         load-bearing.</b> Assigning the camera sets the view's matrix from
    ///         <see cref="RenderCamera.ViewProjection" />, which is a <c>LookAt</c> — correct for a rigid
    ///         transform and not for one carrying scale or shear. The transform's own inverse is the
    ///         authority on where the camera is, so it is written afterwards and wins; the description is
    ///         still there for the shadow cascade fit, which needs a field of view a matrix cannot give
    ///         back.
    ///     </para>
    ///     <para>
    ///         An orthographic camera leaves <see cref="RenderView.Camera" /> null, because
    ///         <see cref="RenderCamera" /> describes a cone and an orthographic frustum is a box — see
    ///         its own remarks. Its shadows are fitted some other way, which is the case that type
    ///         already documents.
    ///     </para>
    /// </remarks>
    void Apply(in Camera camera, in WorldTransform placement) {
        var aspect = camera.AspectRatio > 0f ? camera.AspectRatio : AspectRatio;

        // ⚠ Before the matrix is replaced, so a motion-vector pass can ask where this pixel was. The
        // view holds one frame of history and nothing else does — the camera component is overwritten
        // by whatever moved it, and by the time extraction runs the old value is gone.
        View.Advance();

        var offset = NextJitter();

        View.Camera = camera.Orthographic
            ? null
            : new RenderCamera(
                placement.Position,
                Matrix4x4.TransformDirection(Vector3.Forward, placement.Value),
                Matrix4x4.TransformDirection(Vector3.UnitY, placement.Value),
                camera.FieldOfView,
                aspect,
                camera.NearPlane,
                camera.FarPlane
            ) {
                Lens = camera,
                Jitter = offset
            };

        Chosen = camera;
        View.Position = placement.Position;

        // ⚠ The same offset applied to the same frame's other matrix, and it must be the same one.
        // This matrix is built from the transform's inverse and `RenderCamera.Projection` from the
        // field of view; a screen-space pass inverts the second to unproject a depth buffer the first
        // rasterised. `CameraMath.Jittered` is what makes those two agree — see its remarks for why
        // jittering a projection and jittering a view-projection give the same answer.
        View.ViewProjection = CameraMath.Jittered(
            CameraMath.ViewProjection(in camera, in placement, AspectRatio),
            offset
        );

        // What turns an object's radius and distance into a fraction of the screen, which is what a LOD
        // threshold is authored against. Zero for an orthographic view rather than a wrong number: size
        // on screen there does not fall off with distance at all, so the whole expression a consumer
        // multiplies is the wrong shape — and zero is RenderView's documented "no screen-size work".
        View.ScreenHeightScale = camera.Orthographic ? 0f : 1f / MathF.Tan(camera.FieldOfView * 0.5f);
    }

    /// <summary>Takes the next offset off the sequence, in normalised device coordinates.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The counter only advances on a jittered frame</b>, so a tree with no temporal
    ///         resolve in it does not silently walk the sequence — and a frame that switches TAA on
    ///         starts at the beginning rather than at wherever the run happens to have got to. It is
    ///         also what makes a headless capture's offsets a function of the frame index alone.
    ///     </para>
    ///     <para>
    ///         <see cref="Jitter" /> is left in pixels because that is the unit a person reads; only
    ///         the matrix wants the doubled fraction of the target.
    ///     </para>
    /// </remarks>
    Vector2 NextJitter() {
        if (JitterTarget.X <= 0 || JitterTarget.Y <= 0) {
            Jitter = Vector2.Zero;
            return Vector2.Zero;
        }

        var index = JitterPeriod > 0 ? jitterFrame % JitterPeriod : jitterFrame;

        jitterFrame++;

        // Wrapped rather than left to overflow: a run long enough to reach int.MaxValue is a
        // dedicated server, and a negative index would hand Halton a loop that never terminates.
        if (jitterFrame < 0) {
            jitterFrame = 0;
        }

        Jitter = CameraMath.SubpixelJitter(index);

        return new(2f * Jitter.X / JitterTarget.X, 2f * Jitter.Y / JitterTarget.Y);
    }
}
