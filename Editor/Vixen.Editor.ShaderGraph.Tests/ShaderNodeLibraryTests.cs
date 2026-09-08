// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>What is actually in the node library, and every one of it through both backends.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>Every_node_in_the_library_reaches_both_backends</c> did not read the library.</b> It
///         was named for the whole of it and was a hand-written list of node paths, so a node added
///         without a line in that list was compiled by nothing — which is what happened to
///         <c>Procedural/Noise</c>, <c>Procedural/Fractal Noise</c>, <c>Procedural/Checker</c>,
///         <c>Vector/Rotate UV</c> and <c>Vector/Flipbook</c>, and exactly what the test's own remarks
///         said it existed to prevent. It walks <see cref="NodeTypeRegistry.Types" /> here, so a new
///         <c>[Node]</c> is covered the day it is written.
///     </para>
///     <para>
///         ⚠ <b>Two things made it more than a rename.</b> Masters are mutually exclusive, so the walk
///         is parameterised over the masters the registry holds rather than picking one — and the
///         master list comes from the registry too, so a fifth master is covered without a line here.
///         And the emitted text can no longer be compiled alone: the procedural and UV nodes call into
///         <c>Raven/Library/Material/ComputeColor.rvn</c>, so a compilation over the graph's text by
///         itself reports <c>RVN2010: The name 'ComputeColor' does not exist</c> and would have made
///         a registry walk red the day it was written.
///     </para>
/// </remarks>
public sealed class ShaderNodeLibraryTests {
    /// <summary>The library, as the module README's table prints it.</summary>
    /// <remarks>
    ///     ⚠ Deliberately <em>not</em> generated from the registry, for the reason
    ///     <c>VfxNodeLibraryTests</c> gives: a list generated from the thing it describes cannot
    ///     disagree with it. A node added without a line here fails, which is the prompt to update the
    ///     README in the same change.
    /// </remarks>
    static readonly string[] Registered = [
        "Input/Colour Property",
        "Input/Constant",
        "Input/Float Property",
        "Input/Time",
        "Input/UV",
        "Input/Vertex Colour",
        "Input/World Normal",
        "Input/World Position",
        "Master/PBR",
        "Master/Sprite",
        "Master/Surface",
        "Master/Unlit",
        "Math/Absolute",
        "Math/Add",
        "Math/Divide",
        "Math/Dot",
        "Math/Fraction",
        "Math/Lerp",
        "Math/Multiply",
        "Math/Normalize",
        "Math/One Minus",
        "Math/Power",
        "Math/Saturate",
        "Math/Sine",
        "Math/Smoothstep",
        "Math/Subtract",
        "Procedural/Checker",
        "Procedural/Fractal Noise",
        "Procedural/Noise",
        "Texture/Sample 2D",
        "Vector/Combine",
        "Vector/Flipbook",
        "Vector/Rotate UV",
        "Vector/Split",
        "Vector/Tiling and Offset"
    ];

    /// <summary>What a surface graph refuses, and the reason it is a list rather than a filter.</summary>
    /// <remarks>
    ///     ⚠ <b>An unconnected node still emits</b>, so these two are refused by their presence and not
    ///     by being read: <c>SG0004</c> fires on a <c>Master/Surface</c> graph that merely holds an
    ///     <c>Input/World Position</c>. <c>MaterialData</c> carries neither, and both plausible
    ///     substitutes compile and draw a surface lit as though the graph said something it did not —
    ///     <c>SurfaceGraphTests.A_surface_cannot_read_what_a_feature_is_not_given</c> is what proves the
    ///     refusal is real, so this list is an exclusion with a test behind it rather than an
    ///     assumption. A node added that a surface also cannot hold turns the walk below red, which is
    ///     the prompt to decide which of the two it is.
    /// </remarks>
    static readonly string[] NotInASurface = ["Input/Vertex Colour", "Input/World Position"];

    /// <summary>The masters, discovered rather than listed — a fifth one is covered by existing.</summary>
    public static TheoryData<string> Masters {
        get {
            var data = new TheoryData<string>();

            foreach (var definition in Library().Types.OrderBy(type => type.Path, StringComparer.Ordinal)) {
                if (definition.Create() is ShaderMasterNode) {
                    data.Add(definition.Path);
                }
            }

            return data;
        }
    }

