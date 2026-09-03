// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Raven;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     A graph compiled as a material feature — the shape a <c>.vxmat</c> composes and the only one
///     anything in this engine can draw with.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every test here binds the generated source against the <em>real</em> shader library,
///         not on its own.</b> That is the whole difference from
///         <see cref="ShaderGraphCompilerTests" />, and it is not thoroughness — it is the only way
///         the assertion can be true. A standalone shader is self-contained, so compiling it alone
///         proves it; a feature names <c>IMaterialSurface</c>, <c>MaterialData</c>,
///         <c>MaterialDefaults</c>, <c>Brdf</c>, <c>Normals</c> and <c>MaterialTextures</c>, every
///         one of which lives in <c>Raven/Library</c>. A test that parsed the text and stopped would
///         pass on a shader that names none of them correctly.
///     </para>
///     <para>
///         <b>And it is bound composed into the chain</b> — <c>CompositeSurface.first</c> — rather
///         than as a loose declaration. A shader that binds in isolation and refuses when composed is
///         exactly the failure a material would meet on the first frame that wanted it, and the
///         composition is what puts the generated <c>Compute</c> where <c>ForwardPlus</c> calls it.
///     </para>
/// </remarks>
public class SurfaceGraphTests {
    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();

        NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>Where the shipped shader library sits, relative to a test's output directory.</summary>
    static string LibraryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Raven", "Library"));

    /// <summary>
    ///     The library's package directories, which is how every other consumer enumerates it.
    /// </summary>
    /// <remarks>
    ///     ⚠ The directories rather than the folder, for the reason <c>EditorEffects.Sources</c>
    ///     gives: <c>Example1.rvn</c> sits at the root and imports packages this library does not
    ///     have, so including it fails every shader in the compilation rather than only itself.
    /// </remarks>
    static IEnumerable<string> LibraryFiles() {
        foreach (var package in Directory.EnumerateDirectories(LibraryRoot).Order(StringComparer.Ordinal)) {
            foreach (var file in Directory.EnumerateFiles(package, "*.rvn", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal)) {
                yield return file;
            }
        }
    }

    /// <summary>
    ///     Binds the generated feature into the material chain, against the whole shipped library.
    /// </summary>
    /// <remarks>
    ///     The bindings are <c>MaterialCompiler</c>'s own defaults with the graph in the first chain
    ///     slot — every slot the library declares filled, because Raven refuses a compilation with an
    ///     unbound slot wherever it is declared and the library declares a great many.
    /// </remarks>
    static void BindsIntoTheChain(ShaderGraphSource source) {
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

        List<KeyValuePair<string, string>> bindings = [
            new("surface", "CompositeSurface"),
            new("shading", "StandardShading"),
            new("first", source.Name)
        ];

        foreach (var slot in (string[])["second", "third", "fourth", "fifth", "sixth", "seventh", "eighth"]) {
            bindings.Add(new(slot, "IdentitySurface"));
        }

        foreach (var slot in (string[])["under", "over"]) {
            bindings.Add(new(slot, "IdentitySurface"));
        }

        bindings.Add(new("distanceField", "NoDistanceField"));
        bindings.Add(new("irradiance", "NoIrradiance"));
        bindings.Add(new("punctualShadow", "NoPunctualShadows"));
        bindings.Add(new("directionalShadow", "NoDirectionalShadows"));
        bindings.Add(new("surfaceCache", "NoSurfaceCache"));
        bindings.Add(new("miss", "NoReflectionMiss"));

        var compilation = Compilation.Create(
            "SurfaceGraph",
            PermutationValues.Empty,
            ComposeBindings.Create(bindings),
            trees
        );

        var diagnostics = compilation.GetDiagnostics();

        Assert.True(
            diagnostics.Count == 0,
            "The generated feature did not bind into the material chain:\n"
            + string.Join("\n", diagnostics.Select(diagnostic => diagnostic.ToString()))
            + "\n\n"
            + source.Source
        );
    }

