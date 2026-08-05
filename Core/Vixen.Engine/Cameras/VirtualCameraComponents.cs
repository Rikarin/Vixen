// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Engine.Cameras;

/// <summary>What a shot asks the lens to be, and the part of a camera that can be blended.</summary>
/// <remarks>
///     <para>
///         <b>Not a <see cref="Camera" />, though it carries most of the same numbers.</b> A
///         <see cref="Camera" /> also says which order it renders in and what aspect ratio it wants,
///         and neither of those is a property of a <i>shot</i> — two shots blending into each other
///         cannot each have an opinion about render order. What is left is the optics, plus the one
///         thing a camera component has no reason to carry: <see cref="Dutch" />.
///     </para>
///     <para>
///         ⚠ <b>The live shot's lens is copied onto the camera entity every frame.</b> A field of
///         view typed into the <see cref="Camera" /> component of an entity that a
///         <see cref="CameraDirector" /> is driving will be overwritten before it is ever used —
///         which is the same bargain Cinemachine strikes, and the reason the lens is authored on the
///         shot instead. <see cref="CameraDirector.WriteLens" /> turns the copy off for a game that
///         wants to own its field of view.
///     </para>
/// </remarks>
[DataContract]
public struct CameraLens {
    /// <summary>Vertical field of view, in radians. Ignored when <see cref="Orthographic" />.</summary>
    public float FieldOfView;

    /// <summary>Distance to the near plane.</summary>
    public float NearPlane;

    /// <summary>Distance to the far plane.</summary>
    public float FarPlane;

    /// <summary>Whether the projection is orthographic.</summary>
    public bool Orthographic;

    /// <summary>The height the orthographic view covers, in world units.</summary>
    public float OrthographicHeight;

    /// <summary>
    ///     Roll about the view axis, in radians — the tilted horizon a cinematographer calls a Dutch
    ///     angle.
    /// </summary>
    /// <remarks>
    ///     Held on the lens rather than folded into the shot's rotation on purpose. Every aim stage
    ///     keeps the horizon level and damps towards a level target; a roll mixed into that rotation
    ///     would be something the aim then spends the next second undoing. It is applied once, at the
    ///     end, to the composed output.
    /// </remarks>
    public float Dutch;

    /// <summary>A sensible perspective lens: 60° vertical, from 0.1 to 1000, no roll.</summary>
    /// <remarks>
    ///     A property rather than a <c>default</c>, for the reason <see cref="Camera.Perspective" />
    ///     gives: a zeroed lens has a zero field of view and a zero far plane, and every matrix built
    ///     from it is degenerate.
    /// </remarks>
    public static CameraLens Default => new() {
        FieldOfView = MathUtil.DegreesToRadians(60f),
        NearPlane = 0.1f,
        FarPlane = 1000f,
        Orthographic = false,
        OrthographicHeight = 10f,
        Dutch = 0f
    };

    /// <summary>The lens an existing camera is using.</summary>
    /// <param name="camera">The camera.</param>
    /// <returns>Its optics, with no roll.</returns>
    public static CameraLens From(in Camera camera) => new() {
        FieldOfView = camera.FieldOfView,
        NearPlane = camera.NearPlane,
        FarPlane = camera.FarPlane,
        Orthographic = camera.Orthographic,
        OrthographicHeight = camera.OrthographicHeight,
        Dutch = 0f
    };

    /// <summary>Copies the optics onto a camera, leaving its aspect ratio and order alone.</summary>
    /// <param name="camera">The camera to write.</param>
    public readonly void ApplyTo(ref Camera camera) {
        camera.FieldOfView = FieldOfView;
        camera.NearPlane = NearPlane;
        camera.FarPlane = FarPlane;
        camera.Orthographic = Orthographic;
        camera.OrthographicHeight = OrthographicHeight;
    }

    /// <summary>Mixes two lenses.</summary>
    /// <param name="from">The lens being left.</param>
    /// <param name="to">The lens being arrived at.</param>
    /// <param name="amount">How far along, from 0 to 1.</param>
    /// <returns>The mixed lens.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="Orthographic" /> cuts at the halfway point rather than mixing</b>, because
    ///     there is no projection halfway between a perspective one and an orthographic one — the
    ///     two are not the same family of matrix, and interpolating the numbers would give a picture
    ///     that is neither. A blend between the two modes is a cut wearing a blend's clothes, and it
    ///     is better that it looks like one at a predictable moment than that it produces a frame
    ///     nobody can describe.
    /// </remarks>
    public static CameraLens Blend(in CameraLens from, in CameraLens to, float amount) => new() {
        FieldOfView = MathUtil.Lerp(from.FieldOfView, to.FieldOfView, amount),
        NearPlane = MathUtil.Lerp(from.NearPlane, to.NearPlane, amount),
        FarPlane = MathUtil.Lerp(from.FarPlane, to.FarPlane, amount),
        Orthographic = amount < 0.5f ? from.Orthographic : to.Orthographic,
        OrthographicHeight = MathUtil.Lerp(from.OrthographicHeight, to.OrthographicHeight, amount),
        Dutch = MathUtil.Lerp(from.Dutch, to.Dutch, amount)
    };
}

