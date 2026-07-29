// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Rendering.Features;

namespace Vixen.Rendering.Ecs;

/// <summary>Turns the scene's <see cref="Light" /> entities into the lighting feature's frame list.</summary>
/// <remarks>
///     <para>
///         <b>The bridge, and it is a copy rather than a translation.</b> <see cref="Light" /> holds
///         what a light <i>is</i> and <see cref="RenderLight" /> holds what the shader needs — the same
///         fields, plus the position and the basis folded in from the transform. That the two line up
///         is deliberate: a bridge that had to reinterpret values would be a place for the authored
///         meaning and the rendered one to drift apart.
///     </para>
///     <para>
///         <b>Rebuilt every frame rather than mirrored.</b> A light has no handle to keep, so there is
///         nothing to reconcile: the list is cleared and refilled, which costs a walk over the handful
///         of entities that emit light and removes the entire class of bug where a destroyed entity
///         leaves a light burning. That is the opposite trade from <c>PhysicsBody</c>, where the handle
///         is expensive to create and is therefore kept — and the difference is exactly that a body is
///         state and a light is not.
///     </para>
///     <para>
///         <b>In <see cref="SystemPhase.PreRender" />, ordered by its declared access.</b>
///         <c>TransformSystem</c> writes <see cref="WorldTransform" /> in the same phase and this reads
///         it, so the dependency graph puts this second — which is what makes a light's position this
///         frame's rather than last frame's. Naming the phase alone would not have been enough.
///     </para>
///     <para>
///         ⚠ <b>The transform supplies position and orientation, and the component supplies neither.</b>
///         A directional light has no position and reads only its forward axis; a point light has no
///         orientation and reads only its position; the two area kinds need a whole basis. Every one of
///         them takes it from <see cref="WorldTransform" />, so a light is aimed with the rotate gizmo
///         and a scene file cannot say two different things about where one points.
///     </para>
/// </remarks>
/// <param name="feature">The lighting feature whose list to fill.</param>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class LightExtractionSystem(ForwardLightingRenderFeature feature) : SystemBase, IDeclaredAccess {
    readonly QueryDescription lights = new QueryDescription().WithAll<Light, WorldTransform>();

    /// <summary>How many lights the last pass put in the list.</summary>
    /// <remarks>
    ///     Worth having as a number somebody can look at: "the scene is dark" and "the scene has no
    ///     lights in it" are different problems, and the second one is answered here rather than by
    ///     reading a GPU buffer.
    /// </remarks>
    public int LightCount { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Declared here rather than with attributes, for the reason <c>AudioSystem</c> gives: naming a
    ///     component type in a generic call is what assigns it an id, and an attribute can only look
    ///     one up — on the first frame there is nothing to look up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<WorldTransform>()
        .Read<Light>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Extract(context.World);
        return dependency;
    }

    /// <summary>Refills the lighting feature's list from the world.</summary>
    /// <param name="world">The world.</param>
    /// <remarks>Public so a test, a tool or an editor can light a scene without standing up a runner.</remarks>
    public void Extract(World world) {
        ArgumentNullException.ThrowIfNull(world);

        feature.Lights.Clear();
        LightCount = 0;

        foreach (var chunk in world.Chunks(lights)) {
            var authored = chunk.ReadValues<Light>();
            var transforms = chunk.ReadValues<WorldTransform>();

            for (var i = 0; i < chunk.Count; i++) {
                feature.Lights.Add(Convert(authored[i], transforms[i].Value));
                LightCount++;
            }
        }
    }

    /// <summary>One authored light, in the form the lighting feature reads.</summary>
    /// <param name="light">The authored light.</param>
    /// <param name="matrix">Its local-to-world transform.</param>
    /// <returns>The frame's light.</returns>
    /// <remarks>
    ///     ⚠ <b>Static and pure, which is what makes the mapping testable without a world.</b> Every
    ///     interesting question about this bridge — does a rotated spot point where the gizmo drew it,
    ///     does a rect's basis stay orthogonal — is a question about this function.
    /// </remarks>
    public static RenderLight Convert(Light light, in Matrix4x4 matrix) {
        var position = matrix.Translation;

        // The same basis the scene view's gizmos draw with: -Z forward, +X right, +Y up. A light that
        // rendered along a different axis from the cone somebody aimed would be worse than one that
        // did not render.
        var forward = Vector3.Normalize(Matrix4x4.TransformDirection(Vector3.Forward, matrix));

        return light.Kind switch {
            LightKind.Directional => RenderLight.Directional(forward, light.Colour, light.Intensity),

            LightKind.Spot => RenderLight.Spot(
                position,
                forward,
                light.Range,
                light.InnerAngle,
                light.OuterAngle,
                light.Colour,
                light.Intensity
            ),

            // A tube runs along its own +X, so scaling an entity along X does not stretch the light —
            // HalfLength is the authored length and the transform supplies only the direction. A tube
            // that grew with the gizmo would make "how long is this light" a question with two answers.
            LightKind.Tube => RenderLight.Tube(
                position,
                Vector3.Normalize(Matrix4x4.TransformDirection(Vector3.UnitX, matrix)),
                light.HalfLength,
                light.Radius,
                light.Range,
                light.Colour,
                light.Intensity
            ),

            // Emitting along -Z with its width across +X, so a rect faces the way every other
            // directed thing in the engine faces and `Rect` re-orthogonalises the pair for the GPU.
            LightKind.Rect => RenderLight.Rect(
                position,
                forward,
                Vector3.Normalize(Matrix4x4.TransformDirection(Vector3.UnitX, matrix)),
                light.HalfLength,
                light.Radius,
                light.Range,
                light.Colour,
                light.Intensity
            ),

            // Point, and anything a newer editor invented: a kind this build does not know still has a
            // position, a colour and a range, so it lights the scene as a point rather than not at all.
            _ => RenderLight.Point(position, light.Range, light.Colour, light.Intensity)
        };
    }
}
