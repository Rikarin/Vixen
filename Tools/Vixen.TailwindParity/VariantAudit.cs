// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.TailwindParity;

/// <summary>Checks v4's variant vocabulary against what <c>Variants.TryResolve</c> answers.</summary>
/// <remarks>
///     <para>
///         <b>The other half of the vocabulary, and the half doc 43's ledger has no row for.</b> The
///         <c>.tsv</c> is one row per utility <i>root</i>; a variant emits no property, so the
///         consumption gate never sees one and neither does <c>ParityLedger</c>. What existed instead
///         was prose — A12's row saying seven pseudo-element variants "wait behind the generated
///         box", re-audited six times and never measured.
///     </para>
///     <para>
///         ⚠ <b>The answer comes from the real resolver and not from a list of names.</b>
///         <c>Variants.TryResolve</c> is what the generator calls, so this asks the same question the
///         generator asks; re-implementing the prefix rules here would be the parity test that checks
///         a C# re-implementation rather than the thing, which doc 43 Part 5 records shipping once.
///     </para>
///     <para>
///         ⚠ <b><c>docs/plan/43-web-styling-variants-unsupported.txt</c> can only shrink.</b> A v4
///         variant Vixen refuses and the file does not name fails; a name in the file that Vixen has
///         since learned fails too. So the day the generated box lands and <c>before:</c> starts
///         resolving, the gate says which lines to delete — which is the <c>expires-on</c> mechanism
///         in the one form that cannot be walked past, because deleting the line is what makes the
///         build green again.
///     </para>
/// </remarks>
static class VariantAudit {
    /// <summary>Compares the registry's variants with what the engine resolves.</summary>
    /// <param name="registry">The committed snapshot.</param>
    /// <param name="resolves">Whether the engine resolves a class prefix.</param>
    /// <param name="unsupported">The variants known not to resolve.</param>
    /// <returns>Every disagreement, in a stable order.</returns>
    public static IReadOnlyList<ParityFinding> Run(
        TailwindRegistry registry,
        Func<string, bool> resolves,
        IReadOnlyCollection<string> unsupported
    ) {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resolves);
        ArgumentNullException.ThrowIfNull(unsupported);

        var findings = new List<ParityFinding>();
        var declared = new SortedSet<string>(unsupported, StringComparer.Ordinal);
        var known = new HashSet<string>(StringComparer.Ordinal);

        foreach (var variant in registry.Variants) {
            known.Add(variant.Name);

            if (variant.Probe is null) {
                findings.Add(
                    new ParityFinding(
                        "TWP010",
                        variant.Name,
                        $"{registry.Package}@{registry.Version} registers this variant and compiles no class"
                        + " the snapshot could build from it, so nothing here can say whether Vixen has it"
                    )
                );

                continue;
            }

            var supported = resolves(variant.Probe);

            if (supported && declared.Contains(variant.Name)) {
                findings.Add(
                    new ParityFinding(
                        "TWP009",
                        variant.Name,
                        $"`{variant.Probe}:` resolves now, so this line must come out of"
                        + " docs/plan/43-web-styling-variants-unsupported.txt — that list can only shrink"
                    )
                );

                continue;
            }

            if (!supported && !declared.Contains(variant.Name)) {
                findings.Add(
                    new ParityFinding(
                        "TWP008",
                        variant.Name,
                        $"{registry.Package}@{registry.Version} has this variant, `{variant.Probe}:` does not"
                        + " resolve, and docs/plan/43-web-styling-variants-unsupported.txt does not name it"
                    )
                );
            }
        }

        foreach (var name in declared) {
            if (!known.Contains(name)) {
                findings.Add(
                    new ParityFinding(
                        "TWP011",
                        name,
                        $"{registry.Package}@{registry.Version} has no such variant, so this line of"
                        + " docs/plan/43-web-styling-variants-unsupported.txt excuses nothing"
                    )
                );
            }
        }

        return findings;
    }
}
