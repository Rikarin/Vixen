// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Net.Engine.Players;

/// <summary>Marks a body a player may be given. Placed on the prefab, never on the wire.</summary>
/// <remarks>
///     <para>
///         <b>It costs nothing to replicate because it is not replicated.</b> A networked prefab is
///         instantiated from the same content on both ends, so a tag authored on it is present on the
///         client the moment <c>NetworkSpawnSystem</c> builds the instance — which is what lets a
///         client work out which of the things it owns is the one it drives without being told, and
///         without a byte of wire spent saying so.
///     </para>
///     <para>
///         ⚠ <b><c>[Component]</c> and <c>[DataContract]</c>, deliberately both.</b> Unlike the
///         possession edge, this carries no handle and no derived state: it is an authored fact about
///         a prefab, so a prefab and a scene must both be able to say it.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct PlayerPawn : ITagComponent;

/// <summary>Hides a rollback by letting an entity's visuals lag the body they hang from.</summary>
/// <remarks>
///     <para>
///         <b>On a child, and that is the whole of the design.</b> A correction moves the predicted
///         body at once, because the simulation has to be right; what a player sees may lag and catch
///         up. So the offset goes on a visual child — the mesh, the skeleton root — which the
///         transform hierarchy composes and which nothing simulates. Unreal does exactly this in
///         <c>SmoothClientPosition</c>: it offsets the mesh component and never the capsule.
///     </para>
///     <para>
///         ⚠ <b>Writing the offset onto the body instead would feed it back into the simulation.</b>
///         <c>PhysicsScene</c> adopts a written <c>LocalTransform</c> as a teleport — that is what
///         makes a rollback reach a <c>CharacterController</c> at all — so a smoothed body position
///         would be taken as the truth on the next fixed step, and the error the smoothing was hiding
///         would become an error the simulation had made. The correction would then never settle.
///     </para>
///     <para>
///         Not <c>[DataContract]</c>, because it names an entity. A prefab therefore carries the mesh
///         as a child and something running in the world says which body it belongs to — the line
///         <c>CameraTargets</c> and <c>Possessing</c> are already on.
///     </para>
/// </remarks>
[Component]
public struct PredictionSmoothing {
    /// <summary>The predicted entity whose correction this hides. Usually the parent.</summary>
    public Entity Body;

    /// <summary>Where the visuals sit when nothing is being smoothed, in the parent's frame.</summary>
    /// <remarks>
    ///     Held rather than read once, because the system overwrites the local position every frame
    ///     and would otherwise have no rest position to return to — the same reason
    ///     <c>PhysicsInterpolation</c> keeps both of the poses it lerps between.
    /// </remarks>
    public Vector3 Rest;

    /// <summary>Visuals that sit on their body, smoothed when it is corrected.</summary>
    /// <param name="body">The predicted entity.</param>
    /// <returns>The component.</returns>
    public static PredictionSmoothing Of(Entity body) => new() { Body = body, Rest = Vector3.Zero };
}
