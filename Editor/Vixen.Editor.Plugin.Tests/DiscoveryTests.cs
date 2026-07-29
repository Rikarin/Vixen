// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Editor.Plugin.Tests;

/// <summary>Finding what is installed, before any of it has been allowed to run.</summary>
public class DiscoveryTests {
    [Fact]
    public void A_root_that_does_not_exist_is_not_an_error() {
        // Most projects have no Plugins/ folder, and an editor that warned about it on every launch
        // would be teaching people to ignore its warnings.
        var catalog = PluginDiscovery.Scan(Path.Combine(Path.GetTempPath(), "vixen-plugins-that-are-not-there"));

        Assert.Empty(catalog.Plugins);
        Assert.Empty(catalog.Diagnostics);
    }

    [Fact]
    public void A_folder_with_no_manifest_is_not_a_plugin() {
        using var folder = new PluginFolder();
        Directory.CreateDirectory(Path.Combine(folder.Root, "not-a-plugin"));

        var catalog = PluginDiscovery.Scan(folder.Root);

        // A folder either declares itself or is not a plugin. An editor that loaded whatever DLLs
        // it found under a directory the user can write to has an interesting security model.
        Assert.Empty(catalog.Plugins);
        Assert.Empty(catalog.Diagnostics);
    }

    [Fact]
    public void A_manifest_that_does_not_parse_is_a_diagnostic_rather_than_a_throw() {
        using var folder = new PluginFolder();
        folder.Write("broken", manifest: "id: [unclosed\n");

        var catalog = PluginDiscovery.Scan(folder.Root);

        Assert.Empty(catalog.Plugins);
        Assert.Single(catalog.Diagnostics);
        Assert.Equal(PluginSeverity.Error, catalog.Diagnostics[0].Severity);

        // Filed under the folder's name, because the manifest that would have said the id is the
        // file that could not be read.
        Assert.Equal("broken", catalog.Diagnostics[0].PluginId);
    }

    [Fact]
    public void A_manifest_with_problems_is_reported_against_the_id_it_did_manage_to_state() {
        using var folder = new PluginFolder();
        folder.Write("nameless", manifest: "id: com.example.nameless\napi: 0.1\n");

        var catalog = PluginDiscovery.Scan(folder.Root);

        Assert.Empty(catalog.Plugins);
        Assert.All(catalog.Diagnostics, diagnostic => Assert.Equal("com.example.nameless", diagnostic.PluginId));
    }

    [Fact]
    public void The_assembly_is_found_beside_the_manifest_or_under_a_package_layout() {
        using var folder = new PluginFolder();

        var flat = folder.Write("flat", "public sealed class Nothing { }");
        Assert.Equal(Path.Combine(flat, "flat.dll"), PluginDiscovery.Scan(folder.Root).Find("flat")!.AssemblyPath);

        // The same plugin unzipped from a .nupkg rather than dropped in a folder. Doc 11 says a
        // plugin is "a NuGet package or a folder with an assembly + a manifest"; to a scan they are
        // the same thing and the manifest does not have to say which the author chose.
        var packed = folder.Write("packed", manifest: "id: packed\nname: Packed\napi: 0.1\nassembly: packed.dll\n");
        var library = Directory.CreateDirectory(Path.Combine(packed, "lib", "net10.0"));
        File.WriteAllBytes(Path.Combine(library.FullName, "packed.dll"), [0x4d, 0x5a]);

        Assert.Equal(
            Path.Combine(library.FullName, "packed.dll"),
            PluginDiscovery.Scan(folder.Root).Find("packed")!.AssemblyPath
        );
    }

    [Fact]
    public void An_assembly_that_is_not_there_is_an_empty_path_rather_than_a_refusal() {
        using var folder = new PluginFolder();
        folder.Write("missing");

        // Discovery describes what is on disk; judging it is the loader's, so that "the manifest
        // names an assembly that is not there" is reported beside every other reason a plugin did
        // not start rather than in a different pass.
        var catalog = PluginDiscovery.Scan(folder.Root);

        Assert.Single(catalog.Plugins);
        Assert.Empty(catalog.Plugins[0].AssemblyPath);
    }

    [Fact]
    public void The_first_root_wins_and_the_second_copy_is_reported() {
        using var project = new PluginFolder();
        using var user = new PluginFolder();

        project.Write("com.example.shared");
        user.Write("com.example.shared");

        var catalog = PluginDiscovery.Scan(project.Root, user.Root);

        // Project before user, so a plugin checked into a repository overrides the one the user has
        // installed globally — which is what makes "everybody on this team gets the same tools"
        // true. The copy that lost is reported rather than dropped silently.
        Assert.Single(catalog.Plugins);
        Assert.Equal(project.Root, Path.GetDirectoryName(catalog.Plugins[0].Directory));

        Assert.Single(catalog.Diagnostics);
        Assert.Equal(PluginSeverity.Warning, catalog.Diagnostics[0].Severity);
    }

    [Fact]
    public void Plugins_come_back_in_a_stable_order() {
        using var folder = new PluginFolder();

        folder.Write("c");
        folder.Write("a");
        folder.Write("b");

        // Alphabetical within a root, so the same set of plugins loads in the same order twice and
        // a bug that depends on the order is reproducible rather than a Tuesday thing.
        Assert.Equal(["a", "b", "c"], PluginDiscovery.Scan(folder.Root).Plugins.Select(plugin => plugin.Id));
    }
}
