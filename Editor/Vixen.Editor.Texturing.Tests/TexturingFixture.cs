// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A host with a project in it, thrown away when the test finishes.</summary>
/// <remarks>
///     ⚠ <b>A real <c>EditorShell</c> and a real <c>PluginHost</c>, not doubles.</b> What is being
///     asserted is that a module registers through the contract and that unloading undoes it — and a
///     shell that recorded registrations without a command registry, a workspace and a menu bar
///     behind them would be a double more permissive than the runtime, which is the failure mode this
///     repository names by that phrase.
/// </remarks>
sealed class TexturingFixture : IDisposable {
    /// <summary>Where the throwaway project is.</summary>
    public ProjectPaths Paths { get; }

    /// <summary>The project the module is activated against.</summary>
    public EditorProject Project { get; }

    /// <summary>The editor's chrome.</summary>
    public EditorShell Shell { get; } = new(1280f, 800f);

    /// <summary>What the host publishes to plugins.</summary>
    public PluginServices Services { get; } = new();

    /// <summary>The contribution registry, kept so a test can read what was contributed.</summary>
    /// <remarks>
    ///     Its own rather than <c>EditorRegistry.Default</c>, which is process-wide: two tests
    ///     registering one kind must not be unable to run in the same process. That is the registry's
    ///     own stated reason for the static being a default rather than the answer.
    /// </remarks>
    public EditorRegistry Extensions { get; } = new();

    /// <summary>The loader the module is activated through.</summary>
    public PluginHost Host { get; }

    /// <summary>Builds it.</summary>
    public TexturingFixture() {
        Paths = new(Path.Combine(Path.GetTempPath(), "vixen-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Paths.Assets);

        Project = new EditorProject(Paths);

        Services.Add(Project);
        Services.Add<IEditorRegistry>(Extensions);

        Host = new PluginHost(Shell, Services);
    }

    /// <summary>Writes a <c>.vxtexgraph</c> and its sidecar, and scans it in.</summary>
    /// <param name="name">What to call it, without the extension.</param>
    /// <param name="contents">What is in it. Empty is the ordinary new one.</param>
    /// <returns>Its id.</returns>
    public AssetId AddGraph(string name, string contents = "") {
        var relative = "Assets/" + name + TextureGraphDocument.Extension;
        var absolute = Paths.Absolute(relative);

        File.WriteAllText(absolute, contents);

        var report = Project.Assets.Scan();

        // ⚠ A `MetaCreated` is what a scan is *for* — the sidecar is written the first time the
        // database sees a file, so a fixture that demanded no issues would be one that could never
        // add an asset. Anything else is a fixture that has gone wrong and must not be silent.
        Assert.DoesNotContain(report.Issues, issue => issue.Kind != AssetIssueKind.MetaCreated);
        Assert.True(Project.Assets.TryGetByPath(relative, out var entry), "the scan did not pick the graph up");

        return entry.Guid;
    }

    /// <inheritdoc />
    public void Dispose() {
        Shell.Dispose();

        try {
            if (Directory.Exists(Paths.Root)) {
                Directory.Delete(Paths.Root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }
}
