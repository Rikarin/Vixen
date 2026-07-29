// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Code;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>What editing a text asset does to the document's history and to the file.</summary>
public class CodeDocumentTests {
    /// <summary>The file's text is what the buffer opens with.</summary>
    [Fact]
    public void TheFileIsTheBuffer() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/hero.rvn", "shader Hero {\n}\n");

        var document = new ShaderDocument(fixture.Project, AssetId.New(), path);

        Assert.Equal(3, document.Buffer.LineCount);
        Assert.False(document.IsDirty.Value);
    }

    /// <summary>Typing makes the document dirty and produces an undo entry.</summary>
    [Fact]
    public void TypingIsAnUndoEntry() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/notes.rvn", "a");

        var document = new ShaderDocument(fixture.Project, AssetId.New(), path);
        document.Buffer.Insert(new(0, 1), "bc");

        Assert.True(document.IsDirty.Value);
        Assert.True(document.Stack.CanUndo.Value);

        document.Stack.Undo();
        Assert.Equal("a", document.Buffer.Text);
    }

    /// <summary>⚠ Typing within a line is one entry, however many keystrokes it took.</summary>
    [Fact]
    public void TypingWithinALineMerges() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/notes.rvn", string.Empty);

        var document = new ShaderDocument(fixture.Project, AssetId.New(), path);
        var caret = new TextPosition(0, 0);

        foreach (var character in "hello") {
            caret = document.Buffer.Insert(caret, character.ToString());
        }

        document.Stack.Undo();
        Assert.Equal(string.Empty, document.Buffer.Text);
    }

    /// <summary>⚠ And a newline ends the run, so a paragraph undoes a line at a time.</summary>
    [Fact]
    public void ANewlineEndsTheRun() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/notes.rvn", string.Empty);

        var document = new ShaderDocument(fixture.Project, AssetId.New(), path);
        var caret = document.Buffer.Insert(new(0, 0), "one");

        caret = document.Buffer.Insert(caret, "\n");
        document.Buffer.Insert(caret, "two");

        document.Stack.Undo();
        Assert.Equal("one\n", document.Buffer.Text);

        document.Stack.Undo();
        Assert.Equal("one", document.Buffer.Text);
    }

    /// <summary>⚠ An undo does not push an entry of its own, so the history does not grow.</summary>
    [Fact]
    public void UndoDoesNotRecordItself() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/notes.rvn", "a");

        var document = new ShaderDocument(fixture.Project, AssetId.New(), path);
        document.Buffer.Insert(new(0, 1), "b");

        document.Stack.Undo();
        Assert.False(document.Stack.CanUndo.Value);

        document.Stack.Redo();
        Assert.Equal("ab", document.Buffer.Text);
    }

    /// <summary>Saving writes the buffer and leaves the document clean.</summary>
    [Fact]
    public void SavingWritesTheBuffer() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/notes.rvn", "a");

        var document = new ShaderDocument(fixture.Project, AssetId.New(), path);
        document.Buffer.Insert(new(0, 1), "b");
        document.Save();

        Assert.Equal("ab\n", EditorFixture.Read(path));
        Assert.False(document.IsDirty.Value);
    }
}

/// <summary>What the front ends say about a file, translated into what the gutter draws.</summary>
public class CodeAnalysisTests {
    /// <summary>A shader that parses cleanly has nothing to complain about.</summary>
    [Fact]
    public void AValidShaderIsQuiet() {
        using var fixture = new EditorFixture();
        var path = fixture.Write(
            "Assets/hero.rvn",
            "package A\n\nshader Hero {\n    var count: int\n}\n"
        );

        var document = new ShaderDocument(fixture.Project, AssetId.New(), path);

        document.Reanalyse();
        Assert.False(document.HasErrors);
    }

    /// <summary>A shader that does not parse is an error somewhere in the file.</summary>
    [Fact]
    public void ABrokenShaderIsAnError() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/broken.rvn", "fn (((");

        var document = new ShaderDocument(fixture.Project, AssetId.New(), path);
        document.Reanalyse();

        Assert.True(document.HasErrors);
        Assert.All(document.Diagnostics, diagnostic => Assert.True(diagnostic.Line >= 0));
    }

    /// <summary>The tokenizer is Raven's, which is the control set's own list rather than a second one.</summary>
    [Fact]
    public void TheTokenizerIsRavens() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/hero.rvn", string.Empty);

        var document = new ShaderDocument(fixture.Project, AssetId.New(), path);

        Assert.Same(CStyleTokenizer.Raven, document.Tokenizer);
    }

    /// <summary>A component binds, and the preview has a tree to build from.</summary>
    [Fact]
    public void AComponentBinds() {
        using var fixture = new EditorFixture();

        var path = fixture.Write(
            "Assets/Counter.vxml",
            "@component Counter\n\n<panel class=\"card\">\n  <text>Hello</text>\n</panel>\n"
        );

        var document = new MarkupDocument(fixture.Project, AssetId.New(), path);
        document.Reanalyse();

        Assert.NotNull(document.Component);
        Assert.Equal("Counter", document.Component!.Name);
    }

    /// <summary>A file that declares no component is a complaint rather than a crash.</summary>
    [Fact]
    public void AFileWithNoComponentComplains() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/Nothing.vxml", "<panel />\n");

        var document = new MarkupDocument(fixture.Project, AssetId.New(), path);
        document.Reanalyse();

        Assert.NotEmpty(document.Diagnostics);
        Assert.Null(document.Component);
    }

    /// <summary>A stylesheet has no analysis, and that is a stated gap rather than an accident.</summary>
    [Fact]
    public void AStylesheetHasNoDiagnostics() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/theme.vcss", "button { color: red; }\n");

        var document = new StyleSheetDocument(fixture.Project, AssetId.New(), path);

        Assert.Empty(document.Reanalyse());
    }
}
