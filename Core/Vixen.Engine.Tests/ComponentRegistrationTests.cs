// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Engine.Cameras;
using Vixen.Engine.Scenes;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>That the generated declaration puts the right components in the registry, and only those.</summary>
/// <remarks>The components themselves are in <c>RegistrationComponents.cs</c>.</remarks>
public sealed class ComponentRegistrationTests {
    [Fact]
    public void AComponentWithBothAttributesIsRegisteredWithoutBeingAskedFor() {
        Assert.True(SceneComponentRegistry.TryGet(typeof(Shield), out var binder));
        Assert.Equal("RegistrationTestShield", binder.Name);
        Assert.Equal(typeof(Shield), binder.ComponentType);
    }

    [Fact]
    public void ATagIsRegisteredAndKnowsItIsOne() {
        Assert.True(SceneComponentRegistry.TryGet(typeof(Warded), out var binder));
        Assert.True(binder.IsTag);
    }

    /// <remarks>
    ///     The exclusion that makes the rule a conjunction rather than a marker of its own. A handle
    ///     registered here would be saved into a scene and restored pointing at whatever took its slot.
    /// </remarks>
    [Fact]
    public void AHandleComponentIsNotRegistered() {
        Assert.False(SceneComponentRegistry.TryGet(typeof(RegistrationTestHandle), out _));
    }

    [Fact]
    public void DescribedDataThatIsNotAComponentIsNotRegistered() {
        Assert.False(SceneComponentRegistry.TryGet(typeof(RegistrationTestSettings), out _));
    }

    /// <remarks>
    ///     <c>Camera</c> was registered by the registry's own static constructor until the attributes
    ///     took the job over. This is the assertion that the handover lost nothing — and that the
    ///     engine's own components now arrive by exactly the route a game's do.
    /// </remarks>
    [Fact]
    public void TheEnginesOwnComponentsArriveTheSameWay() {
        Assert.True(SceneComponentRegistry.TryGet(typeof(Camera), out var binder));
        Assert.Equal("Camera", binder.Name);
    }

    /// <remarks>
    ///     A name is what a compiled scene carries, so the lookup by name and the lookup by type have
    ///     to agree — a declaration that populated one and not the other would load scenes it could
    ///     not save.
    /// </remarks>
    [Fact]
    public void ADeclaredComponentIsFoundByNameAndByType() {
        Assert.True(SceneComponentRegistry.TryGet("RegistrationTestShield", out var byName));
        Assert.True(SceneComponentRegistry.TryGet(typeof(Shield), out var byType));
        Assert.Same(byName, byType);
    }

    /// <remarks>
    ///     ⚠ <b>The list a panel is built from, and the reason the declaration is deferred.</b>
    ///     Enumerating has to resolve what has been declared and not yet built; a <c>Binders</c> that
    ///     read the dictionary directly would return an empty menu until something happened to look a
    ///     component up by name first.
    /// </remarks>
    [Fact]
    public void EnumeratingResolvesWhatWasDeclared() {
        Assert.Contains(SceneComponentRegistry.Binders, binder => binder.ComponentType == typeof(Shield));
    }

    /// <remarks>
    ///     Declaring twice is what a plugin loading twice does, and it must not throw or leave two
    ///     binders claiming one name.
    /// </remarks>
    [Fact]
    public void DeclaringAgainIsHarmless() {
        SceneComponentRegistry.Declare<Shield>();
        SceneComponentRegistry.Declare<Shield>();

        Assert.True(SceneComponentRegistry.TryGet(typeof(Shield), out var binder));
        Assert.Single(SceneComponentRegistry.Binders, entry => entry.Name == binder.Name);
    }
}
