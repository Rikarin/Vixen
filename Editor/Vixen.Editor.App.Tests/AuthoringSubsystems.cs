// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Cameras;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Terrain;

namespace Vixen.Editor.App.Tests;

/// <summary>The subsystems whose components a test reads the registry for, loaded on purpose.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A test that asks <c>ComponentsView.Default()</c> what it offers has to load the
///         subsystems first.</b> Each assembly declares its components from a
///         <c>[ModuleInitializer]</c>, which runs when something first reaches into that assembly, and
///         <c>Default()</c> called with no registry reaches into none of them — so without this the
///         offered set is whatever the rest of the run happened to load, and the same test passes in a
///         full run and fails under a filter. Two of them did exactly that.
///     </para>
///     <para>
///         ⚠ <b>Through <see cref="AuthoringAssembly" />, which is what the editor itself does.</b>
///         <c>EditorApplication.BuiltInSubsystems</c> is the same list and
///         <c>EditorApplication</c>'s constructor touches it before the scene file is read — so a test
///         that starts a session gets this for free, and that is precisely why the two tests below
///         could go this long without saying it. A bare <c>typeof</c> would not do: the JIT can
///         satisfy a type token without running the module's initializer, which is the whole of what
///         <see cref="AuthoringAssembly.Touch" /> forces.
///     </para>
///     <para>
///         ⚠ <b>Not a fixture and not a collection.</b> Establishment belongs at the call site of the
///         test that needs it — an ordering attribute or a shared fixture would preserve the
///         dependency and stop it being visible, which is how it survived the first time.
///     </para>
/// </remarks>
static class AuthoringSubsystems {
    static readonly AuthoringAssembly[] Declared = [
        new(typeof(Light)), // Vixen.Rendering — lights, meshes, shapes, volumes, emitters
        new(typeof(TerrainComponent)), // Vixen.Rendering.Terrain
        new(typeof(VirtualCamera)), // Vixen.Engine
        new(typeof(AudioSource)) // Vixen.Audio
    ];

    /// <summary>Runs their declarations, so the registries answer the same under a filter.</summary>
    public static void Load() {
        foreach (var subsystem in Declared) {
            subsystem.Touch();
        }
    }
}
