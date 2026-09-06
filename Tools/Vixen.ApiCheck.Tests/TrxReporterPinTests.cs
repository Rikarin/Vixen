// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     The TRX reporter's version is written twice, and this is what stops the two copies drifting.
/// </summary>
/// <remarks>
///     <para>
///         Every <c>.Tests</c> project gets <c>Microsoft.Testing.Extensions.TrxReport</c> from
///         <c>Directory.Build.props</c>, because the one project that forgets is the one whose
///         results go missing. Almost all of them take the version from
///         <c>Directory.Packages.props</c> — but <c>Vixen.DocGen.Tests</c> and
///         <c>Vixen.Templates.Tests</c> set <c>ManagePackageVersionsCentrally</c> to
///         <c>false</c> because each needs a Roslyn version the central pin forbids, so for those two
///         a version-less reference has nothing to inherit and restore fails with <c>NU1015</c> for
///         the whole solution.
///     </para>
///     <para>
///         ⚠ <b>The obvious repair — one <c>$(property)</c> read by both files — was tried and
///         reverted, because it defeats a gate.</b> <c>CheckAttributionManifest</c> reads the pinned
///         version out of <c>Directory.Packages.props</c> as <i>text</i> to compare it with the
///         licence row in <c>docs/manual/third-party.md</c>. Behind an indirection it reads
///         <c>$(VixenTrxReportVersion)</c>, cannot match any version, and reports the manifest as
///         disagreeing with the pin — a check made blind by a tidier spelling. So both literals stay,
///         and the drift they invite is what this test is for.
///     </para>
///     <para>
///         It reads committed text and takes about a millisecond, which is the point: the failure it
///         prevents otherwise surfaces as a restore error naming two projects that have nothing to do
///         with it.
///     </para>
/// </remarks>
public class TrxReporterPinTests {
    const string Package = "Microsoft.Testing.Extensions.TrxReport";

    /// <summary>Both files pin the reporter at the same version.</summary>
    [Fact]
    public void The_two_pins_of_the_trx_reporter_agree() {
        var central = Pin("Directory.Packages.props", "PackageVersion");
        var local = Pin("Directory.Build.props", "PackageReference");

        Assert.Equal(central, local, StringComparer.Ordinal);
    }

    /// <summary>The version documented in the licence manifest is the version pinned.</summary>
    /// <remarks>
    ///     ⚠ The same claim <c>CheckAttributionManifest</c> makes, and deliberately duplicated: that
    ///     one needs a Release build of the solution and eleven minutes, which is why nobody ran it
    ///     for the two weeks a stale exemption sat on <c>master</c>. This one costs a file read.
    /// </remarks>
    [Fact]
    public void The_licence_manifest_documents_the_version_that_is_pinned() {
        var manifest = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "manual", "third-party.md"));
        var row = manifest.Split('\n').FirstOrDefault(line => line.Contains(Package, StringComparison.Ordinal));

        Assert.True(row is not null, $"docs/manual/third-party.md has no row for {Package}.");
        Assert.Contains(Pin("Directory.Packages.props", "PackageVersion"), row!, StringComparison.Ordinal);
    }

    static string Pin(string file, string element) {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), file));
        var match = Regex.Match(
            text,
            $"<{element}\\s+Include=\"{Regex.Escape(Package)}\"[^>]*?\\s+Version=\"(?<version>[^\"]+)\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)
        );

        Assert.True(match.Success, $"{file} has no <{element}> for {Package} carrying a Version.");

        var version = match.Groups["version"].Value;

        Assert.False(
            version.StartsWith("$(", StringComparison.Ordinal),
            $"{file} pins {Package} at '{version}'. A property here is what makes "
            + "CheckAttributionManifest unable to read the pin — see this class's remarks."
        );

        return version;
    }

    static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
