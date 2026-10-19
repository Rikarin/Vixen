// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Ui;
using Vixen.Editor.Ui.Documents;
using Vixen.Graphics;
using Vixen.Ui;
using Vixen.Ui.Styling;
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

    /// <summary>Where an editor claiming a file extension goes, when the test publishes one.</summary>
    public DocumentHost Editors { get; }

    /// <summary>Where the view showing a claimed file goes, published with <see cref="Editors" /> (#1294).</summary>
    public EditorWorkspace Views { get; } = new();

    /// <summary>The loader the module is activated through.</summary>
    public PluginHost Host { get; }

    /// <summary>Builds it.</summary>
    /// <param name="device">
    ///     A device to publish graphics over, or <see langword="null" /> to publish none. ⚠ Publishing
    ///     an <see cref="IEditorGraphics" /> with a null device is a third state and a real one — the
    ///     editor is in it between construction and the window coming up — so it is spelled by
    ///     <paramref name="graphics" /> rather than inferred from this.
    /// </param>
    /// <param name="graphics">Whether to publish graphics at all. Defaults to "when there is a device".</param>
    /// <param name="editors">Whether to publish an <see cref="IDocumentHost" /> and an <see cref="IWorkspace" />.</param>
    public TexturingFixture(IGraphicsDevice? device = null, bool? graphics = null, bool editors = false) {
        Paths = new(Path.Combine(Path.GetTempPath(), "vixen-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Paths.Assets);

        Project = new EditorProject(Paths);
        Editors = new DocumentHost(Project);

        Services.Add(Project);
        Services.Add<IEditorRegistry>(Extensions);

        if (graphics ?? device is not null) {
            Graphics = new RecordingGraphics(device);
            Services.Add<IEditorGraphics>(Graphics);
        }

        if (editors) {
            Services.Add<IDocumentHost>(Editors);
            Services.Add<IWorkspace>(Views);
        }

        Host = new PluginHost(Shell, Services);
    }

    /// <summary>What the module drew through, when this fixture published any.</summary>
    public RecordingGraphics? Graphics { get; }

    /// <summary>A host for a view built without the module, in a document holding the module's sheet.</summary>
    /// <returns>An element under the shell's root.</returns>
    /// <remarks>
    ///     ⚠ <b>The sheet is the module's since #1486</b>: <c>TexturingModule.Activate</c> loads it and an
    ///     unload takes it out, and no view installs it any more. A test that builds a pane directly
    ///     loads it here, as activating the module would, or the pane lays out as a strip and every
    ///     hit test against it lands on nothing.
    /// </remarks>
    public UiElement ThemedHost() {
        Shell.Document.Load(TexturingTheme.Css, StyleOrigin.UserAgent);

        return Shell.Document.Root.Add<UiElement>();
    }

    /// <summary>Writes a <c>.vxtexgraph</c> and its sidecar, and scans it in.</summary>
    /// <param name="name">What to call it, without the extension.</param>
    /// <param name="contents">What is in it. Empty is the ordinary new one.</param>
    /// <returns>Its id.</returns>
    public AssetId AddGraph(string name, string contents = "") => AddAsset(name, TextureGraphDocument.Extension, contents);

    /// <summary>Writes a <c>.vxlayers</c> and its sidecar, and scans it in.</summary>
    /// <param name="name">What to call it, without the extension.</param>
    /// <param name="contents">What is in it. Empty is the ordinary new one.</param>
    /// <returns>Its id.</returns>
    /// <remarks>
    ///     ⚠ <b>Its own method rather than a second extension parameter on <see cref="AddGraph" />,
    ///     because the two are asked for by name at forty call sites.</b> Both go through
    ///     <see cref="AddAsset" />, so the scan and the issue assertion are one copy.
    /// </remarks>
    public AssetId AddStack(string name, string contents = "") =>
        AddAsset(name, LayerStackDocument.Extension, contents);

    /// <summary>Writes a file under <c>Assets/</c> and scans it in.</summary>
    /// <param name="name">What to call it, without the extension.</param>
    /// <param name="extension">The extension, dot and all.</param>
    /// <param name="contents">What is in it.</param>
    /// <returns>Its id.</returns>
    public AssetId AddAsset(string name, string extension, string contents) {
        var relative = "Assets/" + name + extension;
        var absolute = Paths.Absolute(relative);

        File.WriteAllText(absolute, contents);

        var report = Project.Assets.Scan();

        // ⚠ A `MetaCreated` is what a scan is *for* — the sidecar is written the first time the
        // database sees a file, so a fixture that demanded no issues would be one that could never
        // add an asset. Anything else is a fixture that has gone wrong and must not be silent.
        Assert.DoesNotContain(report.Issues, issue => issue.Kind != AssetIssueKind.MetaCreated);
        Assert.True(Project.Assets.TryGetByPath(relative, out var entry), "the scan did not pick " + relative + " up");

        return entry.Guid;
    }

    /// <summary>Waits for a task on this thread rather than awaiting it, so the test stays where its graph is.</summary>
    /// <typeparam name="T">What the task produces.</typeparam>
    /// <param name="work">The task — an import, typically.</param>
    /// <returns>Its result.</returns>
    /// <remarks>
    ///     ⚠ <b>A block, deliberately, and safe here for a reason worth keeping true:</b> xunit gives
    ///     these tests no synchronisation context, and the awaited work (<c>ContentPipeline</c>) uses
    ///     <c>ConfigureAwait(false)</c>, so nothing it waits for needs this thread back. See
    ///     <see cref="Dispose" /> for why an <c>await</c> is the wrong tool while a fixture is alive.
    /// </remarks>
    public static T Wait<T>(Task<T> work) => work.GetAwaiter().GetResult();

    /// <summary>The thread the fixture's reactive graph was built on, which is the only one it may be torn down on.</summary>
    readonly int owner = Environment.CurrentManagedThreadId;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Refuses a fixture disposed on a thread other than the one that built it</b>
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/1413">#1413</a>). xunit runs these tests on
    ///     pool threads with no synchronisation context, so an <c>async</c> test resumes wherever its
    ///     awaited task completed, and the thread it left is handed the next test. Every label the
    ///     panel drew is a live consumer of the building thread's <c>Strings</c> node, so tearing the
    ///     panel down elsewhere removes those consumers from a node that thread is now using for
    ///     another test's graph — the torn edge array <c>StringsThreadingTests</c> shows, and a reading
    ///     like a <c>Depth</c> of 0 beside an <c>IsDirty</c> of true. An await inside a test that holds
    ///     one of these is therefore blocked on rather than awaited; this is what says so if one is
    ///     added. The fixture is still disposed first, so a refusal leaks nothing.
    /// </remarks>
    public void Dispose() {
        Shell.Dispose();

        try {
            if (Directory.Exists(Paths.Root)) {
                Directory.Delete(Paths.Root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }

        if (Environment.CurrentManagedThreadId != owner) {
            throw new InvalidOperationException(
                $"This fixture was built on thread {owner} and disposed on thread "
                + $"{Environment.CurrentManagedThreadId}: an await moved the test, and its panel's graph went "
                + "with it while the old thread ran another test on the same Strings node. Block on the "
                + "task instead of awaiting it while a TexturingFixture is alive (#1413)."
            );
        }
    }
}