/// <summary>
///     Where a virtual camera would put the real one this frame, if it were the one being watched.
/// </summary>
/// <remarks>
///     <para>
///         <b>The stages write it in turn and it is the only thing they share.</b> A body decides
///         <see cref="Position" />, an aim decides <see cref="Rotation" />, an extension corrects
///         both, and noise adds a shake that the next frame's damping must not see — so the shake is
///         held apart in <see cref="ShakePosition" /> and <see cref="ShakeRotation" /> and folded in
///         only by <see cref="Composed" />. Feeding a shake back into a damped position produces a
///         camera that chases its own jitter, which is a bug that looks like a physics problem.
///     </para>
///     <para>
///         <b>Not <c>[DataContract]</c>, so no scene can carry one.</b> It is derived every frame
///         from components that <i>are</i> authored, and a file that recorded a half-damped position
///         would reload into the middle of a motion nobody asked for. The same argument
///         <c>WorldTransform</c> makes.
///     </para>
///     <para>
///         The engine attaches it. A shot placed by a scene or created by hand is given one by
///         <c>VirtualCameraSystem</c> on the frame after it appears, so nothing has to remember to.
///     </para>
/// </remarks>
[Component]
public struct CameraShot {
    /// <summary>Where the camera would be, in world space, before any shake.</summary>
    public Vector3 Position;

    /// <summary>Which way it would look, before any shake and before <see cref="CameraLens.Dutch" />.</summary>
    public Quaternion Rotation;

    /// <summary>The optics it asks for.</summary>
    public CameraLens Lens;

    /// <summary>This frame's positional shake, in camera space. Cleared and rebuilt every frame.</summary>
    public Vector3 ShakePosition;

    /// <summary>This frame's rotational shake, in camera space. Cleared and rebuilt every frame.</summary>
    public Quaternion ShakeRotation;

    /// <summary>
    ///     Whether the shot has been evaluated before. False makes every stage snap rather than damp.
    /// </summary>
    /// <remarks>
    ///     A camera that eased in from wherever a zeroed struct happens to be would spend its first
    ///     second flying in from the origin, once, invisibly in most scenes and unmistakably in the
    ///     one where the origin is off the map. Damping needs a previous state, and this is the flag
    ///     that says whether there is one.
    /// </remarks>
    public bool HasHistory;

    /// <summary>The shot as it would actually be rendered: shake folded in, Dutch angle applied.</summary>
    /// <param name="position">Where the camera goes.</param>
    /// <param name="rotation">Which way it points.</param>
    /// <remarks>
    ///     The shake is in camera space, so a half-metre of horizontal noise is half a metre across
    ///     the frame whichever way the camera happens to be facing — which is what a handheld
    ///     operator's wobble is, and is not what a world-space offset would give.
    /// </remarks>
    public readonly void Composed(out Vector3 position, out Quaternion rotation) {
        var shake = ShakeRotation.LengthSquared() > 0f ? ShakeRotation : Quaternion.Identity;

        position = Position + Quaternion.Transform(ShakePosition, Rotation);
        rotation = shake * Rotation;

        if (Lens.Dutch != 0f) {
            // A roll about the camera's own view axis, applied before the orientation that put the
            // axis where it is — which is what makes it a roll rather than a yaw with extra steps.
            rotation = Quaternion.FromAxisAngle(Vector3.UnitZ, Lens.Dutch) * rotation;
        }
    }

    /// <summary>A shot at a place, looking a way, through the default lens.</summary>
    /// <param name="position">Where.</param>
    /// <param name="rotation">Which way.</param>
    /// <returns>The shot, with no history, so its first evaluation snaps.</returns>
    public static CameraShot At(Vector3 position, Quaternion rotation) => new() {
        Position = position,
        Rotation = rotation,
        Lens = CameraLens.Default,
        ShakePosition = Vector3.Zero,
        ShakeRotation = Quaternion.Identity,
        HasHistory = false
    };
}

