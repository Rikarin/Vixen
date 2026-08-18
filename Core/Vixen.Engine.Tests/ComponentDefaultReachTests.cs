// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Vixen.Core.Serialization;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Scenes;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>
///     That a component declared outside the engine gets its default the same way the engine's own
///     components do.
/// </summary>
/// <remarks>
///     ⚠ <b>This assembly is the proof and not just the place the assertions live.</b> The components
///     are in <c>RegistrationComponents.cs</c>, declared here, named by nothing in the engine, and
///     reached through the generator this project runs exactly as a game project does — which is the
///     claim the mechanism replaced <c>ComponentsView.Initial</c> to be able to make.
/// </remarks>
public sealed class ComponentDefaultReachTests {
    static ISceneComponentBinder Binder<T>() =>
        SceneComponentRegistry.TryGet(typeof(T), out var binder)
            ? binder
            : throw new InvalidOperationException($"{typeof(T)} is not registered");

    [Fact]
    public void A_components_declared_default_reaches_the_registry_with_nothing_asked_of_the_game() {
        var binder = Binder<Thruster>();

        Assert.True(binder.HasDefault);

        var fresh = Assert.IsType<Thruster>(binder.CreateDefault());

        Assert.Equal(250f, fresh.Thrust);
        Assert.Equal(100f, fresh.Fuel);
    }

    /// <remarks>
    ///     The other half, and the one that keeps the mechanism free: <c>Shield</c> is declared beside
    ///     <c>Thruster</c>, says nothing, and pays nothing for the feature.
    /// </remarks>
    [Fact]
    public void A_component_that_declares_no_default_keeps_its_zero() {
        var binder = Binder<Shield>();

        Assert.False(binder.HasDefault);

        var fresh = Assert.IsType<Shield>(binder.CreateDefault());

        Assert.Equal(0f, fresh.Absorption);
    }

    /// <remarks>
    ///     ⚠ <b>The engine's own components arrive by the same route</b>, which is what stops the
    ///     mechanism being a thing games use and the engine works around. This assembly sits below
    ///     rendering and audio and can only name the engine's three; the rest are asserted in
    ///     <c>Vixen.Editor.App.Tests</c>, which can see every subsystem at once.
    /// </remarks>
    [Theory]
    [InlineData(typeof(Camera))]
    [InlineData(typeof(VirtualCamera))]
    [InlineData(typeof(CameraDirector))]
    public void The_engines_own_components_declare_theirs_the_same_way(Type component) {
        Assert.True(SceneComponentRegistry.TryGet(component, out var binder));
        Assert.True(binder.HasDefault, $"{component.Name} should declare a default");
    }

    /// <remarks>
    ///     ⚠ <b>What the editor's hardcoded chain used to produce for a camera</b>, asserted as a
    ///     value rather than as a flag — a mechanism that reached the type and then handed back the
    ///     wrong instance would pass every test above this one.
    /// </remarks>
    [Fact]
    public void A_freshly_added_camera_is_the_perspective_one() {
        var fresh = Assert.IsType<Camera>(Binder<Camera>().CreateDefault());

        Assert.Equal(Camera.Perspective.FarPlane, fresh.FarPlane);
        Assert.Equal(Camera.Perspective.NearPlane, fresh.NearPlane);
        Assert.False(fresh.Orthographic);
    }

    /// <remarks>
    ///     ⚠ <b>The one that was dead on add.</b> A zeroed shot is <i>disabled</i> as well as
    ///     lensless, so it neither rendered nor looked broken — and it was not on the editor's list,
    ///     because a list in the editor's assembly is a list somebody has to remember to extend.
    /// </remarks>
    [Fact]
    public void A_freshly_added_virtual_camera_is_a_shot_that_would_render() {
        var fresh = Assert.IsType<VirtualCamera>(Binder<VirtualCamera>().CreateDefault());

        Assert.True(fresh.Enabled);
        Assert.NotEqual(0f, fresh.Lens.FarPlane);
    }

    /// <summary>
    ///     ⚠ <b>A load starts at zero, and a declared default never reaches one.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="ISceneComponentBinder.CreateDefault" />'s remark promises this and nothing
    ///         asserted it, which made "may I change a component's default?" a question that could only
    ///         be answered by reading <c>SceneComponentBinder.Read</c> again. The answer is yes, and
    ///         this is the reason: a saved component carries every field, so
    ///         <see cref="ISceneComponentBinder.Read" /> begins at <c>default(T)</c>, and a fallback
    ///         consulted there would make every already-saved scene change meaning the day somebody
    ///         retuned a factory method.
    ///     </para>
    ///     <para>
    ///         Zero on <em>both</em> fields, and the second is the one that matters:
    ///         <c>Thruster</c>'s default is 250 and 100, so a load that reached for it would come back
    ///         with a fuel tank the file never mentioned. Written through the binder's own
    ///         <see cref="ISceneComponentBinder.WriteValue" /> so the bytes are the ones a compiled
    ///         scene actually holds rather than a shape this test invented.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_saved_components_zero_survives_the_load_rather_than_becoming_its_default() {
        var binder = Binder<Thruster>();
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SerializationWriter(buffer);

        binder.WriteValue(ref writer, default(Thruster));
        writer.Flush();

        using var world = new World("Loaded");
        var entity = world.Create();

        // Not zero to begin with, so that reading zeroes back is the file's answer rather than the
        // starting state's — a test on a freshly added component could not tell the two apart.
        world.Add(entity, new Thruster { Thrust = 999f, Fuel = 999f });

        var reader = new SerializationReader(buffer.WrittenSpan);
        binder.Read(ref reader, world, entity);

        var loaded = world.Read<Thruster>(entity);

        Assert.Equal(0f, loaded.Thrust);
        Assert.Equal(0f, loaded.Fuel);
    }

    /// <remarks>
    ///     A tag has no bytes, so it has nothing to hold — and the binder answers for one without
    ///     reaching for a value that would be a value about nothing.
    /// </remarks>
    [Fact]
    public void A_tag_has_no_default_to_declare() {
        var binder = Binder<Warded>();

        Assert.False(binder.HasDefault);
        Assert.IsType<Warded>(binder.CreateDefault());
    }
}
