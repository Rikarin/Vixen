// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Code;
using Vixen.Editor.AssetEditors.Materials;
using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Tests;
using Vixen.Editor.Ui;
using Vixen.Editor.Ui.Documents;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>Which kind claims a file, and what happens when two of them do — <see cref="DocumentHost" /> since #1294.</summary>
public class DocumentHostTests {
    /// <summary>A project over a path nobody touches, for the tests that never open anything.</summary>
    static EditorProject Project() =>
        new(new ProjectPaths(Path.Combine(Path.GetTempPath(), "vixen-document-host-tests")));

    /// <summary>An extension resolves to the kind that claimed it.</summary>
    [Fact]
    public void AnExtensionResolves() {
        var host = StandardEditors.CreateWorldless(Project());

        Assert.True(host.TryGetForFile("Assets/hero.png", out var kind));
        Assert.Equal("Texture", kind!.Name);
    }

    /// <summary>⚠ There is no fallback: a file nothing claims does not open in a text editor by guess.</summary>
    [Fact]
    public void NothingClaimsAnUnknownExtension() =>
        Assert.False(StandardEditors.CreateWorldless(Project()).TryGetForFile("Assets/hero.blob", out _));

    /// <summary>A file with no extension claims nothing rather than throwing.</summary>
    [Fact]
    public void AFileWithNoExtensionClaimsNothing() =>
        Assert.False(StandardEditors.CreateWorldless(Project()).TryGetForFile("Assets/LICENSE", out _));

