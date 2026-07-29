// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;

namespace Vixen.Editor.App;

/// <summary>One entity, as a row of editors and as something the gizmo can drag.</summary>
/// <remarks>
///     <para>
///         <b>The join, and the reason it lives in the application.</b> An entity is a handle and a
///         set of chunk rows; an inspector shows the members of an <i>object</i>. Something has to be
///         that object. Putting it here rather than in either library is what keeps
///         <c>Vixen.Editor.Inspector</c> from knowing what an ECS is and <c>Vixen.Editor.SceneView</c>
///         from knowing what a property drawer is — and it is the same class a game team would write
///         to put their own components in the inspector.
///     </para>
///     <para>
///         <b>It is also the gizmo's target</b>, deliberately. One type behind both means a drag and
///         a typed number cannot disagree about what "position" is, and the inspector following a
///         drag is <c>InspectorView.Reload</c> reading the same three properties the gizmo just wrote.
///     </para>
///     <para>
///         ⚠ <b>Nothing is cached. Every read goes to the chunk and every write goes to the chunk.</b>
///         Two of these over one entity therefore cannot disagree, and an entity a script moved
///         mid-drag is seen rather than overwritten. What it costs is a component lookup per property
///         access, which against a selection somebody is looking at is not a cost.
///     </para>
///     <para>
///         ⚠ <b>A dead entity reads as defaults and refuses writes.</b> A selection outlives the
///         thing it names — a play-mode restore reissues every handle — and an inspector that threw
///         while drawing a row would take the editor down for the rest of the session.
///     </para>
/// </remarks>
public sealed class SceneEntity {
    readonly SceneDocument document;

    /// <summary>The entity being shown.</summary>
    public Entity Entity { get; }

    /// <summary>Whether the entity is still there.</summary>
    public bool IsAlive => document.World.IsAlive(Entity);

    /// <summary>Views an entity.</summary>
    /// <param name="document">The scene it belongs to.</param>
    /// <param name="entity">The entity.</param>
    public SceneEntity(SceneDocument document, Entity entity) {
        ArgumentNullException.ThrowIfNull(document);

        this.document = document;
        Entity = entity;
    }

    /// <summary>What it is called.</summary>
    /// <remarks>
    ///     ⚠ <b><c>SetName</c> and not <c>Rename</c>, because this setter is already inside an undo
    ///     entry.</b> The inspector wraps every write in a <c>SetMembersCommand</c>; a setter that
    ///     recorded a <c>RenameEntityCommand</c> as well put two entries on the stack for one edit —
    ///     and, worse, pushed the second from inside the first. Undoing then ran this setter again,
    ///     which asked the stack to execute during an undo, which the stack refuses: the entry came
    ///     off the history and the name did not move. "Ctrl+Z removes the step and the value stays"
    ///     is that, exactly. The outliner's inline editor still goes through <c>Rename</c> and is
    ///     still one step.
    /// </remarks>
    [Inspector]
    [Tooltip("What this entity is called. The editor keeps names; the runtime does not.")]
    public string Name {
        get => document.NameOf(Entity);
        set => document.SetName(Entity, string.IsNullOrWhiteSpace(value) ? "Entity" : value);
    }

    /// <summary>Where it is, in world space.</summary>
    [Inspector]
    [Header("Transform")]
    public Vector3 Position {
        get => IsAlive ? new Transform(document.World, Entity).Position : Vector3.Zero;

        set {
            if (IsAlive) {
                new Transform(document.World, Entity).Position = value;
            }
        }
    }

    /// <summary>How it is turned, in world space.</summary>
    /// <remarks>Shown as three angles — see <c>EulerAngles</c> for what that costs and why.</remarks>
    [Inspector]
    public Quaternion Rotation {
        get => IsAlive ? new Transform(document.World, Entity).Rotation : Quaternion.Identity;

        set {
            if (IsAlive) {
                new Transform(document.World, Entity).Rotation = value;
            }
        }
    }

    /// <summary>How big it is, relative to its parent.</summary>
    [Inspector]
    public Vector3 Scale {
        get => IsAlive ? new Transform(document.World, Entity).LocalScale : Vector3.One;

        set {
            if (IsAlive) {
                new Transform(document.World, Entity).LocalScale = value;
            }
        }
    }

    /// <summary>Renders it as its name.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;
}