    /// <summary>The simplest surface there is: a constant colour, composed and lit by the scene.</summary>
    [Fact]
    public void A_surface_master_emits_a_material_feature_that_binds_into_the_chain() {
        var graph = new NodeGraphModel { Name = "FlatSurface" };
        var master = graph.Add("Master/Surface");

        master.SetValue("BaseColour", 0.8f, 0.2f, 0.2f);

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Equal(ShaderGraphKind.Surface, result.Value.Kind);
        Assert.Contains("shader FlatSurface : IMaterialSurface {", result.Value.Source, StringComparison.Ordinal);
        Assert.Contains("func Compute(inout d: MaterialData) {", result.Value.Source, StringComparison.Ordinal);

        // Nothing of the standalone shape survives: no transform it cannot supply, no stage it is
        // not in, and no return from a function whose contribution is the struct it was handed.
        Assert.DoesNotContain("worldViewProjection", result.Value.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("[VertexShader]", result.Value.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("return", result.Value.Source, StringComparison.Ordinal);

        BindsIntoTheChain(result.Value);
    }

    /// <summary>
    ///     A textured surface reads the frame's shared table, and says which slot pairs with which
    ///     texture.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The whole of what makes a graph material textured</b>, and it is not the same
    ///     mechanism a standalone graph uses. A feature is composed into a pass it has never seen, so
    ///     it cannot own a binding index; it declares a <c>uint</c> and indexes
    ///     <c>MaterialTextures</c>'s shared array, which is what every hand-written textured feature
    ///     in the library does. <see cref="ShaderGraphSource.Maps" /> is the pairing a host feeds
    ///     <c>MaterialRenderFeature.TextureIndices</c>.
    /// </remarks>
    [Fact]
    public void A_textured_surface_reads_the_shared_table_and_names_the_pairing() {
        var graph = new NodeGraphModel { Name = "TexturedSurface" };
        var sample = graph.Add("Texture/Sample 2D");
        var master = graph.Add("Master/Surface");

        sample.SetText(ShaderProperties.Key, "albedo");
        graph.Connect(new(sample.Id, "RGBA"), new(master.Id, "BaseColour"));

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        // Inherited, because this graph samples — and the declaration is `[Shared]`, so it is the
        // frame's one table rather than a second of them.
        Assert.Contains(
            "shader TexturedSurface : MaterialTextures, IMaterialSurface {",
            result.Value.Source,
            StringComparison.Ordinal
        );

        // A slot, not a texture: the index is what a feature can own and the binding is what it
        // cannot.
        Assert.Contains("var albedoIndex: uint", result.Value.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("Texture2D", result.Value.Source, StringComparison.Ordinal);
        Assert.DoesNotContain(": Sampler", result.Value.Source, StringComparison.Ordinal);

        // The sampler it reads through is the table's shared one, which is the only sampler a
        // feature is allowed to have an opinion about.
        Assert.Contains("materialSampler", result.Value.Source, StringComparison.Ordinal);

        var map = Assert.Single(result.Value.Maps);

        Assert.Equal("albedo", map.Texture);
        Assert.Equal("albedoIndex", map.Slot);

        BindsIntoTheChain(result.Value);
    }

    /// <summary>
    ///     A graph with no texture does not inherit the table, so it pays for no descriptor it never
    ///     reads.
    /// </summary>
    [Fact]
    public void A_surface_that_samples_nothing_does_not_inherit_the_table() {
        var graph = new NodeGraphModel { Name = "Untextured" };

        graph.Add("Master/Surface");

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("MaterialTextures", result.Value.Source, StringComparison.Ordinal);
        Assert.Empty(result.Value.Maps);
    }

    /// <summary>A coordinate the graph computed is the coordinate the texture is read at.</summary>
    /// <remarks>
    ///     ⚠ <b>The reason the emitter indexes the table itself rather than calling
    ///     <c>SampleSurface</c>.</b> That helper samples at <c>d.uv</c> unconditionally, so a graph
    ///     with a <c>Tiling and Offset</c> node in it would compile, draw, and sample somewhere the
    ///     author did not ask for — a wrong image with nothing to blame it on.
    /// </remarks>
    [Fact]
    public void A_computed_coordinate_reaches_the_sample() {
        var graph = new NodeGraphModel { Name = "Tiled" };
        var uv = graph.Add("Input/UV");
        var tiling = graph.Add("Vector/Tiling and Offset");
        var sample = graph.Add("Texture/Sample 2D");
        var master = graph.Add("Master/Surface");

        graph.Connect(new(uv.Id, "UV"), new(tiling.Id, "UV"));
        graph.Connect(new(tiling.Id, "Out"), new(sample.Id, "UV"));
        graph.Connect(new(sample.Id, "RGBA"), new(master.Id, "BaseColour"));

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        // The tiling node's own output, not `d.uv`, is what the sample reads at.
        // `NodeGraphCompiler.Variable` spells an output `n{id}_{port}`.
        Assert.Contains(
            $".Sample(materialSampler, n{tiling.Id.Value}_Out)",
            result.Value.Source,
            StringComparison.Ordinal
        );

        BindsIntoTheChain(result.Value);
    }

    /// <summary>The UV a feature reads is the one the pass already interpolated onto the surface.</summary>
    [Fact]
    public void An_unwired_coordinate_is_the_surface_own() {
        var graph = new NodeGraphModel { Name = "Direct" };
        var sample = graph.Add("Texture/Sample 2D");
        var master = graph.Add("Master/Surface");

        graph.Connect(new(sample.Id, "RGBA"), new(master.Id, "BaseColour"));

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Contains(".Sample(materialSampler, d.uv)", result.Value.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("stream var", result.Value.Source, StringComparison.Ordinal);
    }

    /// <summary>A wired normal bends the shading normal through the pass's own frame.</summary>
    [Fact]
    public void A_wired_normal_is_rotated_by_the_frame_the_pass_supplied() {
        var graph = new NodeGraphModel { Name = "Bumped" };
        var sample = graph.Add("Texture/Sample 2D");
        var master = graph.Add("Master/Surface");

        sample.SetText(ShaderProperties.Key, "normalMap");
        graph.Connect(new(sample.Id, "RGBA"), new(master.Id, "Normal"));

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Contains("d.normalWS = Normals.ToWorld(d.tangentFrame,", result.Value.Source, StringComparison.Ordinal);

        BindsIntoTheChain(result.Value);
    }

    /// <summary>
    ///     An unwired normal writes nothing, so a feature earlier in the chain keeps the normal it
    ///     wrote.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not merely an optimisation. The default an unconnected port carries is (0, 0, 1), which
    ///     <c>ToWorld</c> maps back to the geometric normal — so emitting it anyway would overwrite a
    ///     normal map that a previous feature in the chain had already applied, which is a wrong
    ///     image rather than a redundant instruction.
    /// </remarks>
    [Fact]
    public void An_unwired_normal_writes_nothing() {
        var graph = new NodeGraphModel { Name = "Smooth" };

        graph.Add("Master/Surface");

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("d.normalWS", result.Value.Source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A graph reading what no pass promises a feature is refused, and the node that read it is
    ///     named.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The decision the surface path turns on.</b> <c>MaterialData</c> carries no position
    ///     and no vertex colour, and both plausible substitutes — the origin, white — compile, draw,
    ///     and produce a surface lit as though the graph said something it did not. A refusal is the
    ///     only outcome an author finds out about.
    /// </remarks>
    [Theory]
    [InlineData("Input/World Position", "worldPosition")]
    [InlineData("Input/Vertex Colour", "vertexColour")]
    public void A_surface_cannot_read_what_a_feature_is_not_given(string type, string named) {
        var graph = new NodeGraphModel { Name = "Impossible" };
        var input = graph.Add(type);
        var master = graph.Add("Master/Surface");

        graph.Connect(new(input.Id, input.Type == "Input/World Position" ? "Position" : "Colour"), new(master.Id, "BaseColour"));

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.False(result.Succeeded);

        var diagnostic = Assert.Single(result.Diagnostics, candidate => candidate.Id == "SG0004");

        Assert.Contains(named, diagnostic.Message, StringComparison.Ordinal);

        // And it names the node that read it, not the master and not nothing — which is the whole
        // difference between a message an author can act on and a line number in generated text.
        Assert.Equal(input.Id, diagnostic.Node);
    }

    /// <summary>The three standalone masters still emit a whole shader, untouched by any of this.</summary>
    [Theory]
    [InlineData("Master/Unlit")]
    [InlineData("Master/Sprite")]
    [InlineData("Master/PBR")]
    public void The_standalone_masters_are_unchanged(string master) {
        var graph = new NodeGraphModel { Name = "Standalone" };

        graph.Add(master);

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Equal(ShaderGraphKind.Standalone, result.Value.Kind);
        Assert.Contains("[VertexShader]", result.Value.Source, StringComparison.Ordinal);
        Assert.Contains("[FragmentShader]", result.Value.Source, StringComparison.Ordinal);
        Assert.Contains("var worldViewProjection: mat4", result.Value.Source, StringComparison.Ordinal);
        Assert.Empty(result.Value.Maps);
    }

    /// <summary>A standalone graph still declares its own texture and its own sampler.</summary>
    [Fact]
    public void A_standalone_graph_still_owns_its_bindings() {
        var graph = new NodeGraphModel { Name = "OwnBindings" };
        var sample = graph.Add("Texture/Sample 2D");
        var master = graph.Add("Master/Unlit");

        sample.SetText(ShaderProperties.Key, "albedo");
        graph.Connect(new(sample.Id, "RGBA"), new(master.Id, "Colour"));

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Contains("var albedo: Texture2D", result.Value.Source, StringComparison.Ordinal);
        Assert.Contains("var albedoSampler: Sampler", result.Value.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("materialTextures", result.Value.Source, StringComparison.Ordinal);
    }

    /// <summary>A node's span still points at the line it wrote, in the surface shape too.</summary>
    /// <remarks>
    ///     ⚠ <b>The offset is different arithmetic in each shape and there is nothing to notice when
    ///     it drifts.</b> Both count the compiler's own header and then add it to spans the emitter
    ///     numbered from zero, but the surface header is four imports and no vertex stage while the
    ///     standalone one is two transforms and every varying the graph asked for. A span off by a
    ///     line still resolves to a node, and the squiggle lands on the statement above or below the
    ///     one the compiler complained about — which reads as the diagnostic being vague rather than
    ///     as a map being wrong.
    /// </remarks>
    [Fact]
    public void A_span_points_at_the_written_line_in_a_surface_too() {
        var graph = new NodeGraphModel { Name = "Mapped" };
        var uv = graph.Add("Input/UV");
        var tiling = graph.Add("Vector/Tiling and Offset");
        var sample = graph.Add("Texture/Sample 2D");
        var master = graph.Add("Master/Surface");

        graph.Connect(new(uv.Id, "UV"), new(tiling.Id, "UV"));
        graph.Connect(new(tiling.Id, "Out"), new(sample.Id, "UV"));
        graph.Connect(new(sample.Id, "RGBA"), new(master.Id, "BaseColour"));

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        var lines = result.Value.Source.ReplaceLineEndings("\n").Split('\n');

        foreach (var node in (GraphNode[])[uv, tiling, sample]) {
            // ⚠ `val n{id}_`, not `n{id}_`. The looser form matches a line that *reads* the
            // variable as well as the one that writes it, and in a chain every node's output is read
            // on the line below — so an offset one line out still satisfies it. Sabotaging the
            // surface path's offset arithmetic left this test green until the predicate said
            // "assigns" rather than "mentions".
            var span = Assert.Single(
                result.Value.Spans,
                candidate => candidate.Node == node.Id
                    && lines[candidate.Span.Line].Contains($"val n{node.Id.Value}_", StringComparison.Ordinal)
            );

            Assert.Equal(1, span.Span.Lines);
        }

        // The header belongs to nobody, so a complaint about the package line or an import is a line
        // number rather than the nearest node.
        Assert.False(result.Value.NodeAt(0, out _));

        // And a property's declaration is owned by the node that asked for it, which is where an
        // author is sent when the name they typed is refused.
        var declaration = Assert.Single(
            result.Value.Spans,
            candidate => candidate.Node == sample.Id && lines[candidate.Span.Line].Contains("var albedoIndex", StringComparison.Ordinal)
        );

        Assert.Equal(1, declaration.Span.Lines);
    }
}