    /// <summary>⚠ Two kinds claiming one extension is an error naming both, not last-one-wins.</summary>
    [Fact]
    public void TwoClaimantsAreRefused() {
        var host = new DocumentHost(Project());

        host.Add(new MaterialEditorFactory());

        var failure = Assert.Throws<InvalidOperationException>(() => host.Add(new FakeKind("Other", ".vxmat")));

        Assert.Contains(".vxmat", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>And so is one name used twice.</summary>
    [Fact]
    public void TwoKindsCannotShareAName() {
        var host = new DocumentHost(Project());

        host.Add(new MaterialEditorFactory());

        Assert.Throws<InvalidOperationException>(() => host.Add(new FakeKind("Material", ".other")));
    }

    /// <summary>The default set has one kind per row of doc 11's table that this assembly covers.</summary>
    [Fact]
    public void TheDefaultSetIsComplete() {
        var host = StandardEditors.CreateDefault(Project(), _ => new World("Scene"), _ => new World("Prefab"));

        foreach (var name in new[] {
                     "Texture", "Model", "Material", "Scene", "Prefab", "Shader", "UI",
                     "Addressable Group", "Graphics Compositor",

                     // Doc 20's E5: the four rows of doc 11's thirteen this assembly did not cover,
                     // plus the two authoring surfaces that had no row because they had no format.
                     "VFX Graph", "Animation Clip", "Animation Graph", "Sequence", "Audio Mixer",
                     "Input Actions", "Font",

                     // And doc 20's B5 shader-graph row, which had a node library and a compiler for
                     // a long time and no way into either.
                     "Shader Graph",

                     // Doc 34: a movement vocabulary is a table, a body's proxy shapes are what make one
                     // authored contact fit any body, and the harness is what says when a clip is done.
                     "Move Set", "Proxy Shapes", "Variation Harness", "Shape Vocabulary",

                     // Doc 37's P2, P5, P6 and P8: the mandatory editor; the second planner as a table
                     // and a curve, because a utility set has no edges; the third as tables beside a
                     // graph nobody authors, because a GOAP graph's edges are derived; and an
                     // environment query as two ordered lists, because that is what Unreal's EQS graph
                     // canvas actually holds.
                     "Behaviour Tree", "Utility Set", "GOAP Domain", "Environment Query",

                     // Doc 39's last row: the `.vxcompositor` a project actually ships, which the
                     // compositor graph editor does not claim — that one opens a `.vxcomp`, a node
                     // graph that compiles *to* a frame — so double-clicking the frame did nothing.
                     "Frame"
                 }) {
            Assert.True(host.TryGetByName(name, out _), $"'{name}' is not registered.");
        }

        Assert.Equal(26, host.Count);
    }

    /// <summary>
    ///     ⚠ A kind that claimed a file its importer does not import would open settings nothing
    ///     reads, so the two lists are compared rather than trusted.
    /// </summary>
    [Fact]
    public void KindExtensionsMatchTheirImporters() {
        var importers = BuiltInImporters.Create();
        var host = StandardEditors.CreateWorldless(Project());

        foreach (var name in new[] { "Texture", "Model" }) {
            Assert.True(host.TryGetByName(name, out var kind));

            foreach (var extension in kind!.Extensions) {
                Assert.True(
                    importers.TryGetForFile("x" + extension, out var importer)
                    && importer!.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase),
                    $"Nothing imports '{extension}', which the {name} kind claims."
                );
            }
        }
    }

    /// <summary>Opening an asset produces a document registered with the project.</summary>
    [Fact]
    public void OpeningProducesADocument() {
        using var fixture = new EditorFixture();
        fixture.Write("Assets/hero.vxmat", "shader: ForwardPlus\n");

        fixture.Project.Assets.Scan();
        Assert.True(fixture.Project.Assets.TryGetByPath("Assets/hero.vxmat", out var entry));

        var host = StandardEditors.CreateWorldless(fixture.Project);

        Assert.True(host.TryOpen(entry.Guid, out var document));
        Assert.IsType<MaterialDocument>(document);
        Assert.Contains(document!, fixture.Project.Documents);
    }

    /// <summary>⚠ Opening it again is the same document, not a second undo history over one file.</summary>
    [Fact]
    public void OpeningTwiceIsOneDocument() {
        using var fixture = new EditorFixture();
        fixture.Write("Assets/hero.rvn", "package A\n");

        fixture.Project.Assets.Scan();
        Assert.True(fixture.Project.Assets.TryGetByPath("Assets/hero.rvn", out var entry));

        var host = StandardEditors.CreateWorldless(fixture.Project);

        Assert.True(host.TryOpen(entry.Guid, out var first));
        Assert.True(host.TryOpen(entry.Guid, out var second));

        Assert.Same(first, second);
        Assert.Single(fixture.Project.Documents);
    }

    /// <summary>A folder is not something to open.</summary>
    [Fact]
    public void AFolderDoesNotOpen() {
        using var fixture = new EditorFixture();
        Directory.CreateDirectory(fixture.Paths.Absolute("Assets/Textures"));

        fixture.Project.Assets.Scan();
        Assert.True(fixture.Project.Assets.TryGetByPath("Assets/Textures", out var entry));

        Assert.False(StandardEditors.CreateWorldless(fixture.Project).TryOpen(entry.Guid, out _));
    }

    /// <summary>An asset nothing knows about does not open.</summary>
    [Fact]
    public void AnUnknownAssetDoesNotOpen() {
        using var fixture = new EditorFixture();

        Assert.False(StandardEditors.CreateWorldless(fixture.Project).TryOpen(AssetId.New(), out _));
    }

    /// <summary>
    ///     ⚠ A kind that opens a type it did not declare is refused and its document closed (#1294):
    ///     the view is found by that type, so the open would otherwise succeed into a tab nothing builds.
    /// </summary>
    [Fact]
    public void AKindThatOpensAnUndeclaredTypeIsRefused() {
        using var fixture = new EditorFixture();
        fixture.Write("Assets/hero.liar", "anything\n");
        fixture.Project.Assets.Scan();
        Assert.True(fixture.Project.Assets.TryGetByPath("Assets/hero.liar", out var entry));

        var host = new DocumentHost(fixture.Project);
        host.Add(new LyingKind());

        var failure = Assert.Throws<InvalidOperationException>(() => host.TryOpen(entry.Guid, out _));

        Assert.Contains("ShaderDocument", failure.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Project.Documents);
    }

    /// <summary>⚠ #739: disposing what <c>Add</c> returned frees the name <b>and</b> the extension.</summary>
    /// <remarks>
    ///     Both halves in one test, because either alone leaves the second registration throwing —
    ///     "the name is free but something still claims <c>.vxmat</c>" is the state a reload lands in
    ///     when the removal forgets one of them.
    /// </remarks>
    [Fact]
    public void DisposingARegistrationGivesBackTheNameAndTheExtension() {
        var host = new DocumentHost(Project());
        var registration = host.Add(new MaterialEditorFactory());

        Assert.Equal(1, host.Count);

        registration.Dispose();

        Assert.Equal(0, host.Count);
        Assert.False(host.TryGetForFile("Assets/hero.vxmat", out _));
        Assert.False(host.TryGetByName("Material", out _));

        // The claim a plugin's reload rests on: what was given up can be taken again.
        host.Add(new MaterialEditorFactory());

        Assert.True(host.TryGetForFile("Assets/hero.vxmat", out _));
    }

    /// <summary>Disposing twice is a no-op, not a second removal.</summary>
    /// <remarks>
    ///     ⚠ A plugin that releases its own registration <i>and</i> hands it to
    ///     <c>PluginContext.Owns</c> is doing the right thing twice, and the second dispose lands
    ///     after a reload has re-registered the same name.
    /// </remarks>
    [Fact]
    public void DisposingTwiceDoesNotTakeOutAReplacement() {
        var host = new DocumentHost(Project());
        var registration = host.Add(new MaterialEditorFactory());

        registration.Dispose();
        host.Add(new MaterialEditorFactory());
        registration.Dispose();

        Assert.True(host.TryGetForFile("Assets/hero.vxmat", out _));
    }

    /// <summary>⚠ A refused registration leaves nothing of itself behind, name included.</summary>
    [Fact]
    public void ARefusedRegistrationLeavesNoName() {
        var host = new DocumentHost(Project());

        host.Add(new MaterialEditorFactory());

        Assert.Throws<InvalidOperationException>(() => host.Add(new FakeKind("Other", ".vxmat")));
        Assert.False(host.TryGetByName("Other", out _));
        Assert.Equal(1, host.Count);
    }

    /// <summary>
    ///     ⚠ <b>A plugin can register a kind and its view through the plugin contract's own door</b>
    ///     (#489, through the #1294 split): <see cref="IDocumentHost" /> and <see cref="IWorkspace" />
    ///     published in <c>PluginServices</c>, each <c>Add</c> handing back the removal
    ///     <c>PluginContext.Owns</c> wants.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Through <c>PluginHost.Activate</c></b>: a test that called <c>Add</c> on a host it
    ///     built would pass with the services unpublished and the plugin route shut.
    /// </remarks>
    [Fact]
    public void APluginRegistersAKindAndAViewThroughTheContractAlone() {
        using var shell = new EditorShell(1280f, 800f);

        var host = StandardEditors.CreateWorldless(Project());
        var workspace = StandardEditors.CreateWorkspace();
        var plugins = new PluginHost(shell, new PluginServices().Add<IDocumentHost>(host).Add<IWorkspace>(workspace));
        var views = workspace.Views.Count;

        Assert.False(host.TryGetForFile("Assets/thing.tenth", out _));

        var loaded = plugins.Activate("mine.tenth", "Tenth", new EditorAddingModule());

        Assert.Equal(PluginState.Active, loaded.State);
        Assert.True(host.TryGetForFile("Assets/thing.tenth", out var kind));
        Assert.Equal("Tenth", kind!.Name);
        Assert.Equal(views + 1, workspace.Views.Count);

        // And it comes back out, which is what makes a plugin unloadable rather than a one-way
        // addition to the process.
        Assert.True(plugins.Unload("mine.tenth"));
        Assert.False(host.TryGetForFile("Assets/thing.tenth", out _));
        Assert.Equal(views, workspace.Views.Count);

        // The built-ins are untouched by either half.
        Assert.True(host.TryGetForFile("Assets/hero.png", out _));
    }

    /// <summary>What a third-party asset editor looks like: two registrations in <c>Activate</c>.</summary>
    sealed class EditorAddingModule : IEditorPlugin {
        public void Activate(PluginContext context) {
            context.Owns(context.Services.Require<IDocumentHost>().Add(new FakeKind("Tenth", ".tenth")));
            context.Owns(context.Services.Require<IWorkspace>().Add(new TenthView()));
        }

        public void Deactivate() {
        }
    }

    sealed class TenthView : DocumentView<MaterialDocument> {
        protected override UiElement Build(MaterialDocument document, UiElement panel) => throw new NotSupportedException();
    }

    sealed class FakeKind(string name, string extension) : DocumentKind {
        public override string Name => name;

        public override IReadOnlyList<string> Extensions { get; } = [extension];

        public override IReadOnlyList<Type> DocumentTypes { get; } = [typeof(MaterialDocument)];

        public override EditorDocument Open(DocumentRequest request) => throw new NotSupportedException();
    }

    /// <summary>Declares a material and opens a shader.</summary>
    sealed class LyingKind : DocumentKind {
        public override string Name => "Liar";

        public override IReadOnlyList<string> Extensions { get; } = [".liar"];

        public override IReadOnlyList<Type> DocumentTypes { get; } = [typeof(MaterialDocument)];

        public override EditorDocument Open(DocumentRequest request) =>
            new ShaderDocument(request.Project, request.Asset, request.Path);
    }
}

/// <summary>
///     The product invariant <c>IAssetEditorFactory</c> held as a type, held as a test over the two
///     lists this assembly ships (#1294). The editor's own registries — modules included — are
///     <c>Vixen.Editor.App.Tests</c>' <c>DocumentPairingTests</c>.
/// </summary>
public class StandardDocumentPairingTests {
    /// <summary>Every kind's every type has a view, and every view has a kind that opens its type.</summary>
    [Fact]
    public void Every_shipped_kind_has_a_view_and_every_view_a_kind() {
        var kinds = StandardEditors.Kinds(_ => new World("Scene"), _ => new World("Prefab"));
        var workspace = StandardEditors.CreateWorkspace();

        Assert.Equal([], DocumentPairing.Unpaired(kinds, workspace.Views));
    }

