// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>"It is a plugin" is a reference set, and this is the reference set.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D14: <c>referencing <c>Vixen.Editor.App</c> not at all</c>.</b> Everything else
///         in that sentence — activating through <c>PluginHost</c>, asking through
///         <c>PluginServices</c>, unloading cleanly — is asserted by <c>TexturingModuleTests</c>. This
///         is the half that would still be true of a feature compiled into the application, and it is
///         therefore the half worth pinning.
///     </para>
///     <para>
///         ⚠ <b>What this can and cannot catch, said plainly.</b>
///         <c>Assembly.GetReferencedAssemblies</c> lists what the compiler emitted a reference for,
///         which is what was <em>used</em> — so adding a <c>ProjectReference</c> to
///         <c>Vixen.Editor.App</c> and touching nothing in it would slip past. It catches the change
///         that matters, which is somebody reaching for an application type. The rule belongs in
///         <c>build/Build.ArchitectureRules.cs</c>, which reads project files and would catch both;
///         this slice does not own that file and says so rather than leaving the gap unstated.
///     </para>
/// </remarks>
public class ModuleReferenceTests {
    [Fact]
    public void The_plugin_does_not_reference_the_editor_application() {
        var referenced = typeof(TexturingModule).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToList();

        Assert.DoesNotContain("Vixen.Editor.App", referenced);

        // ⚠ The instrument, checked before the finding. A list that came back empty — a trimmed
        // assembly, a reflection API that answered differently — would make the assertion above pass
        // for the wrong reason, and the assemblies this one certainly uses are the cheapest proof it
        // did not.
        Assert.Contains("Vixen.Editor.Plugin", referenced);
        Assert.Contains("Vixen.Editor.TextureGraph", referenced);
    }

    /// <summary>The compiler and the node library both cross the assembly boundary, on their own terms.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This test was written as a tripwire and it fired on the very next merge.</b> It
    ///         asserted that <c>TextureGraphCompiler</c> was <c>internal</c> — the finding doc 48 did
    ///         not predict — and said in as many words that the day it went red was the day the panel
    ///         could grow a preview. <see href="https://github.com/Rikarin/Vixen/issues/738" /> made it
    ///         public in the same batch, so the assertion is inverted rather than deleted: what a
    ///         plugin can reach is now the compiler <em>and</em> the generated registration.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The other half still holds and is the one worth keeping.</b> Reachability is
    ///         through the PUBLIC surface, not through <c>InternalsVisibleTo</c> — an assembly that
    ///         named this plugin a friend would work for the plugin the editor ships and for no third
    ///         party, which is the arrangement the whole contract exists to avoid.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_texture_graph_compiler_is_reachable_from_a_plugin_through_its_public_surface() {
        var evaluator = typeof(TextureGraph.TexturePlan).Assembly;

        Assert.NotNull(evaluator.GetType("Vixen.Editor.TextureGraph.NodeTypes"));

        var compiler = evaluator.GetType("Vixen.Editor.TextureGraph.TextureGraphCompiler");

        Assert.NotNull(compiler);
        Assert.True(compiler!.IsPublic, "TextureGraphCompiler went internal again — the panel's preview depends on it.");

        Assert.DoesNotContain(
            evaluator.GetCustomAttributes<InternalsVisibleToAttribute>(),
            attribute => attribute.AssemblyName == "Vixen.Editor.Texturing"
        );
    }
}
