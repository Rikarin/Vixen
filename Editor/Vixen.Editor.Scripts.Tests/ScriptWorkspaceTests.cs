// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Editor.Scripts.Tests;

/// <summary>Doc 36 § P5's incremental compilation, asserted as work rather than as milliseconds.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The document's bound is a wall-clock one — "tens of milliseconds for a dozen files,
///         and nothing here measures a project with hundreds" — and a wall-clock assertion is the
///         wrong instrument for it.</b> A budget calibrated on an idle machine is this repository's
///         largest flake source, and on a loaded one it is wrong by a factor in the direction that
///         hides the regression. What is asserted here is the same property said as a count of files
///         parsed, which is deterministic, and which is false before the work and false again under
///         sabotage.
///     </para>
///     <para>
///         ⚠ <b>And correctness is asserted beside it every time.</b> A cache that reused a stale tree
///         would parse nothing at all and pass every count in this file; so each case that asserts a
///         number also asserts that the compiler saw the change — a new error appearing, an old one
///         going, a deleted type staying deleted.
///     </para>
/// </remarks>
public class ScriptWorkspaceTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-workspace-" + Guid.NewGuid().ToString("N"));

    public ScriptWorkspaceTests() => Directory.CreateDirectory(Path.Combine(root, "Assets", "Editor"));

    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    string Output => Path.Combine(root, "Library", "EditorScripts");

    void Write(string name, string source) =>
        File.WriteAllText(Path.Combine(root, "Assets", "Editor", name), source);

    static string Type(string name, string body = "") => $"public static class {name} {{ {body} }}";

    /// <summary>The first build parses everything; a second with nothing touched parses none of it.</summary>
    [Fact]
    public void A_rebuild_with_nothing_changed_parses_nothing() {
        var workspace = new ScriptWorkspace();

        Write("A.cs", Type("A"));
        Write("B.cs", Type("B"));
        Write("C.cs", Type("C"));

        var first = workspace.Compile(root, Output);

        Assert.Equal(3, first.Sources);
        Assert.Equal(3, first.Parsed);
        Assert.NotNull(first.AssemblyPath);

        var second = workspace.Compile(root, Output);

        Assert.Equal(3, second.Sources);
        Assert.Equal(0, second.Parsed);
        Assert.NotNull(second.AssemblyPath);
        Assert.Empty(second.Errors);
    }

    /// <summary>One edited file is one parse, and the compiler sees the edit.</summary>
    /// <remarks>
    ///     ⚠ <b>The error is the half that makes the count worth reading.</b> A workspace that never
    ///     reparsed anything would report one too — it would report zero — and would build the code
    ///     that is no longer on disk.
    /// </remarks>
    [Fact]
    public void One_edited_file_is_one_parse_and_the_edit_reaches_the_compiler() {
        var workspace = new ScriptWorkspace();

        Write("A.cs", Type("A"));
        Write("B.cs", Type("B"));
        Write("C.cs", Type("C"));

        Assert.Equal(3, workspace.Compile(root, Output).Parsed);

        Write("B.cs", Type("B", "static void Broken() { return notAThing; }"));

        var edited = workspace.Compile(root, Output);

        Assert.Equal(1, edited.Parsed);
        Assert.Null(edited.AssemblyPath);
        Assert.Contains(edited.Errors, diagnostic => diagnostic.Id == "CS0103");
        Assert.All(edited.Errors, diagnostic => Assert.EndsWith("B.cs", diagnostic.File, StringComparison.Ordinal));

        // And back: fixing it is one parse again, and the error goes.
        Write("B.cs", Type("B"));

        var fixedAgain = workspace.Compile(root, Output);

        Assert.Equal(1, fixedAgain.Parsed);
        Assert.NotNull(fixedAgain.AssemblyPath);
        Assert.Empty(fixedAgain.Errors);
    }

    /// <summary>
    ///     ⚠ A file somebody deleted leaves the workspace, or the compilation still contains its types
    ///     and the editor goes on loading the tool that was removed.
    /// </summary>
    [Fact]
    public void A_deleted_file_leaves_the_workspace_and_takes_its_types_with_it() {
        var workspace = new ScriptWorkspace();

        Write("A.cs", Type("A"));
        Write("Gone.cs", Type("Gone"));

        Assert.Equal(2, workspace.Compile(root, Output).Parsed);
        Assert.Equal(2, workspace.Cached);

        // The instrument: while both exist, naming the second one compiles.
        Write("A.cs", Type("A", "static void Uses() { _ = typeof(Gone); }"));

        var together = workspace.Compile(root, Output);

        Assert.Equal(1, together.Parsed);
        Assert.NotNull(together.AssemblyPath);

        File.Delete(Path.Combine(root, "Assets", "Editor", "Gone.cs"));

        var after = workspace.Compile(root, Output);

        Assert.Equal(1, after.Sources);
        Assert.Equal(1, workspace.Cached);

        // Nothing was parsed — A.cs is unchanged — and the build still fails, which is the whole
        // point: the removal is a fact about the compilation and not about the parse.
        Assert.Equal(0, after.Parsed);
        Assert.Null(after.AssemblyPath);
        Assert.Contains(after.Errors, diagnostic => diagnostic.Id == "CS0246");
    }

    /// <summary>
    ///     ⚠ A file rewritten to the same length inside one filesystem tick is still reparsed, because
    ///     staleness is the text and not a stamp.
    /// </summary>
    /// <remarks>
    ///     The classic file-cache bug: a last-write time plus a length cannot tell two same-length
    ///     saves apart, and the symptom is an editor running the code somebody deleted. Written back
    ///     to back with no delay, which is the arrangement a timestamp cache fails on.
    /// </remarks>
    [Fact]
    public void A_same_length_rewrite_is_not_mistaken_for_no_change() {
        var workspace = new ScriptWorkspace();
        var file = Path.Combine(root, "Assets", "Editor", "Same.cs");

        File.WriteAllText(file, Type("Same", "public const int Value = 1;"));

        var stamped = File.GetLastWriteTimeUtc(file);
        var length = new FileInfo(file).Length;

        Assert.Equal(1, workspace.Compile(root, Output).Parsed);

        // Same number of characters, different program — and put back under the same timestamp, so a
        // stamp-based cache has nothing at all to notice.
        File.WriteAllText(file, Type("Same", "public const int Value = 2;"));
        File.SetLastWriteTimeUtc(file, stamped);

        Assert.Equal(length, new FileInfo(file).Length);

        var rewritten = workspace.Compile(root, Output);

        Assert.Equal(1, rewritten.Parsed);
        Assert.NotNull(rewritten.AssemblyPath);
    }

    /// <summary>A new file is one parse and the others are not touched.</summary>
    [Fact]
    public void A_new_file_is_one_parse() {
        var workspace = new ScriptWorkspace();

        Write("A.cs", Type("A"));
        Write("B.cs", Type("B"));

        Assert.Equal(2, workspace.Compile(root, Output).Parsed);

        Write("C.cs", Type("C"));

        var grown = workspace.Compile(root, Output);

        Assert.Equal(3, grown.Sources);
        Assert.Equal(1, grown.Parsed);
        Assert.Equal(3, workspace.Cached);
    }

    /// <summary>
    ///     ⚠ The instrument: the one-shot entry point keeps nothing, so it parses everything every
    ///     time. Without this the counts above could be read as a property of the compiler rather than
    ///     of the workspace.
    /// </summary>
    [Fact]
    public void The_one_shot_compile_reuses_nothing() {
        Write("A.cs", Type("A"));
        Write("B.cs", Type("B"));

        Assert.Equal(2, ScriptCompiler.Compile(root, Output).Parsed);
        Assert.Equal(2, ScriptCompiler.Compile(root, Output).Parsed);
    }

    /// <summary>And deleting the last script empties the workspace rather than leaving it holding one.</summary>
    [Fact]
    public void Deleting_the_last_script_empties_the_workspace() {
        var workspace = new ScriptWorkspace();

        Write("Only.cs", Type("Only"));

        Assert.Equal(1, workspace.Compile(root, Output).Parsed);
        Assert.Equal(1, workspace.Cached);

        File.Delete(Path.Combine(root, "Assets", "Editor", "Only.cs"));

        Assert.Equal(0, workspace.Compile(root, Output).Sources);
        Assert.Equal(0, workspace.Cached);
    }
}
