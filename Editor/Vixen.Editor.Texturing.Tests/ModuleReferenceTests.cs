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

    /// <summary>The node library crosses the assembly boundary; nothing else does.</summary>
    /// <remarks>
    ///     ⚠ <b>The finding doc 48 did not predict.</b> <c>TextureGraphCompiler</c> and every
    ///     <c>[Node]</c> class in <c>Vixen.Editor.TextureGraph</c> are <c>internal</c>, and that
    ///     assembly's <c>InternalsVisibleTo</c> names only its own tests — so what a plugin can reach
    ///     is the generated <c>NodeTypes.Register</c> and the evaluator's public surface, and not the
    ///     thing that turns a graph into a plan. This test is what makes the day that changes visible:
    ///     it goes red, and the panel can grow a preview.
    /// </remarks>
    [Fact]
    public void The_texture_graph_compiler_is_not_reachable_from_a_plugin() {
        var evaluator = typeof(TextureGraph.TexturePlan).Assembly;

        Assert.NotNull(evaluator.GetType("Vixen.Editor.TextureGraph.NodeTypes"));

        var compiler = evaluator.GetType("Vixen.Editor.TextureGraph.TextureGraphCompiler");

        Assert.NotNull(compiler);
        Assert.False(compiler.IsPublic, "TextureGraphCompiler is public now — give this plugin a preview.");

        Assert.DoesNotContain(
            evaluator.GetCustomAttributes<InternalsVisibleToAttribute>(),
            attribute => attribute.AssemblyName == "Vixen.Editor.Texturing"
        );
    }
}
