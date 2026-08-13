// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Graphics.Null;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The editor's shader variants, compiled in process.</summary>
/// <remarks>
///     ⚠ <b>The tier <c>EffectSystem</c> documents itself as expecting, and the reason the viewport
///     could not be driven by a compositor.</b> Its own remarks say a bundle provider "misses on a key
///     the build did not enumerate, and the compiler behind it answers"; nothing in the editor built
///     the compiler. What is asserted here is a real key resolving to a real effect with real device
///     handles — not that the object exists.
/// </remarks>
public sealed class EditorEffectsTests : IDisposable {
    readonly NullDevice device = new();
    readonly List<string> temporary = [];

    public void Dispose() {
        device.Dispose();

        foreach (var directory in temporary) {
            try {
                if (Directory.Exists(directory)) {
                    Directory.Delete(directory, recursive: true);
                }
            } catch (IOException) {
                // A cache the test wrote and the OS has not let go of. Not what is under test.
            }
        }
    }

    EditorProject Project() {
        var root = Path.Combine(Path.GetTempPath(), "vixen-effects-" + Guid.NewGuid().ToString("N")[..8]);

        temporary.Add(root);

        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "Library"));

        return new(new ProjectPaths(root));
    }

    /// <summary>The engine's shader library ships beside the editor.</summary>
    /// <remarks>
    ///     ⚠ <b>Without it the editor draws no materials and reports nothing anybody would connect to
    ///     a missing folder.</b> Every material would miss, every draw would fall back, and the
    ///     symptom would be a scene shaded flat — so the absence is worth an assertion of its own
    ///     rather than only a refusal string.
    /// </remarks>
    [Fact]
    public void The_shader_library_is_beside_the_editor() {
        var library = Path.Combine(
            AppContext.BaseDirectory,
            EditorEffects.LibraryFolder.Replace('/', Path.DirectorySeparatorChar)
        );

        Assert.True(Directory.Exists(library), $"{library} is not there; the project file should copy it.");

        // The packages, not the two syntax samples at the root — which import packages this library
        // does not have and would fail every variant in the compilation rather than only themselves.
        Assert.Contains(Directory.EnumerateDirectories(library), path => Path.GetFileName(path) == "Core");
    }

    /// <summary>A key resolves to an effect with stages, a layout and parameters.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the whole claim, and it is the one a mocked provider cannot make.</b> The
    ///     variant is compiled by Raven from the library that ships, loaded onto a device by
    ///     <c>EffectLoader</c>, and comes back with the descriptor set layouts a pipeline is created
    ///     against. Anything less and the compositor has nothing to draw materials with.
    /// </remarks>
    [Fact]
    public void A_variant_is_compiled_on_demand_and_comes_back_loaded() {
        using var effects = new EditorEffects(device, Project());

        Assert.Null(effects.Refusal);
        Assert.True(effects.SourceCount > 50, $"only {effects.SourceCount} shader sources were found.");

        var resolved = effects.System.Resolve(EffectKey.Of("Tonemap"));

        Assert.NotNull(resolved);
        Assert.NotEmpty(resolved.Stages);
        Assert.Empty(effects.System.Misses);
    }

    /// <summary>The shading pass compiles too, which is the variant a viewport actually needs.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>Tonemap</c> proves the chain and not the frame.</b> It declares no slots, so it
    ///         compiles whatever the composition says; <c>ForwardPlus</c> is the pass every surface in
    ///         the scene is drawn by, it is where <see cref="Rendering.Materials.MaterialCompiler" />'s
    ///         composition is actually spent, and it is the one whose absence would leave a
    ///         compositor-driven pane with lights, geometry, post — and no shaded pixel anywhere.
    ///     </para>
    ///     <para>
    ///         The set layouts are asserted rather than only the stages, because they are what a
    ///         pipeline is created against and what <c>WorldRenderer.AdoptViewLayout</c> reads set 1's
    ///         layout out of. A variant that came back with stages and no layouts would resolve, draw
    ///         nothing, and report success.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_shading_pass_compiles_on_demand() {
        using var effects = new EditorEffects(device, Project());

        Assert.Null(effects.Refusal);

        var resolved = effects.System.Resolve(EffectKey.Of("ForwardPlus"));

        Assert.NotNull(resolved);
        Assert.NotEmpty(resolved.Stages);
        Assert.NotEmpty(resolved.SetLayouts);
        Assert.Empty(effects.System.Misses);
    }

    /// <summary>The second ask for a variant is the same object rather than a second compilation.</summary>
    [Fact]
    public void A_variant_is_compiled_once() {
        using var effects = new EditorEffects(device, Project());

        var first = effects.System.Resolve(EffectKey.Of("Tonemap"));
        var again = effects.System.Resolve(EffectKey.Of("Tonemap"));

        Assert.Same(first, again);
        Assert.Equal(1, effects.System.Count);
    }

    /// <summary>A compiled variant is written to the project's cache, so the next session reads it.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes compile-on-demand affordable at all.</b> Without the cache every variant
    ///     the editor has ever shown is recompiled on every launch, which for a scene of forty
    ///     materials is a stall somebody would call a hang.
    /// </remarks>
    [Fact]
    public void A_compiled_variant_lands_in_the_projects_cache() {
        var project = Project();

        using var effects = new EditorEffects(device, project);

        effects.System.Resolve(EffectKey.Of("Tonemap"));

        var cache = Path.Combine(project.Paths.Library, "Shaders");

        Assert.True(Directory.Exists(cache), "nothing was cached, so every launch recompiles.");
        Assert.NotEmpty(Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories));
    }

    /// <summary>A key nothing declares is a miss rather than an exception.</summary>
    /// <remarks>
    ///     ⚠ <b>A miss has to be survivable, because a material naming a shader somebody deleted is
    ///     an ordinary state of a project.</b> The miss list is what a shipping build asserts is
    ///     empty; in an editor it is a row in a panel.
    /// </remarks>
    [Fact]
    public void A_shader_that_does_not_exist_is_a_miss() {
        using var effects = new EditorEffects(device, Project());

        Assert.Null(effects.System.Resolve(EffectKey.Of("NoSuchShaderAnywhere")));
        Assert.Equal(1, effects.System.MissCount);
    }

    /// <summary>A rebuild keeps the one object every consumer took a reference to.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The whole of what makes editing a shader do anything.</b> A
    ///         <c>WorldRenderer</c> hands its <see cref="EffectSystem" /> to the render host, to both
    ///         material features and to the compositor builder — all in a constructor — so a rebuild
    ///         that answered by <em>replacing</em> the system recompiles into an object nothing is
    ///         asking any more.
    ///     </para>
    ///     <para>
    ///         The failure that produces is silent in every direction: the new system reports the
    ///         right source count and no refusal, the old one goes on answering out of its cache, and
    ///         the editor draws the pre-edit shader with no miss and no warning for the rest of the
    ///         session.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_rebuild_keeps_the_system_the_renderer_is_holding() {
        using var effects = new EditorEffects(device, Project());

        var held = effects.System;

        Assert.NotNull(held.Resolve(EffectKey.Of("Tonemap")));

        effects.Rebuild();

        Assert.Same(held, effects.System);

        // And the variant compiled from the old sources is gone from it, which is the other half:
        // an identity that survived while the cache did would answer every ask with the pre-edit
        // shader.
        Assert.Equal(0, held.Count);
        Assert.NotNull(held.Resolve(EffectKey.Of("Tonemap")));
    }

    /// <summary>An edit that breaks a shader is seen through the reference somebody already had.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted through <c>held</c> and never through <c>effects.System</c>.</b> That is what
    ///     makes this a test of the identity rather than of the rebuild: a tier that swapped its system
    ///     would report the refusal on the new one and hand the old one — the one the renderer has —
    ///     the shader that compiled before the edit.
    /// </remarks>
    [Fact]
    public void A_broken_edit_reaches_the_system_somebody_already_holds() {
        var project = Project();

        using var effects = new EditorEffects(device, project);

        var held = effects.System;

        Assert.NotNull(held.Resolve(EffectKey.Of("Tonemap")));

        File.WriteAllText(Path.Combine(project.Paths.Assets, "Broken.rvn"), "this is not a shader at all {{{");
        effects.Rebuild();

        Assert.NotNull(effects.Refusal);
        Assert.Null(held.Resolve(EffectKey.Of("Tonemap")));

        // And fixing it puts the same reference back to work, which is what a person editing a shader
        // in the editor actually does.
        File.Delete(Path.Combine(project.Paths.Assets, "Broken.rvn"));
        effects.Rebuild();

        Assert.Null(effects.Refusal);
        Assert.NotNull(held.Resolve(EffectKey.Of("Tonemap")));
    }

    /// <summary>A project shader that does not parse is reported once, on the way in.</summary>
    /// <remarks>
    ///     ⚠ <b>Met at construction rather than on the frame that wanted an unrelated variant.</b>
    ///     Parsing is shared across every variant, so a broken file in <c>Assets/</c> fails all of
    ///     them — and the useful message names the file rather than the material that happened to ask
    ///     first.
    /// </remarks>
    [Fact]
    public void A_broken_project_shader_is_refused_with_its_diagnostics() {
        var project = Project();

        File.WriteAllText(Path.Combine(project.Paths.Assets, "Broken.rvn"), "this is not a shader at all {{{");

        using var effects = new EditorEffects(device, project);

        Assert.NotNull(effects.Refusal);
        Assert.NotEqual(string.Empty, effects.Refusal);
    }
}
