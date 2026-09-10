// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.Core.Diagnostics.Analyzers.Tests;

/// <summary>The positives and the id-named negatives for <c>VXLG0001</c>.</summary>
/// <remarks>
///     ⚠ The negatives carry the weight here. The reported shape is one line away from four correct
///     ones — a catch that logs the exception, one that rethrows, one with a filter, one that named a
///     narrower type — and a rule that reported any of those would be switched off within a week,
///     which is how the shape it exists for gets through with it. Two of the four are also the ones a
///     text scan gets wrong: an exception mentioned only inside an interpolated string, and one
///     mentioned only inside a lambda. The instrument this rule replaced got exactly those wrong on
///     three of eight findings.
/// </remarks>
public sealed class SilentCatchAnalyzerTests {
    const string Scaffold = """
                            using System;
                            using System.IO;

                            public sealed class Subsystem {
                                public string? Note;

                                public void Work() { }

                                public void Log(string message) { }

                                public void Log(Exception failure) { }

                                public void Later(Action work) { }

                            """;

    /// <summary>
    ///     ⚠ Verifying the instrument before anything trusts it. The rule returns immediately when
    ///     <c>System.Exception</c> resolves to nothing, and every negative in this file would pass
    ///     against a harness whose compilation could not name it — as would every positive, silently,
    ///     if the harness were handed the wrong analyzer. This asks the compilation for the type and
    ///     the next test asks the analyzer for its id.
    /// </summary>
    [Fact]
    public void TheHarnessCompilationCanNameSystemException() {
        var compilation = AnalyzerHarness.Compile("public static class Empty { }");

        var exception = compilation.GetTypeByMetadataName("System.Exception");

        Assert.NotNull(exception);
        Assert.Equal("System", exception.ContainingNamespace.ToDisplayString());
    }

    /// <summary>The id, the category and the severity <c>AnalyzerReleases.Unshipped.md</c> declares.</summary>
    [Fact]
    public void TheRuleIsTheOneTheReleaseFileDeclares() {
        var rule = Assert.Single(new SilentCatchAnalyzer().SupportedDiagnostics);

        Assert.Equal(SilentCatchAnalyzer.DiagnosticId, rule.Id);
        Assert.Equal("VXLG0001", rule.Id);
        Assert.Equal("Vixen.Diagnostics", rule.Category);
        Assert.Equal(DiagnosticSeverity.Warning, rule.DefaultSeverity);
        Assert.True(rule.IsEnabledByDefault);
    }

    [Fact]
    public async Task AnEmptyBareCatchIsReported() {
        var diagnostics = await RunAsync(
            """
                public void Step() {
                    try {
                        Work();
                    } catch {
                    }
                }
            }
            """
        );

        var reported = Assert.Single(diagnostics);

        Assert.Equal("VXLG0001", reported.Id);
        Assert.Equal("catch", AnalyzerHarness.Underlined(reported));
    }

    /// <summary>
    ///     The commonest form: the failure is turned into a return value and nothing anywhere records
    ///     what it was.
    /// </summary>
    [Fact]
    public async Task ACatchOfExceptionThatNeverNamesItIsReported() {
        var diagnostics = await RunAsync(
            """
                public bool Step() {
                    try {
                        Work();

                        return true;
                    } catch (Exception) {
                        return false;
                    }
                }
            }
            """
        );

        var reported = Assert.Single(diagnostics);

        Assert.Equal("catch (Exception)", AnalyzerHarness.Underlined(reported));
    }

    /// <summary>
    ///     ⚠ Doc 13 says "logs <em>with the exception object</em>", and this is why the words are in
    ///     it: a log line that says a thing failed and not what the failure was leaves the reader
    ///     exactly where they started.
    /// </summary>
    [Fact]
    public async Task ACatchThatLogsWithoutTheExceptionIsReported() {
        var diagnostics = await RunAsync(
            """
                public void Step() {
                    try {
                        Work();
                    } catch (Exception) {
                        Log("the step failed");
                    }
                }
            }
            """
        );

        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task ANamedExceptionHandedToTheLoggerIsNotReported() {
        var diagnostics = await RunAsync(
            """
                public void Step() {
                    try {
                        Work();
                    } catch (Exception failure) {
                        Log(failure);
                    }
                }
            }
            """
        );

        Assert.Empty(diagnostics);
    }

    /// <summary>
    ///     ⚠ The first of the two shapes a text scan gets wrong. An interpolation hole is source the
    ///     scanner blanks along with the rest of the string literal, and this clause is correct.
    /// </summary>
    [Fact]
    public async Task AnExceptionNamedOnlyInsideAnInterpolatedStringIsNotReported() {
        var diagnostics = await RunAsync(
            """
                public void Step() {
                    try {
                        Work();
                    } catch (Exception failure) {
                        Note = $"the step failed: {failure.Message}";
                    }
                }
            }
            """
        );

        Assert.Empty(diagnostics);
    }

    /// <summary>
    ///     The second. The exception reaches a closure that runs later, which is how a failure crosses
    ///     back to a thread that can report it.
    /// </summary>
    [Fact]
    public async Task AnExceptionNamedOnlyInsideALambdaIsNotReported() {
        var diagnostics = await RunAsync(
            """
                public void Step() {
                    try {
                        Work();
                    } catch (Exception failure) {
                        Later(() => Log(failure));
                    }
                }
            }
            """
        );

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ACatchThatRethrowsIsNotReported() {
        var diagnostics = await RunAsync(
            """
                public void Step() {
                    try {
                        Work();
                    } catch (Exception) {
                        Note = "seen";

                        throw;
                    }
                }
            }
            """
        );

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ACatchWithAFilterIsNotReported() {
        var diagnostics = await RunAsync(
            """
                public void Step() {
                    try {
                        Work();
                    } catch (Exception failure) when (failure is IOException) {
                    }
                }
            }
            """
        );

        Assert.Empty(diagnostics);
    }

    /// <summary>
    ///     ⚠ The scope decision, held to by a test so that widening it is a deliberate act. A named
    ///     type is a decision about a named failure; <c>Core/</c> has twelve such clauses and each is
    ///     written with its reason above it.
    /// </summary>
    [Fact]
    public async Task AnEmptyCatchOfANarrowerTypeIsNotReported() {
        var diagnostics = await RunAsync(
            """
                public void Step() {
                    try {
                        Work();
                    } catch (IOException) {
                    }
                }
            }
            """
        );

        Assert.Empty(diagnostics);
    }

    /// <summary>
    ///     A file with no <c>try</c> in it at all, which is what almost every compilation the rule runs
    ///     over looks like.
    /// </summary>
    [Fact]
    public async Task CodeWithNoCatchIsNotReported() {
        var diagnostics = await RunAsync(
            """
                public void Step() {
                    Work();
                }
            }
            """
        );

        Assert.Empty(diagnostics);
    }

    /// <summary>Two silent clauses on one <c>try</c> are two findings, not one.</summary>
    [Fact]
    public async Task EachSilentClauseIsReportedOnceOnItsOwn() {
        var diagnostics = await RunAsync(
            """
                public void Step() {
                    try {
                        Work();
                    } catch (IOException) {
                    }

                    try {
                        Work();
                    } catch (Exception) {
                    }

                    try {
                        Work();
                    } catch {
                    }
                }
            }
            """
        );

        Assert.Equal(2, diagnostics.Length);
    }

    static Task<ImmutableArray<Diagnostic>> RunAsync(string body) =>
        AnalyzerHarness.RunAsync(Scaffold + body);
}