    /// <summary>Where the shipped shader library sits, relative to a test's output directory.</summary>
    /// <remarks>
    ///     ⚠ Walked from <see cref="AppContext.BaseDirectory" /> rather than from a
    ///     <c>[CallerFilePath]</c>, because CI sets <c>DeterministicSourcePaths</c> and rewrites every
    ///     one of those to <c>/_/</c>. <c>SurfaceGraphTests</c> resolves it the same way.
    /// </remarks>
    static string LibraryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Raven", "Library"));

    /// <summary>The library's package directories, which is how every other consumer enumerates it.</summary>
    /// <remarks>
    ///     ⚠ The directories rather than the folder: <c>Example1.rvn</c> sits at the root and imports
    ///     packages this library does not have, so including it fails every shader in the compilation
    ///     rather than only itself.
    /// </remarks>
    static IEnumerable<string> LibraryFiles() {
        foreach (var package in Directory.EnumerateDirectories(LibraryRoot).Order(StringComparer.Ordinal)) {
            foreach (var file in Directory.EnumerateFiles(package, "*.rvn", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal)) {
                yield return file;
            }
        }
    }

    /// <summary>The library is exactly the list the module README prints.</summary>
    [Fact]
    public void The_library_is_the_list_the_readme_prints() =>
        Assert.Equal(Registered, Library().Types.Select(type => type.Path).Order(StringComparer.Ordinal));

    /// <summary>Every node in the library, in one graph, through both backends — for every master.</summary>
    /// <remarks>
    ///     <para>
    ///         The test that earns the node library its keep: a node whose Raven is subtly wrong
    ///         compiles nowhere, and one whose emitted expression is the wrong width fails to
    ///         type-check. Neither shows up in a graph that never uses it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The whole shipped library is in the compilation, and it has to be.</b> Compiling
    ///         the emitted text alone — which is what this test used to do — cannot see a node that
    ///         calls a library function, and the five procedural and UV nodes all do. So the graph is
    ///         parsed beside every <c>Raven/Library</c> package with <c>MaterialCompiler</c>'s own
    ///         compose bindings, which is also the only way Raven accepts a compilation holding the
    ///         library at all: it refuses an unbound slot wherever one is declared.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the assertion is that the graph's <em>own</em> entry points came out.</b> The
    ///         module holds a hundred and thirty-odd shaders once the library is in it, so
    ///         <c>Assert.NotEmpty(generated)</c> — which is what the old helper asserted — would pass
    ///         with the graph dropped on the floor.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Masters))]
    public void Every_node_in_the_library_reaches_both_backends(string master) {
        var registry = Library();

        // What shape this master makes decides what reaching a backend means, and a test cannot read
        // `ShaderMasterNode.Kind` — it is protected. Compiling the master on its own is the honest way
        // to ask, and it is an assertion in its own right: a master that compiles to nothing covers
        // nothing.
        var alone = new NodeGraphModel { Name = "Alone" };

        alone.Add(master);

        var probe = new ShaderGraphCompiler(registry).Compile(alone);

        Assert.True(probe.Succeeded, string.Join("\n", probe.Diagnostics));

        var kind = probe.Value.Kind;
        var graph = new NodeGraphModel { Name = "Everything" };
        var held = new List<GraphNode>();

        // One of each, left unwired: an unconnected input emits through its default, which is the path
        // a fresh node takes and the one an author sees first. What wiring proves — conversion,
        // widening, a varying asked for — is asserted by the graphs written out in the two suites
        // beside this one, which is where a chain can be written down without inventing it from ports.
        foreach (var definition in registry.Types.OrderBy(type => type.Path, StringComparer.Ordinal)) {
            if (definition.Create() is ShaderMasterNode) {
                continue;
            }

            if (kind == ShaderGraphKind.Surface && NotInASurface.Contains(definition.Path, StringComparer.Ordinal)) {
                continue;
            }

            held.Add(graph.Add(definition.Path));
        }

        // ⚠ A loop that asserts inside itself passes on an empty collection, so the count is part of
        // what is asserted. An absurd floor rather than a number that drifts with the library: what
        // pins the contents is `The_library_is_the_list_the_readme_prints`.
        Assert.True(held.Count > 20, $"the walk saw {held.Count} nodes, so it is not walking the library.");

        graph.Add(master);

        var result = new ShaderGraphCompiler(registry).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        // Every node wrote a statement. A node whose `Emit` produced nothing would leave the graph
        // compiling perfectly and be covered by none of what follows.
        foreach (var node in held) {
            Assert.Contains($"n{node.Id.Value}_", result.Value.Source, StringComparison.Ordinal);
        }

        Generates(result.Value, kind);
    }

