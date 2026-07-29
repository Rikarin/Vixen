// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.Core.IO.Analyzers.Tests;

public class SystemIoPathAnalyzerTests {
    [Fact]
    public void TheRuleIsTheOneTheEditorConfigScopes() {
        // The id, the category and the default severity are what `.editorconfig` and
        // AnalyzerReleases.Unshipped.md name. Change one here and the scoping silently stops
        // applying — an exclusion that matches nothing looks exactly like a rule with no findings.
        var rule = Assert.Single(new SystemIoPathAnalyzer().SupportedDiagnostics);

        Assert.Equal(SystemIoPathAnalyzer.DiagnosticId, rule.Id);
        Assert.Equal("VXIO0001", rule.Id);
        Assert.Equal("Vixen.IO", rule.Category);
        Assert.Equal(DiagnosticSeverity.Warning, rule.DefaultSeverity);
        Assert.True(rule.IsEnabledByDefault);
    }

    [Fact]
    public async Task ACallIsReportedAtTheCallRatherThanAtTheType() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            """
            using System.IO;

            public static class Loader {
                public static string Of(string root, string name) => Path.Combine(root, name);
            }
            """
        );

        var reported = Assert.Single(diagnostics);

        Assert.Equal("VXIO0001", reported.Id);
        Assert.Equal("Path.Combine", AnalyzerHarness.Underlined(reported));
        Assert.Contains(
            "System.IO.Path.Combine",
            reported.GetMessage(CultureInfo.InvariantCulture),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task QualifyingItDoesNotHideItAndIsReportedWhole() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            """
            public static class Loader {
                public static string Of(string root, string name) => System.IO.Path.Combine(root, name);
            }
            """
        );

        var reported = Assert.Single(diagnostics);

        Assert.Equal("System.IO.Path.Combine", AnalyzerHarness.Underlined(reported));
    }

    [Fact]
    public async Task APropertyIsAUseAsMuchAsACallIs() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            """
            using System.IO;

            public static class Loader {
                public static char Separator => Path.DirectorySeparatorChar;
            }
            """
        );

        var reported = Assert.Single(diagnostics);

        Assert.Equal("Path.DirectorySeparatorChar", AnalyzerHarness.Underlined(reported));
        Assert.Contains(
            "System.IO.Path.DirectorySeparatorChar",
            reported.GetMessage(CultureInfo.InvariantCulture),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task AUsingStaticIsReportedAndSoIsWhatItBringsIn() {
        // The import alone would be a rule that reports a line nobody reads, and the unqualified
        // call alone would be a rule that misses the import in a file that has not called anything
        // yet. Both, because the import is the decision and the call is where it lands.
        var diagnostics = await AnalyzerHarness.RunAsync(
            """
            using static System.IO.Path;

            public static class Loader {
                public static string Of(string root, string name) => Combine(root, name);
            }
            """
        );

        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, found => AnalyzerHarness.Underlined(found) == "System.IO.Path");
        Assert.Contains(diagnostics, found => AnalyzerHarness.Underlined(found) == "Combine");
    }

    [Fact]
    public async Task RenamingItDoesNotHideIt() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            """
            using Files = System.IO.Path;

            public static class Loader {
                public static string Of(string root, string name) => Files.Combine(root, name);
            }
            """
        );

        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, found => AnalyzerHarness.Underlined(found) == "System.IO.Path");
        Assert.Contains(diagnostics, found => AnalyzerHarness.Underlined(found) == "Files.Combine");
    }

    [Fact]
    public async Task AMethodGroupIsAUseToo() {
        // Named rather than called, so overload resolution has not run and the identifier binds to
        // candidates rather than to a symbol. It is still the BCL doing the addressing.
        var diagnostics = await AnalyzerHarness.RunAsync(
            """
            using System;
            using System.IO;

            public static class Loader {
                public static Func<string, string, string> Combining() => Path.Combine;
            }
            """
        );

        var reported = Assert.Single(diagnostics);

        Assert.Equal("Path.Combine", AnalyzerHarness.Underlined(reported));
        Assert.Contains(
            "System.IO.Path.Combine",
            reported.GetMessage(CultureInfo.InvariantCulture),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task NamingTheTypeAloneIsEnoughToReport() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            """
            using System;
            using System.IO;

            public static class Loader {
                public static Type Which() => typeof(Path);
            }
            """
        );

        var reported = Assert.Single(diagnostics);

        Assert.Equal("Path", AnalyzerHarness.Underlined(reported));
        Assert.Contains(
            "System.IO.Path ",
            reported.GetMessage(CultureInfo.InvariantCulture),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task DocumentationThatMentionsItIsNotUseOfIt() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            """
            using System.IO;

            /// <summary>Segments of a virtual path.</summary>
            public static class VirtualPaths {
                /// <summary>The extension, by the convention <see cref="Path.GetExtension(string)" /> uses.</summary>
                /// <param name="path">The path.</param>
                /// <returns>The extension.</returns>
                public static string ExtensionOf(string path) => path;
            }
            """
        );

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task OurOwnPathIsNotTheirs() {
        // `entry.Path`, `mount.Path` and a local `Combine` are all over the engine. A rule that
        // matched on the name would report every one of them and be turned off within a week.
        var diagnostics = await AnalyzerHarness.RunAsync(
            """
            public sealed record FileEntry(string Path, long Length);

            public static class Reader {
                public static string NameOf(FileEntry entry) => entry.Path;

                public static string Combine(string left, string right) => left + "/" + right;

                public static string Both(FileEntry entry) => Combine(entry.Path, "x");
            }
            """
        );

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task VirtualPathsAreWhatTheRuleIsFor() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            """
            using System.IO;

            public static class Loader {
                public static Stream Of(Stream source) => source;
            }
            """
        );

        // Importing System.IO is not the violation — Stream, and every provider, lives there.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task GeneratedCodeIsNotOursToFix() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            """
            // <auto-generated/>
            using System.IO;

            public static class Loader {
                public static string Of(string root, string name) => Path.Combine(root, name);
            }
            """,
            "Loader.g.cs"
        );

        Assert.Empty(diagnostics);
    }
}
