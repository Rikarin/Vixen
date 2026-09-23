// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.TailwindParity;

/// <summary>Where the files this reads live, found by walking up from the binary.</summary>
/// <remarks>
///     ⚠ Same walk as <c>ParityLedger.Locate</c> in <c>Vixen.Ui.Styling.Utilities.Tests</c>, and for
///     the same reason: the ledger is a document rather than content, so no build step copies it
///     beside a test assembly and no <c>AppContext</c> path points at it. The walk anchors on the
///     ledger itself, so a run from a worktree finds that worktree's ledger.
/// </remarks>
sealed record RepositoryFiles(
    string Ledger,
    string Registry,
    string Unlisted,
    string VariantsUnsupported,
    string Document
) {
    /// <summary>The ledger's path relative to the repository root.</summary>
    public const string LedgerName = "docs/plan/43-web-styling-parity.tsv";

    /// <summary>The committed registry snapshot's path relative to the repository root.</summary>
    public const string RegistryName = "docs/plan/tailwind-registry.json";

    /// <summary>The shrinking unlisted-statics list's path relative to the repository root.</summary>
    public const string UnlistedName = "docs/plan/43-web-styling-unlisted.txt";

    /// <summary>The shrinking unsupported-variants list's path relative to the repository root.</summary>
    public const string VariantsUnsupportedName = "docs/plan/43-web-styling-variants-unsupported.txt";

    /// <summary>Doc 43 itself, whose prose states counts the three artefacts above measure.</summary>
    public const string DocumentName = "docs/plan/43-web-styling-parity.md";

    /// <summary>Walks up from a directory until the ledger is beneath it.</summary>
    /// <param name="start">Where to start; the running binary by default.</param>
    /// <returns>The four artefacts and the document that describes them.</returns>
    public static RepositoryFiles Locate(string? start = null) {
        var directory = new DirectoryInfo(start ?? AppContext.BaseDirectory);

        while (directory is not null) {
            var ledger = Path.Combine(directory.FullName, LedgerName.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(ledger)) {
                return new RepositoryFiles(
                    ledger,
                    Path.Combine(directory.FullName, RegistryName.Replace('/', Path.DirectorySeparatorChar)),
                    Path.Combine(directory.FullName, UnlistedName.Replace('/', Path.DirectorySeparatorChar)),
                    Path.Combine(
                        directory.FullName,
                        VariantsUnsupportedName.Replace('/', Path.DirectorySeparatorChar)
                    ),
                    Path.Combine(directory.FullName, DocumentName.Replace('/', Path.DirectorySeparatorChar))
                );
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"{LedgerName} was not found above {start ?? AppContext.BaseDirectory}"
        );
    }
}
