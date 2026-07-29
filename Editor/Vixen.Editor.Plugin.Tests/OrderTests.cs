// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Editor.Plugin.Tests;

/// <summary>Which order plugins activate in, and what happens when they cannot.</summary>
public class OrderTests {
    static PluginDescriptor Plugin(string id, params string[] dependencies) =>
        new(
            new PluginManifest {
                Id = id,
                Name = id,
                Api = EditorApi.Version,
                Dependencies = [.. dependencies]
            },
            "/plugins/" + id,
            "/plugins/" + id + "/plugin.yaml",
            "/plugins/" + id + "/" + id + ".dll"
        );

    [Fact]
    public void A_plugin_follows_what_it_depends_on() {
        List<PluginDiagnostic> diagnostics = [];
        var ordered = PluginOrder.Sort([Plugin("c", "b"), Plugin("b", "a"), Plugin("a")], diagnostics);

        Assert.Equal(["a", "b", "c"], ordered.Select(plugin => plugin.Id));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Plugins_that_depend_on_nothing_keep_the_order_they_were_found_in() {
        List<PluginDiagnostic> diagnostics = [];
        var ordered = PluginOrder.Sort([Plugin("b"), Plugin("a"), Plugin("c")], diagnostics);

        // Discovery order is the tie-break, so two runs over the same folder activate in the same
        // order twice.
        Assert.Equal(["b", "a", "c"], ordered.Select(plugin => plugin.Id));
    }

    [Fact]
    public void A_missing_dependency_stops_the_plugin_and_everything_behind_it() {
        List<PluginDiagnostic> diagnostics = [];
        var ordered = PluginOrder.Sort([Plugin("a", "absent"), Plugin("b", "a"), Plugin("c")], diagnostics);

        // Activating it anyway would move the failure inside the plugin's own code, where it
        // surfaces as a null service or an id nothing registered — the same bug, reported worse.
        Assert.Equal(["c"], ordered.Select(plugin => plugin.Id));

        Assert.Contains(diagnostics, diagnostic => diagnostic.PluginId == "a" && diagnostic.Message.Contains("not installed", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.PluginId == "b" && diagnostic.Message.Contains("did not load", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cycle_is_reported_once_and_names_its_members() {
        List<PluginDiagnostic> diagnostics = [];
        var ordered = PluginOrder.Sort([Plugin("a", "b"), Plugin("b", "c"), Plugin("c", "a")], diagnostics);

        Assert.Empty(ordered);
        Assert.Single(diagnostics);

        // One diagnostic for one mistake, and it names the whole loop rather than leaving the
        // author to find it.
        Assert.Contains("a → b → c → a", diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_outside_a_cycle_that_depends_on_one_is_told_so() {
        List<PluginDiagnostic> diagnostics = [];
        var ordered = PluginOrder.Sort([Plugin("a", "b"), Plugin("b", "a"), Plugin("d", "a")], diagnostics);

        Assert.Empty(ordered);
        Assert.Contains(diagnostics, diagnostic => diagnostic.PluginId == "d" && diagnostic.Message.Contains("did not load", StringComparison.Ordinal));
    }

    [Fact]
    public void A_diamond_activates_each_plugin_once() {
        List<PluginDiagnostic> diagnostics = [];
        var ordered = PluginOrder.Sort([Plugin("top", "left", "right"), Plugin("left", "base"), Plugin("right", "base"), Plugin("base")], diagnostics);

        Assert.Equal(["base", "left", "right", "top"], ordered.Select(plugin => plugin.Id));
        Assert.Empty(diagnostics);
    }
}
