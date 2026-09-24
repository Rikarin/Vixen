// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Testing;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     docs/plan/25 § 2.1 — the graph and `Vixen.ApiCheck` read the same surface for different
///     reasons and have to agree about what is in it.
/// </summary>
/// <remarks>
///     Each fixture is a scratch git repository carrying this checkout's <c>.gitignore</c>, because
///     <see cref="BaselineAgreement.Compare" /> reads the tree git defines (#1424) and refuses a
///     directory git cannot answer for.
/// </remarks>
public class BaselineAgreementTests : IDisposable {
    readonly string root = Directory.CreateTempSubdirectory("vixen-baseline-").FullName;

    public BaselineAgreementTests() {
        File.Copy(Path.Combine(RepositoryFiles.Root, ".gitignore"), Path.Combine(root, ".gitignore"));
        Git(root, "init", "--quiet");
    }

    public void Dispose() {
        if (Directory.Exists(root)) {
            // git marks its object files read-only, which Directory.Delete refuses on Windows.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Runs git in a scratch repository and fails the test on a non-zero exit.</summary>
    static void Git(string directory, params string[] arguments) {
        var start = new ProcessStartInfo("git") {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var git = Process.Start(start)!;
        var error = git.StandardError.ReadToEndAsync();
        git.StandardOutput.ReadToEnd();
        git.WaitForExit();

        Assert.True(git.ExitCode == 0, $"git {string.Join(' ', arguments)} exited {git.ExitCode}: {error.Result}");
    }

    string WriteBaseline(string assembly, string contents) {
        var directory = Path.Combine(root, assembly.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "PublicAPI.Unshipped.txt"), contents);

        return directory;
    }

    static DocNode Node(string qualifiedName, string assembly) => new() {
        Id = "T:" + qualifiedName,
        Kind = DocKind.Class,
        Name = qualifiedName[(qualifiedName.LastIndexOf('.') + 1)..],
        QualifiedName = qualifiedName,
        Namespace = qualifiedName[..qualifiedName.LastIndexOf('.')],
        Assembly = assembly,
        Area = "Core",
        Slug = Slugs.ForType("T:" + qualifiedName),
        Signature = [new DocSpan("public sealed class", "text")],
        IsPackable = true
    };

    /// <summary>A type's own line is the one with an arrow and a type keyword after it.</summary>
    [Fact]
    public void OnlyTypeDeclarationLinesAreRead() {
        var directory = WriteBaseline("Vixen.Core",
            """
            Vixen.Core.DisposeBag -> sealed class
            Vixen.Core.DisposeBag : System.IDisposable
            Vixen.Core.DisposeBag.Add<T>(T disposable) -> T
            Vixen.Core.DisposeBag.Count.get -> int
            Vixen.Core.SurfaceKind -> enum : byte
            Vixen.Core.Pooling.PooledDictionary<TKey, TValue> -> sealed class
            const Vixen.Core.Limits.Max = 8 -> int
            static Vixen.Core.GameTime.Zero.get -> Vixen.Core.GameTime
            """);

        Assert.Equal(
            [
                "Vixen.Core.DisposeBag",
                "Vixen.Core.Pooling.PooledDictionary<TKey,TValue>",
                "Vixen.Core.SurfaceKind"
            ],
            BaselineAgreement.ReadTypes(directory).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void RemovedEntriesAreNotSurface() {
        var directory = WriteBaseline("Vixen.Core",
            """
            Vixen.Core.Kept -> sealed class
            *REMOVED*Vixen.Core.Gone -> sealed class
            """);

        Assert.Equal(["Vixen.Core.Kept"], BaselineAgreement.ReadTypes(directory));
    }

    [Fact]
    public void AgreementIsSilent() {
        WriteBaseline("Vixen.Core", "Vixen.Core.World -> sealed class");

        Assert.Empty(BaselineAgreement.Compare(root, [Node("Vixen.Core.World", "Vixen.Core")]));
    }

    /// <summary>
    ///     ⚠ The dangerous direction. A baselined type the graph does not have is what a generator
    ///     that stopped running looks like — and the wrong design-time configuration did exactly
    ///     that to 298 types before anything noticed.
    /// </summary>
    [Fact]
    public void ABaselinedTypeMissingFromTheGraphIsReported() {
        WriteBaseline("Vixen.Core",
            """
            Vixen.Core.World -> sealed class
            Vixen.Core.Generated.Registry -> sealed class
            """);

        var disagreement = Assert.Single(
            BaselineAgreement.Compare(root, [Node("Vixen.Core.World", "Vixen.Core")]));

        Assert.Equal(["Vixen.Core.Generated.Registry"], disagreement.MissingFromGraph);
        Assert.Empty(disagreement.MissingFromBaseline);
    }

    [Fact]
    public void AnUnapprovedTypeInTheGraphIsReportedToo() {
        WriteBaseline("Vixen.Core", "Vixen.Core.World -> sealed class");

        var disagreement = Assert.Single(BaselineAgreement.Compare(root, [
            Node("Vixen.Core.World", "Vixen.Core"),
            Node("Vixen.Core.Newcomer", "Vixen.Core")
        ]));

        Assert.Equal(["Vixen.Core.Newcomer"], disagreement.MissingFromBaseline);
    }

    /// <summary>
    ///     ⚠ The baseline writes `Pool&lt;TKey, TValue&gt;` and Roslyn writes the same type the same
    ///     way — but a stray space either side would make every generic in the engine look missing.
    /// </summary>
    [Fact]
    public void GenericsAgreeWhateverTheSpacing() {
        WriteBaseline("Vixen.Core", "Vixen.Core.Pooling.PooledDictionary<TKey, TValue> -> sealed class");

        Assert.Empty(BaselineAgreement.Compare(
            root,
            [Node("Vixen.Core.Pooling.PooledDictionary<TKey,TValue>", "Vixen.Core")]));
    }

    /// <summary>
    ///     ⚠ The baseline writes variance and the symbol does not. Left alone, every covariant
    ///     interface in the engine reads as missing.
    /// </summary>
    [Fact]
    public void VarianceIsNotADifferentType() {
        WriteBaseline("Vixen.Ui.Reactive", "Vixen.Ui.Reactive.IReadOnlySignal<out T> -> interface");

        Assert.Empty(BaselineAgreement.Compare(
            root,
            [Node("Vixen.Ui.Reactive.IReadOnlySignal<T>", "Vixen.Ui.Reactive")]));
    }

    /// <summary>An assembly with an empty baseline has approved nothing yet, and says nothing.</summary>
    [Fact]
    public void AnEmptyBaselineIsNotADisagreement() {
        WriteBaseline("Vixen.Core", string.Empty);

        Assert.Empty(BaselineAgreement.Compare(root, [Node("Vixen.Core.World", "Vixen.Core")]));
    }

    /// <summary>
    ///     ⚠ The failure the listing exists for: a checkout keeps agent worktrees under
    ///     <c>.claude/worktrees/</c>, so a recursive walk found nine copies of every baseline and
    ///     eight were another branch's. Every copy planted here disagrees with the graph, so reading
    ///     any one of them is a reported disagreement.
    /// </summary>
    /// <remarks>
    ///     The <c>references/</c> and <c>.nuke/temp/</c> rows are the two the walk's own name list
    ///     did not decide the way git does: it skipped every dot directory, so <c>.nuke</c> by luck,
    ///     and read <c>references/</c>, where a cloned engine's baseline is somebody else's surface.
    ///     The baseline created and not yet added is the half that keeps this from passing by
    ///     reading nothing: it has to be found.
    /// </remarks>
    [Fact]
    public void OnlyTheCheckoutsOwnBaselinesAreRead() {
        WriteBaseline("Core/Vixen.Assets", "Vixen.Assets.Kept -> sealed class");
        WriteBaseline("Core/Vixen.Fresh", "Vixen.Fresh.Unapproved -> sealed class");

        const string stale = "Vixen.Assets.Stale -> sealed class";
        WriteBaseline(".claude/worktrees/other/Core/Vixen.Assets", stale);
        Git(Path.Combine(root, ".claude", "worktrees", "other"), "init", "--quiet");
        WriteBaseline("Core/Vixen.Assets/bin/Release/net10.0", stale);
        WriteBaseline("Core/Vixen.Assets/obj", stale);
        WriteBaseline("artifacts/staging/Vixen.Assets", stale);
        WriteBaseline("references/godot/Vixen.Assets", stale);
        WriteBaseline(".nuke/temp/vixen-sdk-tools/Vixen.Assets", stale);

        Git(root, "add", ".gitignore", "Core/Vixen.Assets/PublicAPI.Unshipped.txt");

        var disagreement = Assert.Single(BaselineAgreement.Compare(root, [Node("Vixen.Assets.Kept", "Vixen.Assets")]));

        Assert.Equal("Vixen.Fresh", disagreement.Assembly);
        Assert.Equal(["Vixen.Fresh.Unapproved"], disagreement.MissingFromGraph);
    }
}
