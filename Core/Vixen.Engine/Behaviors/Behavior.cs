// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Coroutines;
using Vixen.Engine.Transforms;

namespace Vixen.Engine.Behaviors;

/// <summary>
///     The MonoBehaviour-shaped API: a class attached to an entity with lifecycle callbacks.
/// </summary>
/// <remarks>
///     <para>
///         This is the surface most users touch, and it is an archetype ECS underneath. A behaviour
///         holds no component data — it reaches its entity's components through
///         <see cref="Get{T}" />, which is a chunk row, not a field on this object.
///     </para>
///     <para>
///         <b>The rule that keeps this coherent</b> (from
///         [04](../../../docs/plan/04-ecs-and-scripting.md)): component data lives in the ECS, and a
///         behaviour holds nothing that is not either a component or private scratch. A behaviour
///         that keeps a <c>List&lt;Entity&gt;</c> of "its children" has made the ECS below it
///         decoration; it asks the hierarchy instead.
///     </para>
///     <para>
///         The callbacks are <c>protected</c>, and the store reaches them through one-line internal
///         bridges rather than by widening them. <c>protected internal</c> would have done, right up
///         to the point where an assembly with <c>InternalsVisibleTo</c> — the test project, an
///         editor — has to write <c>protected internal override</c> while everyone else writes
///         <c>protected override</c>. A callback whose required modifier depends on who is looking
///         is not an API anyone should have to remember.
///     </para>
/// </remarks>
public abstract partial class Behavior : ICoroutineOwner {
    bool enabled = true;

    /// <summary>The entity this is attached to.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not this behaviour's data, so a <c>[DataContract]</c> subclass does not write
    ///         it.</b> The serialization generator walks base types and takes every public member it
    ///         can set — which without this would put the store's own plumbing, and the entity's
    ///         transform, into every serialised behaviour. See <see cref="Behavior" />'s remarks on
    ///         what a behaviour is allowed to hold.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And <c>[EditorVisible(false)]</c> beside it, because the two answer different
    ///         questions.</b> <c>TypeDescriptorGenerator</c> says so out loud: a member is shown
    ///         unless something hides it, and being left out of the file is not being left out of the
    ///         panel. Here the answer happens to be the same — an inspector drawing a behaviour's
    ///         <c>World</c>, or a second copy of the entity's position, is showing plumbing.
    ///         <see cref="Enabled" /> is the deliberate exception: it is the checkbox on the
    ///         foldout, and it is the one piece of this that an author does decide.
    ///     </para>
    /// </remarks>
    [DataMemberIgnore]
    [EditorVisible(false)]
    public Entity Entity { get; internal set; }

    /// <summary>The world the entity lives in.</summary>
    [DataMemberIgnore]
    [EditorVisible(false)]
    public World World { get; internal set; } = null!;

    /// <summary>Whether the behaviour has been destroyed, or its entity has.</summary>
    [DataMemberIgnore]
    [EditorVisible(false)]
    public bool IsDestroyed { get; internal set; }

    /// <summary>
    ///     Whether <c>Update</c> runs. Changing it queues <c>OnEnable</c> or <c>OnDisable</c> for the
    ///     next lifecycle drain rather than calling it here.
    /// </summary>
    /// <remarks>
    ///     Deferred because a behaviour that disables another one from inside its own <c>Update</c>
    ///     would otherwise run a callback in the middle of a loop over the batch it is in — which is
    ///     the same class of problem structural change during iteration is, and gets the same answer.
    /// </remarks>
    public bool Enabled {
        get => enabled;

        set {
            if (enabled == value || IsDestroyed) {
                return;
            }

            enabled = value;
            Store?.QueueEnabledChange(this, value);
        }
    }

    /// <summary>Whether the behaviour has had <c>Awake</c> called.</summary>
    [DataMemberIgnore]
    [EditorVisible(false)]
    public bool IsAwake { get; internal set; }

    /// <summary>Whether the behaviour has had <c>Start</c> called.</summary>
    [DataMemberIgnore]
    [EditorVisible(false)]
    public bool IsStarted { get; internal set; }

    internal BehaviorStore? Store { get; set; }

    /// <summary>
    ///     Which bucket this is in — the static type at the <c>Add&lt;T&gt;</c> call site, not
    ///     necessarily the runtime type.
    /// </summary>
    /// <remarks>
    ///     <c>Add(entity, new PlayerController())</c> infers <c>PlayerController</c> and gets a
    ///     bucket of exactly that, which is the point. <c>Add&lt;Behavior&gt;(entity, derived)</c>
    ///     buckets under <c>Behavior</c> and gives up the monomorphic loop — correct, slower, and
    ///     the caller's choice. What must not happen is adding under one key and removing under
    ///     another, which is why this is recorded rather than re-derived from <c>GetType()</c>.
    /// </remarks>
    internal Type BucketKey { get; set; } = typeof(Behavior);

