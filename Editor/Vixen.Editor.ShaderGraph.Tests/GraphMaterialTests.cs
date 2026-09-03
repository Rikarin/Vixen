// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Materials;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     A graph-authored material compiled the way a frame compiles one, against what Raven actually
///     emitted for it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the oracle, and without it the material half of the graph story is a
///         prediction checked against itself.</b> <c>MaterialCompiler</c> works out what a composed
///         feature's parameters will be called with no compiler in the process — it has to, because a
///         shipping build must build the key that finds a baked effect without linking Raven. The
///         cost is a rule written down twice, and <c>MaterialReflectionTests</c> holds the engine's
///         side of it against a checked-in <c>ForwardPlus.reflect.json</c>. A graph's shader is
///         generated per project, so there is no file to check in: the reflection has to be produced
///         here, from the same text the graph emitted.
///     </para>
///     <para>
///         <b>What makes it worth the seconds it costs is what fails when it is wrong.</b> A value
///         written under a name no layout asks for is dropped in silence — the material draws, lit
///         and plausible, with the author's colour nowhere in it. There is no error anywhere in that
///         sequence, on any device.
///     </para>
///     <para>
///         <b>And it proves more than the names.</b> Getting an <see cref="EffectData" /> back at all
///         means the generated feature compiled into <c>ForwardPlus</c>, through the real
///         composition, all the way to SPIR-V — which is the variant a device would be handed.
///     </para>
/// </remarks>
public class GraphMaterialTests {
    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();

        NodeTypes.Register(registry);

