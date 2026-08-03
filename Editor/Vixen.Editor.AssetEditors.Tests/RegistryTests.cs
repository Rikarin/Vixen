// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Code;
using Vixen.Editor.AssetEditors.Importing;
using Vixen.Editor.AssetEditors.Materials;
using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>Which editor claims a file, and what happens when two of them do.</summary>
public class AssetEditorRegistryTests {
    /// <summary>An extension resolves to the editor that claimed it.</summary>
    [Fact]
    public void AnExtensionResolves() {
        var registry = StandardEditors.CreateWorldless();

        Assert.True(registry.TryGetForFile("Assets/hero.png", out var editor));
        Assert.Equal("Texture", editor!.Name);
    }

    /// <summary>⚠ There is no fallback: a file nothing claims does not open in a text editor by guess.</summary>
    [Fact]
    public void NothingClaimsAnUnknownExtension() =>
        Assert.False(StandardEditors.CreateWorldless().TryGetForFile("Assets/hero.blob", out _));

    /// <summary>A file with no extension claims nothing rather than throwing.</summary>
    [Fact]
    public void AFileWithNoExtensionClaimsNothing() =>
        Assert.False(StandardEditors.CreateWorldless().TryGetForFile("Assets/LICENSE", out _));

    /// <summary>⚠ Two editors claiming one extension is an error naming both, not last-one-wins.</summary>
    [Fact]
    public void TwoClaimantsAreRefused() {
        var registry = new AssetEditorRegistry().Add(new MaterialEditorFactory());

        var failure = Assert.Throws<InvalidOperationException>(() => registry.Add(new ClashingFactory()));

        Assert.Contains(".vxmat", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>And so is one name used twice.</summary>
    [Fact]
    public void TwoEditorsCannotShareAName() {
        var registry = new AssetEditorRegistry().Add(new MaterialEditorFactory());

        Assert.Throws<InvalidOperationException>(() => registry.Add(new SameNameFactory()));
    }

    /// <summary>The default set has one editor per row of doc 11's table that this assembly covers.</summary>
    [Fact]
    public void TheDefaultSetIsComplete() {
        var registry = StandardEditors.CreateDefault(_ => new World("Scene"), _ => new World("Prefab"));

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
                     "Move Set", "Proxy Shapes", "Variation Harness", "Shape Vocabulary"
                 }) {
            Assert.True(registry.TryGetByName(name, out _), $"'{name}' is not registered.");
        }

        Assert.Equal(21, registry.Count);
    }

    /// <summary>
    ///     ⚠ An editor that claimed a file its importer does not import would open settings nothing
    ///     reads, so the two lists are compared rather than trusted.
    /// </summary>
    [Fact]
    public void EditorExtensionsMatchTheirImporters() {
        var importers = BuiltInImporters.Create();
        var editors = StandardEditors.CreateWorldless();

        foreach (var name in new[] { "Texture", "Model" }) {
            Assert.True(editors.TryGetByName(name, out var editor));

            foreach (var extension in editor!.Extensions) {
                Assert.True(
                    importers.TryGetForFile("x" + extension, out var importer)
                    && importer!.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase),
                    $"Nothing imports '{extension}', which the {name} editor claims."
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

        var registry = StandardEditors.CreateWorldless();

        Assert.True(registry.TryOpen(fixture.Project, entry.Guid, out var document));
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

        var registry = StandardEditors.CreateWorldless();

        Assert.True(registry.TryOpen(fixture.Project, entry.Guid, out var first));
        Assert.True(registry.TryOpen(fixture.Project, entry.Guid, out var second));

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

        Assert.False(StandardEditors.CreateWorldless().TryOpen(fixture.Project, entry.Guid, out _));
    }

    /// <summary>An asset nothing knows about does not open.</summary>
    [Fact]
    public void AnUnknownAssetDoesNotOpen() {
        using var fixture = new EditorFixture();

        Assert.False(StandardEditors.CreateWorldless().TryOpen(fixture.Project, AssetId.New(), out _));
    }

    sealed class ClashingFactory : IAssetEditorFactory {
        public string Name => "Other";

        public IReadOnlyList<string> Extensions { get; } = [".vxmat"];

        public EditorDocument Open(AssetEditorRequest request) => throw new NotSupportedException();

        public UiElement CreateView(EditorDocument document, UiElement panel) => throw new NotSupportedException();
    }

    sealed class SameNameFactory : IAssetEditorFactory {
        public string Name => "Material";

        public IReadOnlyList<string> Extensions { get; } = [".other"];

        public EditorDocument Open(AssetEditorRequest request) => throw new NotSupportedException();

        public UiElement CreateView(EditorDocument document, UiElement panel) => throw new NotSupportedException();
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
