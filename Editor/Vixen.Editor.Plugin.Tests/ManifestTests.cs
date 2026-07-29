// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Xunit;

namespace Vixen.Editor.Plugin.Tests;

/// <summary>The file a plugin declares itself in, and what the loader can tell from it alone.</summary>
public class ManifestTests {
    [Fact]
    public void A_manifest_reads_through_the_ordinary_yaml_binder() {
        var manifest = YamlSerializer.Parse<PluginManifest>(
            """
            id: com.example.terrain
            name: Terrain Tools
            version: 1.2.3
            api: 0.1
            assembly: Example.Terrain.dll
            entryPoint: Example.Terrain.TerrainPlugin
            description: Sculpting brushes.
            author: Example Ltd
            dependencies:
              - com.example.brushes
            """
        );

        Assert.Equal("com.example.terrain", manifest.Id);
        Assert.Equal("Terrain Tools", manifest.Name);
        Assert.Equal(new Version(1, 2, 3), manifest.Version);
        Assert.Equal(new Version(0, 1), manifest.Api);
        Assert.Equal("Example.Terrain.dll", manifest.AssemblyFileName);
        Assert.Equal("Example.Terrain.TerrainPlugin", manifest.EntryPoint);
        Assert.Equal(["com.example.brushes"], manifest.Dependencies);

        // A plugin is on unless it says otherwise, so a hand-written manifest is three lines.
        Assert.True(manifest.Enabled);
    }

    [Fact]
    public void A_key_a_later_editor_added_does_not_stop_this_one_reading_it() {
        // The property that lets a plugin ship one manifest for two editor versions. An unknown key
        // is ignored by the binder everywhere else in the repository and this is not an exception.
        var manifest = YamlSerializer.Parse<PluginManifest>(
            """
            id: com.example.later
            name: Later
            api: 0.1
            sandbox: strict
            """
        );

        Assert.Equal("com.example.later", manifest.Id);
        Assert.Empty(manifest.Problems());
    }

    [Fact]
    public void An_assembly_that_is_not_named_is_the_id() {
        var manifest = new PluginManifest { Id = "com.example.terrain" };
        Assert.Equal("com.example.terrain.dll", manifest.AssemblyFileName);
    }

    [Fact]
    public void Every_problem_is_reported_at_once() {
        // Not the first: an author fixing a manifest one rejection at a time runs the editor four
        // times to learn four things the first run already knew.
        var problems = new PluginManifest().Problems();

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, problem => problem.Contains("'id'", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("'name'", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("'api'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("com.example.terrain")]
    [InlineData("terrain")]
    [InlineData("com.example.terrain-tools")]
    [InlineData("a1.b2")]
    public void An_id_shaped_like_a_command_id_is_accepted(string id) {
        var manifest = new PluginManifest { Id = id, Name = "n", Api = EditorApi.Version };
        Assert.Empty(manifest.Problems());
    }

    [Theory]
    [InlineData("Com.Example")]
    [InlineData("com..example")]
    [InlineData("com.example.")]
    [InlineData(".com")]
    [InlineData("com example")]
    [InlineData("1com")]
    public void An_id_that_is_not_is_refused(string id) {
        // 'com..example' and 'com.example.' are the two typos a permissive check lets through, and
        // what they produce is a dependency list that silently fails to match.
        var manifest = new PluginManifest { Id = id, Name = "n", Api = EditorApi.Version };
        Assert.Contains(manifest.Problems(), problem => problem.Contains("'id'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_plugin_that_depends_on_itself_is_refused_by_the_manifest() {
        var manifest = new PluginManifest {
            Id = "com.example.a",
            Name = "A",
            Api = EditorApi.Version,
            Dependencies = ["com.example.a"]
        };

        Assert.Contains(manifest.Problems(), problem => problem.Contains("own id", StringComparison.Ordinal));
    }

    [Fact]
    public void The_editor_accepts_the_contract_version_it_implements() {
        Assert.True(EditorApi.IsCompatible(EditorApi.Version));
        Assert.Null(EditorApi.Explain(EditorApi.Version));
    }

    [Fact]
    public void A_different_major_is_never_compatible() {
        Assert.False(EditorApi.IsCompatible(new Version(EditorApi.Version.Major + 1, EditorApi.Version.Minor)));
    }

    [Fact]
    public void Before_one_point_zero_the_minor_is_the_breaking_number() {
        // SemVer's own reading of 0.x, and the honest one for extension points that are still
        // moving: 0.1 and 0.2 are not compatible in either direction and the loader says so.
        Assert.Equal(0, EditorApi.Version.Major);

        Assert.False(EditorApi.IsCompatible(new Version(0, EditorApi.Version.Minor + 1)));
        Assert.False(EditorApi.IsCompatible(new Version(0, EditorApi.Version.Minor - 1)));
    }

    [Fact]
    public void The_explanation_says_which_of_the_two_things_to_update() {
        Assert.Contains(
            "Update the editor",
            EditorApi.Explain(new Version(EditorApi.Version.Major + 1, 0))!,
            StringComparison.Ordinal
        );

        Assert.Contains(
            "Rebuild the plugin",
            EditorApi.Explain(new Version(0, 0))!,
            StringComparison.Ordinal
        );
    }
}
