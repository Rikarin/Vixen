// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     docs/plan/25 § 2.1 — the graph and `Vixen.ApiCheck` read the same surface for different
///     reasons and have to agree about what is in it.
/// </summary>
public class BaselineAgreementTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-baseline-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    string WriteBaseline(string assembly, string contents) {
        var directory = Path.Combine(root, assembly);

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
}