    /// <summary>Puts the emitted feature through both backends, against the whole shipped library.</summary>
    static void Generates(ShaderGraphSource source, ShaderGraphKind kind) {
        var trees = LibraryFiles()
            .Select(file => SyntaxTree.ParseText(File.ReadAllText(file), path: Path.GetFileName(file)))
            .Append(SyntaxTree.ParseText(source.Source, path: source.Name + ".rvn"))
            .ToArray();

        foreach (var tree in trees) {
            Assert.True(
                tree.Diagnostics.Count == 0,
                $"{tree.FilePath} did not parse:\n{string.Join("\n", tree.Diagnostics)}\n\n{source.Source}"
            );
        }

        // `MaterialCompiler`'s own defaults, with the graph in the first chain slot when it is a
        // feature and an identity there when it is a whole shader — every slot the library declares
        // filled, because Raven refuses a compilation with an unbound slot wherever it is declared.
        List<KeyValuePair<string, string>> bindings = [
            new("surface", "CompositeSurface"),
            new("shading", "StandardShading"),
            new("first", kind == ShaderGraphKind.Surface ? source.Name : "IdentitySurface")
        ];

        foreach (var slot in (string[])[
                     "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "under", "over"
                 ]) {
            bindings.Add(new(slot, "IdentitySurface"));
        }

        bindings.Add(new("distanceField", "NoDistanceField"));
        bindings.Add(new("irradiance", "NoIrradiance"));
        bindings.Add(new("punctualShadow", "NoPunctualShadows"));
        bindings.Add(new("directionalShadow", "NoDirectionalShadows"));
        bindings.Add(new("surfaceCache", "NoSurfaceCache"));
        bindings.Add(new("miss", "NoReflectionMiss"));

        var compilation = Compilation.Create(
            "ShaderGraph",
            PermutationValues.Empty,
            ComposeBindings.Create(bindings),
            trees
        );

        var semantic = compilation.GetDiagnostics();

        Assert.True(
            semantic.Count == 0,
            "The generated shader did not bind against the library:\n"
            + string.Join("\n", semantic.Select(diagnostic => diagnostic.ToString()))
            + "\n\n"
            + source.Source
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        IrVerifier.Verify(module, bag);
        NothingObjected(bag, source);

        foreach (var target in (string[])["glsl", "spirv"]) {
            var backend = TargetBackends.Create(target);

            Assert.NotNull(backend);

            var generated = backend.Generate(module, bag);

            NothingObjected(bag, source);

            // ⚠ The graph's own entry points, by name. A surface feature has none of its own — it is
            // composed into `ForwardPlus`, and that is the shader whose fragment stage has to carry it.
            var wanted = kind == ShaderGraphKind.Surface ? "ForwardPlus" : source.Name;

            Assert.Contains(generated, unit => unit.Name == wanted + ".vert");
            Assert.Contains(generated, unit => unit.Name == wanted + ".frag");
        }
    }

    /// <summary>Errors only, because the shipped library is in the bag too.</summary>
    /// <remarks>
    ///     ⚠ <b><c>bag.IsEmpty</c> is the wrong predicate once the library is in the compilation</b> and
    ///     it is what the old helper used, over a bag holding only the graph. Lowering a hundred and
    ///     thirty library shaders reports <c>RVN4003</c> against a dozen of them — an <c>info</c>
    ///     saying a uniform's default stays host-side — and composing a textured surface into
    ///     <c>VisibilityResolve</c> reports <c>RVN3013</c>, a warning about a compute entry point
    ///     having no derivatives. Neither is this graph's, and asserting on them would make the test
    ///     fail for the library's reasons rather than the node's.
    /// </remarks>
    static void NothingObjected(DiagnosticBag bag, ShaderGraphSource source) {
        var errors = bag.Where(diagnostic => diagnostic.IsError).ToArray();

        Assert.True(
            errors.Length == 0,
            string.Join("\n", errors.Select(diagnostic => diagnostic.ToString())) + "\n\n" + source.Source
        );
    }

    static NodeTypeRegistry Library() => ShaderNodeLibrary.Create();
}
