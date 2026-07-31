// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
            );

        View.Position = placement.Position;
        View.ViewProjection = CameraMath.ViewProjection(in camera, in placement, AspectRatio);

        // What turns an object's radius and distance into a fraction of the screen, which is what a LOD
        // threshold is authored against. Zero for an orthographic view rather than a wrong number: size
        // on screen there does not fall off with distance at all, so the whole expression a consumer
        // multiplies is the wrong shape — and zero is RenderView's documented "no screen-size work".
        View.ScreenHeightScale = camera.Orthographic ? 0f : 1f / MathF.Tan(camera.FieldOfView * 0.5f);
    }
}
