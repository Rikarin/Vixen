// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Engine.Scenes;
using Vixen.Physics.Characters;
using Vixen.Physics.Ecs;
using Vixen.Physics.Shapes;
using Xunit;

namespace Vixen.Physics.Tests;

/// <summary>That a subsystem's components reach the engine's registry without the engine knowing them.</summary>
/// <remarks>
///     <para>
///         The case that makes the arrangement worth having. <c>Vixen.Physics</c> declares
///         <see cref="Collider" /> and <see cref="RigidBody" /> and <c>Vixen.Engine</c> has never heard
///         of either; the generated <c>[ModuleInitializer]</c> in this assembly is the whole of the
///         connection, so a <c>.vxscene</c> can place a body and the inspector can draw one.
///     </para>
///     <para>
///         ⚠ <b>Each test touches the type before it asks about it, and that is the behaviour rather
///         than a workaround.</b> A module initializer runs when its assembly is loaded, so a component
///         nothing has referenced yet is not declared yet — which is why an editor has to load a
///         project's game assemblies eagerly to build a complete Add Component menu, and why a scene
///         naming a component from an assembly the game never touches fails at the load with
///         <c>SceneComponentException</c> rather than silently.
///     </para>
/// </remarks>
public sealed class PhysicsSceneComponentTests {
    [Fact]
    public void AColliderIsDeclaredByTheAssemblyThatOwnsIt() {
        _ = Collider.Of(new ShapeId(1));

        Assert.True(SceneComponentRegistry.TryGet(typeof(Collider), out var binder));
        Assert.Equal(typeof(Collider), binder.ComponentType);
    }

    [Fact]
    public void ARigidBodyIsDeclaredToo() {
        _ = RigidBody.Dynamic();

        Assert.True(SceneComponentRegistry.TryGet(typeof(RigidBody), out _));
    }

    /// <remarks>
    ///     The exclusion, from the other side of an assembly boundary. <c>PhysicsBody</c> carries
    ///     neither attribute, so nothing had to remember to keep the handle out of the scene format.
    /// </remarks>
    [Fact]
    public void TheBridgesOwnHandleIsNotDeclared() {
        _ = Collider.Of(new ShapeId(1));

        Assert.False(SceneComponentRegistry.TryGet(typeof(PhysicsBody), out _));
        Assert.False(SceneComponentRegistry.TryGet(typeof(PhysicsTeleport), out _));
    }

    /// <remarks>
    ///     ⚠ <b>A declared default crosses the same boundary the declaration does.</b> A zeroed
    ///     <see cref="CharacterMovement" /> has no speed, no gravity and no shape, so a character added
    ///     from the inspector stood still in mid-air — and the engine, which has never heard of this
    ///     type, is not where the fix could have gone.
    /// </remarks>
    [Fact]
    public void CharacterMovementCarriesItsDefaultAcrossTheBoundary() {
        _ = CharacterMovement.Default;

        Assert.True(SceneComponentRegistry.TryGet(typeof(CharacterMovement), out var binder));
        Assert.True(binder.HasDefault);

        var fresh = Assert.IsType<CharacterMovement>(binder.CreateDefault());

        Assert.Equal(CharacterMovement.Default.WalkSpeed, fresh.WalkSpeed);
        Assert.NotEqual(0f, fresh.Gravity);
    }

    /// <remarks>
    ///     The other half, in the assembly that would notice: a collider says nothing about a default
    ///     and is not made to.
    /// </remarks>
    [Fact]
    public void AComponentThatDeclaresNoDefaultSaysSo() {
        _ = Collider.Of(new ShapeId(1));

        Assert.True(SceneComponentRegistry.TryGet(typeof(Collider), out var binder));
        Assert.False(binder.HasDefault);
    }
}
