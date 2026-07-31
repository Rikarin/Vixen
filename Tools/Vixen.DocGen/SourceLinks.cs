// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;

namespace Vixen.DocGen;

/// <summary>Turns a declaration's location into a path and a GitHub URL — docs/plan/25 § 2.7.</summary>
/// <param name="repositoryRoot">Absolute path of the checkout the symbols were read from.</param>
/// <param name="repositoryUrl">
///     The project URL, without a trailing slash — <c>https://github.com/rikarin/Vixen</c>.
/// </param>
/// <param name="commit">
///     The commit being documented: a release tag's sha for a released version, the branch head for
///     <c>next</c>. Null produces paths without URLs, which is what a local run wants.
/// </param>
sealed class SourceLinks(string repositoryRoot, string repositoryUrl, string? commit) {
    readonly string _root = repositoryRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// <summary>The link for a symbol's first declaration in source, or null if it has none.</summary>
    /// <remarks>
    ///     ⚠ A partial type has several, and this takes the one carrying the doc comment because
    ///     that is the half a reader is looking for. The others are listed on the page as "also
    ///     declared in" from <see cref="All" />.
    /// </remarks>
    public DocSource? For(ISymbol symbol) {
        var all = All(symbol);

        return all.Count == 0 ? null : all[0];
    }

    /// <summary>Every source declaration of the symbol, doc-commented parts first.</summary>
    public IReadOnlyList<DocSource> All(ISymbol symbol) {
        var documented = !string.IsNullOrWhiteSpace(symbol.GetDocumentationCommentXml());

        return [.. symbol.Locations
            .Where(location => location.IsInSource)
            .Select(location => location.GetLineSpan())
            .Where(span => !string.IsNullOrEmpty(span.Path))
            .OrderByDescending(_ => documented)
            .Select(span => Create(span.Path, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1))];
    }

    /// <summary>True when the file is generator output rather than a file in the tree.</summary>
    /// <remarks>
    ///     Generated code has no file a reader can open, so § 2.7 sends the link to the generator
    ///     instead. Roslyn writes generated documents under <c>obj/</c> when
    ///     <c>EmitCompilerGeneratedFiles</c> is on and gives them a synthetic path when it is not;
    ///     both are outside the source tree, which is the test.
    /// </remarks>
    public bool IsGenerated(string path) {
        if (!path.StartsWith(_root, StringComparison.Ordinal)) {
            return true;
        }

        var relative = Relative(path);

        return relative.StartsWith("obj/", StringComparison.Ordinal)
            || relative.Contains("/obj/", StringComparison.Ordinal);
    }

    DocSource Create(string absolutePath, int startLine, int endLine) {
        var relative = Relative(absolutePath);
        var url = commit is null || IsGenerated(absolutePath)
            ? null
            : $"{repositoryUrl.TrimEnd('/')}/blob/{commit}/{relative}#L{startLine}-L{endLine}";

        return new DocSource(relative, startLine, endLine, url);
    }

    /// <summary>
    ///     Repository-relative, with forward slashes whatever the OS uses, because the path ends up
    ///     in a URL and in a JSON file that three platforms have to produce identically.
    /// </summary>
    string Relative(string absolutePath) {
        var path = absolutePath.StartsWith(_root, StringComparison.Ordinal)
            ? absolutePath[_root.Length..]
            : absolutePath;

        return path.Replace('\\', '/');
    }
}