    /// <summary>The instrument: a view dropped from the list is reported, by name.</summary>
    [Fact]
    public void A_kind_whose_view_is_missing_is_reported() {
        var kinds = StandardEditors.Kinds(_ => new World("Scene"), _ => new World("Prefab"));
        var views = StandardEditors.Views().Where(view => view is not MaterialEditorView).ToList();

        var problem = Assert.Single(DocumentPairing.Unpaired(kinds, views));

        Assert.Contains("MaterialDocument", problem, StringComparison.Ordinal);
    }
}

/// <summary>The atomic write every document in this assembly goes through.</summary>
public class AssetFileTests {
    /// <summary>A missing file reads as empty, so the first one can be written by opening it.</summary>
    [Fact]
    public void AMissingFileIsEmpty() {
        using var fixture = new EditorFixture();

        Assert.Equal(string.Empty, AssetFile.Read(fixture.Paths.Absolute("Assets/nothing.vxmat")));
    }

    /// <summary>⚠ LF on every platform, and a trailing newline.</summary>
    [Fact]
    public void WritingNormalisesLineEndings() {
        using var fixture = new EditorFixture();
        var path = fixture.Paths.Absolute("Assets/notes.txt");

        AssetFile.Write(path, "one\r\ntwo");

        Assert.Equal("one\ntwo\n", File.ReadAllText(path));
    }

    /// <summary>The temporary the write goes through does not survive it.</summary>
    [Fact]
    public void NoTemporaryIsLeftBehind() {
        using var fixture = new EditorFixture();
        var path = fixture.Paths.Absolute("Assets/deep/notes.txt");

        AssetFile.Write(path, "text");

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
