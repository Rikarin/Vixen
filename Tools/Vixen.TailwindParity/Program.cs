// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.TailwindParity;
using Vixen.Ui.Styling.Utilities;

// The whole of the entry point. Everything worth testing is in `ParityAudit`, which
// `Vixen.TailwindParity.Tests` calls against the committed snapshot — so the check is a test and
// this is the way to read its output by hand.
var files = RepositoryFiles.Locate(args.Length > 0 ? args[0] : null);

if (!File.Exists(files.Registry)) {
    Console.Error.WriteLine(
        $"Vixen.TailwindParity : error VXTW001: {RepositoryFiles.RegistryName} is missing."
        + " Take it with Tools/Vixen.TailwindParity/snapshot.mjs, which needs tailwindcss installed."
    );

    return 2;
}

var registry = TailwindRegistry.Read(files.Registry);
var rows = ParityLedgerTable.Read(files.Ledger);
var unlisted = ParityAudit.ReadUnlisted(files.Unlisted);
var unsupported = ParityAudit.ReadUnlisted(files.VariantsUnsupported);
var tokens = ThemeTokens.CreateDefault();

var findings = ParityAudit.Run(registry, rows, unlisted)
    .Concat(VariantAudit.Run(registry, probe => Variants.TryResolve(probe, tokens, out _), unsupported))
    .Concat(ProseAudit.Run(File.ReadAllText(files.Document), registry, unlisted, unsupported))
    .ToList();

foreach (var finding in findings) {
    Console.Error.WriteLine($"Vixen.TailwindParity : error {finding.Code}: {finding.Subject}: {finding.Text}");
}

Console.WriteLine(
    $"Vixen.TailwindParity: {rows.Count} ledger rows against {registry.Package}@{registry.Version}"
    + $" ({registry.Taken}) — {registry.FunctionalRoots.Length} functional roots,"
    + $" {registry.StaticRoots.Length} static, {registry.Variants.Length} variants,"
    + $" {unlisted.Count} statics knowingly unlisted, {unsupported.Count} variants knowingly absent."
    + $" {findings.Count} findings."
);

return findings.Count == 0 ? 0 : 1;
