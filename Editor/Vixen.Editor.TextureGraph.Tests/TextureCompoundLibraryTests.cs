// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core.Yaml;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § D5's other half: the compounds are content, and this is the folder that makes that
///     true.
/// </summary>
/// <remarks>
///     <para>
///         <b>The files are the subject, not a fixture.</b> Every case below reads the committed
///         <c>.vxtexgraph</c>s out of the assembly's own manifest and compiles them through the real
///         node library — so a node that renames a port, or a setting that stops taking a name a
///         compound types into it, is red here rather than at the first artist who drops the
///         generator into a graph. That is the whole reason a library of content needs a suite: a
///         compound cannot be caught by the compiler, because it is not compiled.
///     </para>
///     <para>
///         ⚠ <b>Ask what this file prints on the day the folder ships nothing.</b>
///         <see cref="Every_shipped_compound_publishes_and_compiles" /> would then iterate an empty
///         list and pass, which is the shape of a green suite over no work at all — so
///         <see cref="The_shipped_library_is_the_folder_and_not_a_list" /> asserts the count and
///         names the four, and it is the first thing to read when something here goes quiet.
///     </para>
/// </remarks>
public sealed class TextureCompoundLibraryTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-compounds-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A file the test wrote and the OS has not let go of. Not what is under test.
        }
    }

    /// <summary>Where this file was compiled from, which is what the folder walk is anchored to.</summary>
    static string Here([CallerFilePath] string path = "") => path;

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>A graph containing one published compound, wired into an output.</summary>
    static (TextureGraphCompiler Compiler, NodeGraphModel Graph, GraphNode Used) Containing(
        string path,
        ISubGraphSource library,
        NodeTypeRegistry registry
    ) {
        NodeGraphModel graph = new();
        var used = graph.Add(path);
        var output = graph.Add("Output/Output");

        graph.Connect(new(used.Id, "Out"), new(output.Id, "Input"));

        // Whatever image inputs the compound declares get a source, because an unwired one is a
        // TG0002 about the containing graph rather than about the compound — and the point of these
        // cases is what the compound does, not what a half-built canvas does.
        foreach (var port in registry.Types.Single(type => type.Path == path).Ports) {
            if (port is { Direction: PortDirection.Input, Kind: PortKind.Image }) {
                graph.Connect(new(graph.Add("Source/Noise").Id, "Out"), new(used.Id, port.Name));
            }
        }

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = 128,
            BaseHeight = 128,
            Seed = 5,
            SubGraphSource = library
        };

        return (compiler, graph, used);
    }

    /// <summary>The library is what the folder holds, and the folder holds these.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The names used to be spelled out here and that was the wrong shape.</b> The
    ///         defect this catches is a compound that silently stopped shipping — a file moved out of
    ///         <c>Compounds/</c>, an <c>EmbeddedResource</c> glob narrowed — and an equality against
    ///         four literals catches that <em>and</em> goes red on the merge that adds a fifth
    ///         compound, which is the exact-equality-over-a-shared-surface failure this workstream
    ///         has now had six times. So the expectation is the folder: the files on disk, read at
    ///         the path this file was compiled from, against the manifest
    ///         <see cref="TextureCompoundLibrary.Shipped" /> is built out of. A slice that ships a
    ///         fifth compound is covered by this without editing it; a file that stops being
    ///         embedded still fails.
    ///     </para>
    ///     <para>
    ///         <b>The two sides really are independent.</b> <c>Shipped</c> comes from
    ///         <c>GetManifestResourceNames</c> — what the build put <em>into the assembly</em> — and
    ///         the expectation is the directory listing. That is what makes narrowing the glob
    ///         visible; a test that derived both from the same place would agree with itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Anchored at this file's compiled path, never walked up to the repository
    ///         root.</b> <c>.claude/worktrees</c> holds a whole checkout per agent, so a walk from
    ///         the root would be comparing other people's copies of these compounds with this
    ///         assembly's.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_shipped_library_is_the_folder_and_not_a_list() {
        var folder = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(Here())!)!,
            "Vixen.Editor.TextureGraph",
            "Compounds"
        );

        Assert.True(
            Directory.Exists(folder),
            $"'{folder}' does not exist, so the expectation below is empty and this compares nothing with "
            + "nothing. It is anchored at this file's compiled path; a run whose sources are not on the "
            + "machine cannot take this case."
        );

        var onDisk = Directory
            .GetFiles(folder, "*" + TextureCompoundLibrary.Extension, SearchOption.AllDirectories)
            .Select(path => Path.ChangeExtension(Path.GetRelativePath(folder, path), null)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The instrument: an empty folder listing would make the equality below a claim that the
        // assembly ships nothing, which is what a vacuous pass looks like here. Four when this was
        // written — a floor, because a fifth compound is somebody's work rather than this file's bug.
        Assert.True(
            onDisk.Length >= 4,
            $"Only {onDisk.Length} compound(s) were found under '{folder}', and there were four when this was "
            + "written. Doc 48 § A.9's honest number is measured against this folder, so a walk that found "
            + "almost nothing is a pass over no content rather than a clean library."
        );

        Assert.Equal(onDisk, TextureCompoundLibrary.Shipped);

        // ⚠ And membership rather than equality for the four § 4.9 shipped, which is the half the
        // folder comparison above genuinely cannot make: a compound *moved out of* `Compounds/`
        // leaves the disk and the manifest at once, so the two sides agree and say nothing. Renaming
        // or retiring one of these is a breaking change to every graph containing it — a node type
        // that vanishes from a menu — so it should be a deliberate edit here rather than a silence.
        // `Contains` and not `Equal`: a fifth compound is a sibling's work, not this file's failure.
        Assert.All(
            ["Generators/Curvature Edge Wear", "Generators/Dirt", "Generators/Grunge Rough Dirty",
                "Utility/Histogram Scan"],
            path => Assert.Contains(path, TextureCompoundLibrary.Shipped)
        );

        // ⚠ A manifest resource name has no way to tell a folder separator from a dot somebody put
        // in a file name, so a compound called `Grunge v2.vxtexgraph` would publish under a path with
        // a phantom folder in it — silently, and only visible as a node missing from a menu.
        Assert.All(TextureCompoundLibrary.Shipped, path => Assert.DoesNotContain('.', path));
    }

    /// <summary>
    ///     Every shipped compound publishes with no complaint and compiles inside another graph.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>And it is <em>not</em> what makes a folder of content safe to ship, which this file
    ///     claimed until the claim was sabotage-tested.</b> Renaming <c>Colour/Levels</c>'s
    ///     <c>Input Black</c> port leaves every case here green: a stale key in
    ///     <see cref="GraphNode.Values" /> is not an error — the dictionary is free — and
    ///     <c>NodeGraphDocument.Load</c> drops an edge to a port that has gone without saying so
    ///     loudly enough to reach a compilation. The shipped generator then computes something else,
    ///     silently, in every graph that contains it.
    ///     <see cref="Every_port_a_shipped_compound_names_is_a_port_that_exists" /> is the case that
    ///     goes red for that, and this one is the weaker claim it rests on: that the files parse,
    ///     publish and produce a plan.
    /// </remarks>
    [Fact]
    public void Every_shipped_compound_publishes_and_compiles() {
        var registry = Registry();
        var library = TextureCompoundLibrary.Publish(registry, folder: null, out var problems);

        Assert.Equal([], problems.Select(problem => problem.Path + ": " + problem.Problem).ToArray());
        Assert.NotEmpty(TextureCompoundLibrary.Shipped);

        foreach (var path in TextureCompoundLibrary.Shipped) {
            // It is a node type an author can find, with the ports the file declares.
            Assert.Contains(registry.Types, type => type.Path == path);

            var (compiler, graph, _) = Containing(path, library, registry);
            var compilation = compiler.Compile(graph);

            Assert.Empty(compilation.Diagnostics);

            // And it produced something: an inlined compound that contributed no op would be a node
            // that draws nothing, which every assertion above is true of.
            Assert.NotEmpty(compilation.Value.Ops);
            Assert.False(compiler.Inlining.IsEmpty);
        }

        // ⚠ And one of them contains another, which is the property that makes a library a library
        // rather than a folder: `Generators/Curvature Edge Wear` reaches its threshold through
        // `Utility/Histogram Scan`. Two levels of inlining, from files, with no code between them.
        var (nested, wear, _) = Containing("Generators/Curvature Edge Wear", library, registry);

        Assert.Empty(nested.Compile(wear).Diagnostics);
        Assert.Contains(nested.Inlining.Origins, origin => origin.Value.Type == "Utility/Histogram Scan");
    }

    /// <summary>
    ///     ⚠ Every port, setting and node type a shipped compound names still exists.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one assertion in this file that a rename cannot walk past, and it exists
    ///         because the obvious one could.</b> Sabotage: rename <c>Colour/Levels</c>'s
    ///         <c>Input Black</c> to <c>Black Point</c>. Every other case here stays green —
    ///         <c>Dirt</c>'s <c>0.48</c> is now a key in a free dictionary that nothing reads, so the
    ///         generator publishes, compiles and produces a plan whose levels are the node's defaults
    ///         instead of the ones an author chose. A picture, drawn without a word, which is this
    ///         repository's commonest shape of wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the check is against the <em>file</em> and not against the published node
    ///         type.</b> Publishing has already thrown the difference away: a graph model holds
    ///         whatever values were written down and a node type holds the ports that exist, and
    ///         nothing between them compares the two. That is the same reason
    ///         <c>TextureNodeLibraryTests</c> reads the embedded <c>.rvn</c>s rather than the
    ///         <c>All</c> declarations — a roll call taken over the thing that was already filtered
    ///         cannot see what the filter dropped.
    ///     </para>
    ///     <para>
    ///         The boundary nodes are the exception, and a stated one: <c>Sub-graph/Input</c> and
    ///         <c>Sub-graph/Output</c> have no registered type at all — their ports are the graph's
    ///         own <c>interface</c>, synthesised per graph — so they are checked against that
    ///         instead, which is the same question asked of the right list.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_port_a_shipped_compound_names_is_a_port_that_exists() {
        // ⚠ The registry the compounds are checked against is the one an author has, which is the one
        // the compounds themselves are published into: `Generators/Curvature Edge Wear` contains
        // `Utility/Histogram Scan`, so a roll call over the generated node types alone would call a
        // compound-inside-a-compound an undeclared type. A library grows by nesting, and this is the
        // only case in the file that would have noticed.
        var registry = Registry();

        TextureCompoundLibrary.Publish(registry, folder: null, out _);

        var wrong = new List<string>();

        Assert.NotEmpty(TextureCompoundLibrary.Shipped);

        foreach (var path in TextureCompoundLibrary.Shipped) {
            var text = TextureCompoundLibrary.Source(path);

            Assert.NotNull(text);

            var asset = YamlSerializer.Parse<NodeGraphAsset>(text);
            var boundary = asset.Interface.Select(port => port.Name).ToHashSet(StringComparer.Ordinal);
            var types = asset.Nodes.ToDictionary(node => node.Id, node => node.Type);

            bool Has(int id, string port) {
                var type = types.GetValueOrDefault(id, "");

                // A boundary node's ports are the graph's interface, whichever way round they face
                // from inside — an `Out` on the graph is an input of the Output node.
                if (SubGraphs.IsBoundary(type)) {
                    return boundary.Contains(port);
                }

                return registry.Types.FirstOrDefault(candidate => candidate.Path == type) is { } definition
                    && (definition.Ports.Any(declared => declared.Name == port)
                        || definition.Setting(port) is not null);
            }

            foreach (var node in asset.Nodes) {
                if (!SubGraphs.IsBoundary(node.Type)
                    && registry.Types.All(candidate => candidate.Path != node.Type)) {
                    wrong.Add($"{path}: node {node.Id} is a '{node.Type}', which nothing declares.");

                    continue;
                }

                foreach (var port in node.Values.Keys.Concat(node.Texts.Keys)) {
                    // An expression is stored under a marked key naming the port it drives.
                    var named = TextureGraphExpressions.IsExpression(port, out var driven) ? driven : port;

                    if (!Has(node.Id, named)) {
                        wrong.Add($"{path}: node {node.Id} ('{node.Type}') sets '{named}', which it has not got.");
                    }
                }
            }

            foreach (var edge in asset.Edges) {
                if (!Has(edge.FromNode, edge.FromPort)) {
                    wrong.Add($"{path}: an edge leaves node {edge.FromNode}'s '{edge.FromPort}', which is not a port.");
                }

                if (!Has(edge.ToNode, edge.ToPort)) {
                    wrong.Add($"{path}: an edge arrives at node {edge.ToNode}'s '{edge.ToPort}', which is not a port.");
                }
            }
        }

        Assert.Equal([], wrong.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    ///     ⚠ Every shipped generator reads a mesh map by usage, and names no mesh.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48's own finding, as an assertion: Painter's smartness is entirely in the
    ///         bakes.</b> Dirt is a curvature multiplied by an occlusion and edge wear is a curvature
    ///         with a scan on it — so a generator that read no mesh map would be a flat colour with a
    ///         generator's name on it, which is exactly the state
    ///         <a href="https://github.com/Rikarin/Vixen/issues/702">#702</a> found the whole read
    ///         side in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the second half is what makes one compound work on every mesh:</b> every
    ///         external a generator asks for is a <c>meshmap:</c> reference, so the file names no
    ///         asset, no path and no mesh at all. A generator that had acquired a hard-wired bitmap
    ///         would compile, bake and look right on the machine that authored it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_shipped_generator_reads_a_mesh_map_and_names_no_mesh() {
        var registry = Registry();
        var library = TextureCompoundLibrary.Publish(registry, folder: null, out _);

        var generators = TextureCompoundLibrary.Shipped
            .Where(path => path.StartsWith("Generators/", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(generators);

        foreach (var path in generators) {
            var (compiler, graph, _) = Containing(path, library, registry);

            Assert.Empty(compiler.Compile(graph).Diagnostics);
            Assert.NotEmpty(compiler.Externals);

            Assert.All(
                compiler.Externals,
                external => Assert.StartsWith("meshmap:", external.Asset, StringComparison.Ordinal)
            );
        }
    }

    /// <summary>
    ///     A compound's scalar interface ports are knobs a containing graph turns, and its parameters
    ///     are not.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Which is why <c>Histogram Scan</c>'s two knobs are ports and not
    ///     <c>TextureGraphParameter</c>s, and it is worth saying because § D9 reads as though either
    ///     would do.</b> <c>SubGraphs.Flatten</c> replaces the sub-graph node with the graph's
    ///     contents and the node — which is where a parameter override is stored — is then gone, so
    ///     an expression inside a published graph folds against that graph's declared default and
    ///     turning the knob changes nothing until
    ///     <a href="https://github.com/Rikarin/Vixen/issues/742">#742</a>. A port survives inlining
    ///     because it is an edge. So the shipped compounds put every knob on the interface, and this
    ///     is what says the difference is real rather than a preference.
    /// </remarks>
    [Fact]
    public void A_compounds_scalar_port_is_a_knob_a_containing_graph_turns() {
        var registry = Registry();
        var library = TextureCompoundLibrary.Publish(registry, folder: null, out _);

        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var scan = graph.Add("Utility/Histogram Scan");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(scan.Id, "Input"));
        graph.Connect(new(scan.Id, "Out"), new(output.Id, "Input"));
        scan.SetValue("Black", 0.25f);

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = 64,
            BaseHeight = 64,
            SubGraphSource = library
        };

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var levels = Assert.Single(compilation.Value.Ops, op => op.Kernel == "Levels");

        // The number typed into the containing graph's node reached the inlined Levels — which is
        // the whole of what "a published graph is a node with knobs" has to mean.
        Assert.Equal(0.25f, Assert.Single(levels.Parameters, parameter => parameter.Name == "inputBlack").Value);

        // And the port's own default is what an untouched knob is worth, rather than the Levels
        // node's — so the compound decides its own defaults, which is what makes it a node.
        Assert.Equal(0.55f, Assert.Single(levels.Parameters, parameter => parameter.Name == "inputWhite").Value);
    }

    /// <summary>A project's own compounds sit in the same menu as the shipped ones.</summary>
    [Fact]
    public void A_projects_own_compound_is_published_beside_the_shipped_ones() {
        var folder = Path.Combine(root, "Compounds", "Studio");

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "House Grunge.vxtexgraph"), Simple());

        var registry = Registry();
        var library = TextureCompoundLibrary.Publish(registry, Path.Combine(root, "Compounds"), out var problems);

        Assert.Empty(problems);
        Assert.Contains(registry.Types, type => type.Path == "Studio/House Grunge");

        // And it is usable, which is a stronger claim than being in the menu.
        var (compiler, graph, _) = Containing("Studio/House Grunge", library, registry);

        Assert.Empty(compiler.Compile(graph).Diagnostics);

        // The shipped ones are still there: a project folder adds to the library rather than
        // replacing it.
        Assert.All(
            TextureCompoundLibrary.Shipped,
            path => Assert.Contains(registry.Types, type => type.Path == path)
        );
    }

    /// <summary>
    ///     ⚠ A project compound that collides with a shipped one is refused rather than allowed to
    ///     shadow it.
    /// </summary>
    /// <remarks>
    ///     <b>Silently overriding is how a graph that worked yesterday starts computing something
    ///     else.</b> An author's half-finished copy of <c>Generators/Dirt</c>, saved under the same
    ///     name, would rebind every material in the project that reads it — with no edit to any of
    ///     them and nothing anywhere saying so. The refusal names both files, because the author's
    ///     next move is to rename one.
    /// </remarks>
    [Fact]
    public void A_project_compound_may_not_shadow_a_shipped_one() {
        var folder = Path.Combine(root, "Compounds", "Generators");

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Dirt.vxtexgraph"), Simple());

        var registry = Registry();

        TextureCompoundLibrary.Publish(registry, Path.Combine(root, "Compounds"), out var problems);

        var clash = Assert.Single(problems);

        Assert.Equal("Generators/Dirt", clash.Path);
        Assert.EndsWith("Dirt.vxtexgraph", clash.Source, StringComparison.Ordinal);
        Assert.NotEmpty(clash.Problem);

        // And the shipped one is what survived: the project's file did not replace it, so a graph
        // containing Generators/Dirt still reads mesh maps.
        var library = TextureCompoundLibrary.Publish(registry: new(), folder: null, out _);
        var fresh = Registry();

        library = TextureCompoundLibrary.Publish(fresh, Path.Combine(root, "Compounds"), out _);

        var (compiler, graph, _) = Containing("Generators/Dirt", library, fresh);

        Assert.Empty(compiler.Compile(graph).Diagnostics);
        Assert.NotEmpty(compiler.Externals);
    }

    /// <summary>A file that will not read is reported and costs the rest of the library nothing.</summary>
    [Fact]
    public void An_unreadable_compound_is_reported_and_the_rest_still_publish() {
        var folder = Path.Combine(root, "Compounds", "Studio");

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Broken.vxtexgraph"), "nodes: [ this is not a graph");
        File.WriteAllText(Path.Combine(folder, "Fine.vxtexgraph"), Simple());

        var registry = Registry();

        TextureCompoundLibrary.Publish(registry, Path.Combine(root, "Compounds"), out var problems);

        Assert.Equal("Studio/Broken", Assert.Single(problems).Path);
        Assert.Contains(registry.Types, type => type.Path == "Studio/Fine");
        Assert.All(
            TextureCompoundLibrary.Shipped,
            path => Assert.Contains(registry.Types, type => type.Path == path)
        );
    }

    /// <summary>The smallest publishable compound: a colour out through the boundary.</summary>
    static string Simple() =>
        """
        version: 1
        name: Simple
        nodes:
          - { id: 1, type: Source/Uniform, x: 0, y: 0 }
          - { id: 2, type: Sub-graph/Output, x: 320, y: 0 }
        edges:
          - { fromNode: 1, fromPort: Out, toNode: 2, toPort: Out }
        interface:
          - { name: Out, direction: Output, kind: Image }
        settings: { baseWidth: '512', baseHeight: '512', seed: '3' }
        """;
}