/// <summary>
///     A shot: somewhere the camera could be, with a lens and a claim on being the one that renders.
/// </summary>
/// <remarks>
///     <para>
///         <b>A virtual camera is not a camera.</b> It renders nothing and owns no target. It
///         describes a point of view, and a <see cref="CameraDirector" /> picks one of them each
///         frame and moves the single real <see cref="Camera" /> to it, blending when the choice
///         changes. That indirection is the whole idea: a level can hold twenty framed shots, each
///         set up once and left alone, and the cuts between them are a matter of which one currently
///         has the highest <see cref="Priority" /> rather than of code that moves a camera about.
///     </para>
///     <para>
///         <b>What it does is decided by which stage components sit beside it.</b> A body — one of
///         <see cref="FollowBody" />, <see cref="FramingBody" />, <see cref="OrbitBody" />,
///         <see cref="HardLockBody" /> — decides where it is; an aim —
///         <see cref="ComposerAim" />, <see cref="HardLookAim" />, <see cref="PovAim" />,
///         <see cref="MatchTargetAim" /> — decides where it looks. Neither is required: a shot with
///         no body and no aim sits exactly where its entity's transform puts it, which is what a
///         hand-placed establishing shot wants.
///     </para>
///     <para>
///         ⚠ <b>Two bodies, or two aims, on one shot is a configuration error the engine cannot
///         report.</b> The stages are separate chunk sweeps and they run in a fixed order, so the
///         last one to run wins and the other silently does nothing. It is checked by
///         <c>VirtualCameras.Validate</c>, which the editor's inspector and a test can call and the
///         frame loop deliberately does not — a per-frame archetype interrogation to catch a mistake
///         that is made once at authoring time is the wrong place to spend the budget.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct VirtualCamera : IDefaultComponent<VirtualCamera> {
    /// <summary>
    ///     Which shot wins. The enabled shot with the highest priority is the one the director drives
    ///     towards.
    /// </summary>
    public int Priority;

    /// <summary>Whether this shot is a candidate at all.</summary>
    /// <remarks>
    ///     The ECS has no notion of a disabled entity, so this is a field rather than an archetype
    ///     question. It is the switch a trigger volume flips to hand the camera to the next room.
    /// </remarks>
    public bool Enabled;

    /// <summary>The optics this shot asks for.</summary>
    public CameraLens Lens;

    /// <summary>Which director this shot answers to.</summary>
    /// <remarks>
    ///     A director only ever considers the shots on its own channel. One game in one window
    ///     leaves every one of them at zero and never thinks about it; a split-screen game gives each
    ///     player a channel, and the alternative — one set of shots and a rule about who may see
    ///     which — is how two players end up sharing a camera the moment one of them picks up a
    ///     higher-priority trigger.
    /// </remarks>
    public int Channel;

    /// <summary>An enabled shot at priority zero, through the default lens.</summary>
    /// <remarks>
    ///     A property rather than a <c>default</c>, and for a sharper reason than
    ///     <see cref="Camera.Perspective" /> has: a zeroed <see cref="VirtualCamera" /> is
    ///     <i>disabled</i> as well as degenerate, so a shot added with <c>default</c> would not
    ///     render and would not be visibly broken either. Everything that makes one starts here — and
    ///     <see cref="IDefaultComponent{TSelf}" /> is what makes the editor's Add Component one of
    ///     them, which for years it was not.
    /// </remarks>
    public static VirtualCamera Default => new() {
        Priority = 0,
        Enabled = true,
        Lens = CameraLens.Default,
        Channel = 0
    };

    /// <inheritdoc />
    static VirtualCamera IDefaultComponent<VirtualCamera>.DefaultValue => Default;
}

/// <summary>What a shot follows and what it looks at.</summary>
/// <remarks>
///     <para>
///         <b>Two targets, because they are two questions.</b> A third-person camera follows the
///         player and looks at a point above their head; a boss-fight camera follows a fixed rail and
///         looks at the boss; a security camera follows nothing and looks at whoever walks in. Body
///         stages read <see cref="Follow" /> and aim stages read <see cref="LookAt" />, and
///         <see cref="Both" /> is the common case where they are the same entity.
///     </para>
///     <para>
///         ⚠ <b>Not <c>[DataContract]</c>, so a scene cannot author it.</b> An entity handle names a
///         slot in the world that issued it and means nothing in another one — the line
///         <c>PhysicsBody</c> is already on. A level therefore places its shots, their priorities and
///         their lenses, and something running in the world wires up what they point at. That is
///         where most games put it anyway, because the thing being followed is usually spawned; the
///         case it costs is a cutscene camera framing a prop that the same scene placed, and undoing
///         that needs entity references in the compiled scene format, which do not exist yet.
///     </para>
/// </remarks>
[Component]
public struct CameraTargets {
    /// <summary>What the body stage positions the shot relative to.</summary>
    public Entity Follow;

    /// <summary>What the aim stage points the shot at.</summary>
    public Entity LookAt;

    /// <summary>Both targets, set to the same entity.</summary>
    /// <param name="entity">The entity to follow and look at.</param>
    /// <returns>The targets.</returns>
    public static CameraTargets Both(Entity entity) => new() { Follow = entity, LookAt = entity };
}