    /// <summary>Its index in the bucket's array. Moves as the bucket partitions and compacts.</summary>
    internal int Slot { get; set; }

    /// <summary>The clock for the pass this behaviour is running in.</summary>
    /// <remarks>
    ///     Inside <c>LateUpdate</c> and <c>Update</c> this is the frame's; a behaviour does not run
    ///     in <c>SystemPhase.FixedUpdate</c>, so it never sees a fixed step. Work that must be a
    ///     function of the fixed step belongs in an <c>ISystem</c>, which is the trade doc 04 states.
    /// </remarks>
    [DataMemberIgnore]
    [EditorVisible(false)]
    public GameTime Time => Store?.Time ?? GameTime.Zero;

    /// <summary>A façade over the entity's transform.</summary>
    /// <returns>The façade.</returns>
    /// <exception cref="ComponentNotFoundException">The entity has no transform.</exception>
    /// <remarks>
    ///     Assigning through it needs a local — <c>var t = Transform; t.Position = …;</c> — because a
    ///     property of a value returned from another property is not a variable (CS1612). The four
    ///     properties below exist so the common cases do not have to think about that.
    /// </remarks>
    [DataMemberIgnore]
    [EditorVisible(false)]
    public Transform Transform => new(World, Entity);

    /// <summary>Position in world space.</summary>
    [DataMemberIgnore]
    [EditorVisible(false)]
    public Vector3 Position {
        get => Transform.Position;

        set {
            var transform = Transform;
            transform.Position = value;
        }
    }

    /// <summary>Position relative to the parent.</summary>
    [DataMemberIgnore]
    [EditorVisible(false)]
    public Vector3 LocalPosition {
        get => Transform.LocalPosition;

        set {
            var transform = Transform;
            transform.LocalPosition = value;
        }
    }

    /// <summary>Rotation in world space.</summary>
    [DataMemberIgnore]
    [EditorVisible(false)]
    public Quaternion Rotation {
        get => Transform.Rotation;

        set {
            var transform = Transform;
            transform.Rotation = value;
        }
    }

    /// <summary>Rotation relative to the parent.</summary>
    [DataMemberIgnore]
    [EditorVisible(false)]
    public Quaternion LocalRotation {
        get => Transform.LocalRotation;

        set {
            var transform = Transform;
            transform.LocalRotation = value;
        }
    }

    /// <summary>Called once, when the behaviour is attached, before anything else.</summary>
    protected virtual void Awake() { }

    internal void InvokeAwake() => Awake();

    /// <summary>Called after <c>Awake</c>, and whenever the behaviour is enabled again.</summary>
    protected virtual void OnEnable() { }

    internal void InvokeOnEnable() => OnEnable();

    /// <summary>Called once, the frame after <c>Awake</c>, before the first <c>Update</c>.</summary>
    protected virtual void Start() { }

    internal void InvokeStart() => Start();

    /// <summary>Called every frame in <c>SystemPhase.Update</c>, while enabled.</summary>
    protected virtual void Update() { }

    internal void InvokeUpdate() => Update();

    /// <summary>Called every frame in <c>SystemPhase.LateUpdate</c>, while enabled.</summary>
    protected virtual void LateUpdate() { }

    internal void InvokeLateUpdate() => LateUpdate();

    /// <summary>Called when the behaviour is disabled, and before <c>OnDestroy</c> if it was enabled.</summary>
    protected virtual void OnDisable() { }

    internal void InvokeOnDisable() => OnDisable();

    /// <summary>Called once, when the behaviour or its entity is destroyed.</summary>
    protected virtual void OnDestroy() { }

    internal void InvokeOnDestroy() => OnDestroy();

    /// <summary>A reference to one of the entity's components, for writing.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>A reference into the chunk.</returns>
    public ref T Get<T>() => ref World.Get<T>(Entity);

    /// <summary>A reference to one of the entity's components, for reading.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>A read-only reference into the chunk.</returns>
    public ref readonly T Read<T>() => ref World.Read<T>(Entity);

    /// <summary>Whether the entity has a component.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>Whether it has one.</returns>
    public bool Has<T>() => World.Has<T>(Entity);

    /// <summary>Another behaviour on the same entity, if there is one.</summary>
    /// <typeparam name="T">The behaviour type.</typeparam>
    /// <returns>It, or <see langword="null" />.</returns>
    public T? GetBehavior<T>() where T : Behavior => Store?.Get<T>(Entity);

    /// <summary>Destroys this behaviour, running <c>OnDisable</c> and <c>OnDestroy</c> at the next drain.</summary>
    public void Destroy() => Store?.Destroy(this);
}