        return registry;
    }

    static string LibraryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Raven", "Library"));

    /// <inheritdoc cref="SurfaceGraphTests.LibraryFiles" />
    static IEnumerable<string> LibraryFiles() {
        foreach (var package in Directory.EnumerateDirectories(LibraryRoot).Order(StringComparer.Ordinal)) {
            foreach (var file in Directory.EnumerateFiles(package, "*.rvn", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal)) {
                yield return file;
            }
        }
    }

    /// <summary>A graph with one of each thing a material supplies: a number, a colour and a map.</summary>
    static ShaderGraphSource Compiled() {
        var graph = new NodeGraphModel { Name = "AuthoredSurface" };
        var tint = graph.Add("Input/Colour Property");
        var rough = graph.Add("Input/Float Property");
        var sample = graph.Add("Texture/Sample 2D");
        var multiply = graph.Add("Math/Multiply");
        var master = graph.Add("Master/Surface");

        tint.SetText(ShaderProperties.Key, "tint");
        rough.SetText(ShaderProperties.Key, "roughness");
        sample.SetText(ShaderProperties.Key, "albedo");

        graph.Connect(new(sample.Id, "RGBA"), new(multiply.Id, "A"));
        graph.Connect(new(tint.Id, "Colour"), new(multiply.Id, "B"));
        graph.Connect(new(multiply.Id, "Out"), new(master.Id, "BaseColour"));
        graph.Connect(new(rough.Id, "Out"), new(master.Id, "Roughness"));

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        return result.Value;
    }

    /// <summary>The material a frame would draw that graph with.</summary>
    static (Material Material, ShaderGraphSource Source) Authored() {
        var source = Compiled();

        var feature = ShaderGraphMaterial.Feature(
            source,
            new Dictionary<string, Vector4>(StringComparer.Ordinal) {
                ["tint"] = new(0.8f, 0.2f, 0.2f, 1f),
                ["roughness"] = new(0.35f, 0f, 0f, 0f)
            }
        );

        var compilation = MaterialCompiler.Compile(
            new() { ShaderName = "ForwardPlus", Features = [feature], Shading = new StandardShading() }
        );

        Assert.False(compilation.Failed, string.Join("\n", compilation.Diagnostics));

        return (compilation.Material!, source);
    }

    /// <summary>Compiles <c>ForwardPlus</c> with the graph in the chain, and hands back the variant.</summary>
    static EffectData Variant(Material material, ShaderGraphSource source) {
        List<(string Name, string Text)> sources = [
            .. LibraryFiles().Select(file => (Path.GetFileName(file), File.ReadAllText(file))),
            (source.Name + ".rvn", source.Source)
        ];

        var compiler = RavenEffectCompiler.FromSources(sources);
        var data = compiler.TryGet(EffectKey.Of(material.ShaderName).With(material.Composition));

        Assert.NotNull(data);

        return data;
    }

    /// <summary>
    ///     A graph-authored feature composes into the chain and compiles into the pass, end to end.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not a formality. Every step before this one can be right and this still fail — a slot
    ///     the material left unfilled, a shader the composition names that the compilation has not
    ///     got, a feature that binds alone and refuses composed. What a failure here looks like in a
    ///     running editor is an effect miss: a draw that does not happen, with nothing on screen and
    ///     nothing in the log about a material.
    /// </remarks>
    [Fact]
    public void A_graph_authored_material_compiles_into_the_forward_pass() {
        var (material, source) = Authored();

        Assert.Equal(
            source.Name,
            material.Composition.Resolve($"{MaterialCompiler.ChainShader}.first")
        );

        var data = Variant(material, source);

        Assert.NotEmpty(data.Stages);

        foreach (var stage in data.Stages) {
            Assert.NotEmpty(stage.Bytecode);
        }
    }

    /// <summary>
    ///     Every value the material writes is a parameter the compiled shader actually has.
    /// </summary>
    /// <remarks>
    ///     The direction that catches a name predicted wrongly. A value written under a name no
    ///     layout asks for is dropped in silence, so the material draws lit and plausible with the
    ///     author's colour nowhere in it.
    /// </remarks>
    [Fact]
    public void Every_value_the_material_writes_is_in_the_compiled_shader() {
        var (material, source) = Authored();
        var data = Variant(material, source);

        // ⚠ Not prefixed with the pass. `EffectData.Parameters` already carries the engine's
        // qualified key — `ForwardPlus.CompositeSurface.<graph>.tint` — which is *not* what the
        // checked-in `reflect.json` holds, and is why `MaterialReflectionTests` adds a prefix and
        // this does not. Predicting otherwise is how this test first ran: every name doubled, and
        // every assertion red for a reason that had nothing to do with the graph.
        var reflected = data.Parameters
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);

        var written = material.Parameters.Keys
            .Select(key => key.Name)
            .Where(name => name.Contains($".{source.Name}.", StringComparison.Ordinal))
            .ToArray();

        // Three: the colour, the number and the texture slot. Asserted so that a conversion which
        // quietly produced no parameters at all cannot pass the loop below by having nothing to
        // check — which is the shape of vacuous pass this repository keeps finding.
        Assert.Equal(3, written.Length);

        foreach (var name in written) {
            Assert.Contains(name, reflected);
        }
    }

    /// <summary>
    ///     Every composed parameter the shader has is one the material writes.
    /// </summary>
    /// <remarks>
    ///     The other direction, and the one that catches a missing value: a parameter the material
    ///     never writes takes the shader's declared default, which is a plausible-looking image
    ///     rather than an error.
    /// </remarks>
    [Fact]
    public void Every_parameter_the_graph_shader_has_is_one_the_material_writes() {
        var (material, source) = Authored();
        var data = Variant(material, source);

        var written = material.Parameters.Keys.Select(key => key.Name).ToHashSet(StringComparer.Ordinal);

        var composed = data.Parameters
            .Select(parameter => parameter.Name)
            .Where(name => name.Contains($".{source.Name}.", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(composed);

        foreach (var name in composed) {
            Assert.Contains(name, written);
        }
    }

    /// <summary>The texture slot is named the same way by the graph and by the material compiler.</summary>
    /// <remarks>
    ///     ⚠ <b>The pairing a host feeds <c>MaterialRenderFeature.TextureIndices</c>, and the one
    ///     place a mismatch is invisible.</b> An unmatched pair leaves the index at zero, which is a
    ///     valid slot holding the table's fallback — so the material samples a real texture that is
    ///     not its own.
    /// </remarks>
    [Fact]
    public void The_texture_slot_the_host_writes_is_the_one_the_shader_declares() {
        var (material, source) = Authored();
        var data = Variant(material, source);

        var map = Assert.Single(source.Maps);

        // What a host would compute, from the composition path the compiler built.
        var predicted = GraphSurfaceFeature.IndexParameter(
            $"{material.ShaderName}.{MaterialCompiler.ChainShader}.{source.Name}.",
            map.Slot
        );

        Assert.Contains(predicted, data.Parameters.Select(parameter => parameter.Name));
    }

    /// <summary>A standalone graph is refused as a material feature, by name.</summary>
    [Fact]
    public void A_standalone_graph_is_not_a_material_feature() {
        var graph = new NodeGraphModel { Name = "Standalone" };

        graph.Add("Master/Unlit");

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        var failure = Assert.Throws<ArgumentException>(() => ShaderGraphMaterial.Feature(result.Value));

        Assert.Contains("Master/Surface", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A feature naming no shader is refused against the material, not composed.</summary>
    [Fact]
    public void A_feature_naming_no_shader_is_refused() {
        var compilation = MaterialCompiler.Compile(
            new() { ShaderName = "ForwardPlus", Features = [new GraphSurfaceFeature()], Shading = new StandardShading() }
        );

        Assert.True(compilation.Failed);
        Assert.Contains(compilation.Diagnostics, d => d.Id == MaterialDiagnosticId.UnnamedShader);
    }

    /// <summary>A texture slot is not offered to an author as a number to type.</summary>
    [Fact]
    public void A_texture_slot_is_not_a_value_a_material_sets() {
        var source = Compiled();

        Assert.Contains(source.Properties, property => property.Name == "albedoIndex");
        Assert.DoesNotContain(ShaderGraphMaterial.Values(source), property => property.Name == "albedoIndex");

        var feature = ShaderGraphMaterial.Feature(source);

        Assert.DoesNotContain(feature.Numbers, number => number.Name == "albedoIndex");
        Assert.DoesNotContain(feature.Vectors, vector => vector.Name == "albedoIndex");
        Assert.Contains(feature.Maps, map => map.Slot == "albedoIndex");
    }
}
