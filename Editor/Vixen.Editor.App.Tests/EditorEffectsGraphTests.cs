// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.Testing;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>A project's shader graphs reach the editor's effect compilation, and a broken one is named (#1300).</summary>
/// <remarks>
///     <para>
///         <b>Written against the tree where <c>EditorEffects</c> called <c>ShaderGraphSources.All</c>
///         by name, and kept green across the seam that replaced it.</b> Nothing asserted this end to
///         end: <c>EditorEffectsTests</c> builds the tier over a project with no graphs, and
///         <c>ShaderGraphSourceTests</c> compiles graphs that no editor ever asks for. The graphs are
///         what a material authored in the shader graph resolves through; without them the material
///         is an effect miss with nothing said.
///     </para>
///     <para>
///         <b>A differential and a name</b>: deleting the one good graph and rebuilding drops the
///         source count by exactly one, and the broken one is in <c>GraphRefusals</c> by file name
///         with its <c>SG0003</c>. A tier that saw no graphs fails both.
///     </para>
/// </remarks>
public sealed class EditorEffectsGraphTests : IDisposable {
    readonly NullDevice device = new();
    readonly List<IDisposable> owned = [];

    public void Dispose() {
        for (var index = owned.Count - 1; index >= 0; index--) {
            owned[index].Dispose();
        }

        device.Dispose();
    }

    [Fact]
    public void A_projects_shader_graphs_are_compiled_with_the_editors_effects() {
        var session = EditorSession.Start();

        owned.Add(session);

        var painted = Graph(session, "Painted", "Master/Surface");

        Graph(session, "Unfinished", "Input/UV");

        session.Application.GraphicsDevice = device;
        session.Frame();

        var effects = session.Application.Effects;

        Assert.NotNull(effects);

        var refusal = Assert.Single(effects.GraphRefusals);

        Assert.StartsWith("Unfinished.vxshadergraph", refusal, StringComparison.Ordinal);
        Assert.Contains("SG0003", refusal, StringComparison.Ordinal);

        var withGraph = effects.SourceCount;

        File.Delete(painted);
        effects.Rebuild();

        Assert.Equal(withGraph - 1, effects.SourceCount);
    }

    static string Graph(EditorSession session, string name, string node) {
        var graph = new NodeGraphModel { Name = name };

        graph.Add(node);

        var path = session.Project.Paths.Absolute("Assets/" + name + ".vxshadergraph");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, YamlSerializer.ToYaml(NodeGraphDocument.Save(graph)));

        return path;
    }
}
