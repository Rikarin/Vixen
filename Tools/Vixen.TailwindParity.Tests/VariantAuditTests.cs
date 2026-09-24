// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling.Utilities;
using Xunit;

namespace Vixen.TailwindParity.Tests;

/// <summary>That Vixen's variant vocabulary is measured against v4's rather than described.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 43's ledger has no row for a variant and cannot.</b> Every row is a utility root
///         and every measured column comes from a property something emits; a variant emits none, so
///         the consumption gate never sees one. What stood in for a measurement was prose — A12's
///         row saying seven pseudo-element variants "wait behind the generated box", carried through
///         six audits of <c>#233</c> and never once checked. Twenty-one of v4's eighty-eight do not
///         resolve, and now they are a file.
///     </para>
///     <para>
///         ⚠ <b>The answer comes from <c>Variants.TryResolve</c>, which is what the generator
///         calls.</b> A list of names kept here would be a second opinion about the engine rather
///         than a reading of it — doc 43 Part 5's "a parity test that checked a C# re-implementation
///         rather than the shader", which this repository shipped once.
///     </para>
/// </remarks>
public sealed class VariantAuditTests {
    static readonly RepositoryFiles Files = RepositoryFiles.Locate();

    static readonly TailwindRegistry Miniature = new() {
        Package = "tailwindcss",
        Version = "4.3.3",
        Taken = "2026-09-23",
        StaticRoots = ["sr-only"],
        FunctionalRoots = ["top"],
        Variants = [new TailwindVariant("hover", "hover"), new TailwindVariant("before", "before")],
    };

    static IReadOnlyList<string> Codes(Func<string, bool> resolves, params string[] unsupported) =>
        [.. VariantAudit.Run(Miniature, resolves, unsupported).Select(finding => finding.Code)];

    /// <summary>The check this file exists for, against the real engine.</summary>
    [Fact]
    public void Every_variant_tailwindcss_has_is_one_vixen_resolves_or_one_this_repository_names() {
        var registry = TailwindRegistry.Read(Files.Registry);
        var unsupported = ParityAudit.ReadUnlisted(Files.VariantsUnsupported);
        var tokens = ThemeTokens.CreateDefault();

        var findings = VariantAudit.Run(
            registry,
            probe => Variants.TryResolve(probe, tokens, out _),
            unsupported
        );

        Assert.Equal([], findings.Select(finding => finding.ToString()));
    }

    /// <summary>⚠ The four pseudo-element variants #233 still owes are in the file, by name.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted as membership of the <i>file</i> and not as a refusal by the engine</b>, and
    ///     the difference is the whole point. "<c>before:</c> does not resolve" is already covered by
    ///     the fact above, which would stay green if somebody quietly deleted the line — the file
    ///     would then be short and the engine would be reported as having a variant it does not. This
    ///     asks the opposite question: that the debt is written down where the day it is paid is
    ///     visible. When A12 lands, this fails and so does TWP009, and both say to delete the line.
    /// </remarks>
    [Fact]
    public void The_generated_box_variants_are_recorded_as_owed() {
        var unsupported = ParityAudit.ReadUnlisted(Files.VariantsUnsupported).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            ["after", "before", "file", "marker"],
            unsupported.Intersect(["after", "before", "file", "marker"], StringComparer.Ordinal).Order()
        );

        // ⚠ And `placeholder` is NOT among them, which is the correction five audits of #233 needed:
        // `TextField` builds the prompt as a real child with its own tag, so the variant landed as a
        // child combinator with no pseudo-element in it.
        Assert.DoesNotContain("placeholder", unsupported);

        // ⚠ Nor `backdrop`, for the same reason, although the variant table said it named "a control
        // that does not exist": `Dialog` and `Drawer` build the sheet behind them as parts.
        Assert.DoesNotContain("backdrop", unsupported);

        // ⚠ Nor `details-content`, which the file said waited on the generated box: `Expander` is the
        // `<details>` here and builds the slot as `Part("expander-content")`.
        Assert.DoesNotContain("details-content", unsupported);
    }

    /// <summary>A variant v4 has that Vixen refuses and nobody wrote down.</summary>
    [Fact]
    public void An_unrecorded_refusal_is_TWP008() {
        Assert.Contains("TWP008", Codes(probe => probe == "hover"));
        Assert.DoesNotContain("TWP008", Codes(probe => probe == "hover", "before"));
    }

    /// <summary>⚠ A recorded refusal the engine has since learned, so the file can only shrink.</summary>
    [Fact]
    public void A_refusal_the_engine_has_outgrown_is_TWP009() {
        Assert.Contains("TWP009", Codes(_ => true, "before"));
    }

    /// <summary>A variant v4 compiles no class from, which is not the same as one Vixen lacks.</summary>
    [Fact]
    public void A_variant_with_no_probe_is_TWP010() {
        var registry = Miniature with { Variants = [new TailwindVariant("mystery", null)] };

        Assert.Equal(["TWP010"], VariantAudit.Run(registry, _ => true, []).Select(finding => finding.Code));
    }

    /// <summary>A line in the file naming a variant v4 does not have.</summary>
    [Fact]
    public void A_recorded_variant_tailwind_does_not_have_is_TWP011() {
        Assert.Contains("TWP011", Codes(_ => true, "no-such-variant"));
    }

    /// <summary>And a vocabulary that agrees reports nothing at all.</summary>
    [Fact]
    public void A_vocabulary_that_agrees_reports_nothing() {
        Assert.Empty(Codes(probe => probe == "hover", "before"));
    }
}
