// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.VfxGraph;
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>What is actually in the library, against what the module says is in it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The README's table drifted by seven nodes with a green suite, because nothing asserted
///         the registry's <em>contents</em>.</b> <c>Every_block_in_the_library_compiles_both_ways</c> is
///         registry-driven, so it covers whatever is there and says nothing about what is missing —
///         which is the right shape for a smoke test and the wrong one for a claim about the library.
///     </para>
///     <para>
///         So the list below is written out. A node added without a line here fails, which is a
///         prompt to update the README in the same change; a node removed fails for the same reason.
///         ⚠ It is deliberately <em>not</em> generated from the registry, because a list generated
///         from the thing it describes cannot disagree with it.
///     </para>
/// </remarks>
public sealed class VfxNodeLibraryTests {
    static readonly string[] Registered = [
        "Vfx/Effect",
        "Vfx/Initialize/Angular Velocity",
        "Vfx/Initialize/Colour",
        "Vfx/Initialize/Lifetime",
        "Vfx/Initialize/Position",
        "Vfx/Initialize/Position in Box",
        "Vfx/Initialize/Position in Sphere",
        "Vfx/Initialize/Random Custom",
        "Vfx/Initialize/Random Velocity",
        "Vfx/Initialize/Rotation",
        "Vfx/Initialize/Set Custom",
        "Vfx/Initialize/Set Velocity",
        "Vfx/Initialize/Size",
        "Vfx/Initialize/Velocity in Cone",
        "Vfx/Output/Billboard",
        "Vfx/Output/Light",
        "Vfx/Output/Mesh",
        "Vfx/Output/Ribbon",
        "Vfx/Spawn/Burst",
        "Vfx/Spawn/Rate",
        "Vfx/Update/Attract",
        "Vfx/Update/Collide Plane",
        "Vfx/Update/Collide Sphere",
        "Vfx/Update/Colour over Life",
        "Vfx/Update/Custom over Life",
        "Vfx/Update/Drag",
        "Vfx/Update/Gravity",
        "Vfx/Update/Integrate",
        "Vfx/Update/Rotate",
        "Vfx/Update/Size over Life",
        "Vfx/Update/Turbulence",
        "Vfx/Update/Vortex"
    ];

    /// <summary>The library is exactly the list the module README prints.</summary>
    [Fact]
    public void The_library_is_the_list_the_readme_prints() =>
        Assert.Equal(Registered, Library().Types.Select(type => type.Path).Order(StringComparer.Ordinal));

    /// <summary>Every opcode the runtime implements is reachable from a graph.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the assertion the node census cannot make, and the one that was silently
    ///         false for five opcodes.</b> <c>SetPosition</c>, <c>VelocityInCone</c>,
    ///         <c>SetRotation</c>, <c>SetAngularVelocity</c> and <c>Rotate</c> were each implemented in
    ///         <c>VfxSimulation</c> and in <c>VfxShaderEmitter</c> — two backends, in agreement, for
    ///         behaviour no author could ask for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every block is dropped in unwired and asked what it contributed</b>, rather than
    ///         each node being named beside an opcode. A table pairing the two would be a second copy
    ///         of <c>Contribute</c>, and would agree with a node that contributed the wrong opcode.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_opcode_the_runtime_implements_has_a_node_that_emits_it() {
        var graph = new NodeGraphModel { Name = "Census" };

        foreach (var path in Registered) {
            // One renderer per graph, and the light output would claim it. Nothing here is a
            // renderer's business: an output contributes no operation.
            if (path.StartsWith("Vfx/Output/", StringComparison.Ordinal)) {
                continue;
            }

            var node = graph.Add(path);

            if (node.Type.Contains("Custom", StringComparison.Ordinal)) {
                node.SetText("Attribute", "strip");
            }
        }

        var result = new VfxGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        var reached = result.Value.Graph.Initializers
            .Concat(result.Value.Graph.Updaters)
            .Select(operation => operation.Opcode)
            .ToHashSet();

        var unreachable = Enum.GetValues<VfxOpcode>().Where(opcode => !reached.Contains(opcode)).ToArray();

        Assert.True(unreachable.Length == 0, $"no node emits {string.Join(", ", unreachable)}.");
    }

    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();

        NodeTypes.Register(registry);

        return registry;
    }
}
