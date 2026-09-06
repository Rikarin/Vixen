// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     The one Nuke rule that turns a whole target into a no-op without failing, held over
///     <c>build/</c>'s source.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nuke quotes each element of <c>ApplicationArguments</c>.</b> So a whole command line
///         handed to <c>SetApplicationArguments</c> as one string reaches the process as a single
///         <c>argv</c> entry — <c>-- "--filter * --artifacts … --exporters json"</c> — and whatever
///         was being driven either refuses it or ignores it. That is what
///         <a href="https://github.com/Rikarin/Vixen/issues/339">#339</a>'s
///         <c>Benchmark</c> target did: BenchmarkDotNet's <c>ConfigParser</c> answered
///         <c>isSuccess=false</c>, printed its help screen and ran nothing, and the failure a reader
///         would eventually have seen was "no reports to write a baseline from" — the symptom, three
///         steps from the cause.
///     </para>
///     <para>
///         ⚠ <b>It is invisible until somebody runs the target, and nobody had.</b> Every other
///         <c>DotNetRun</c> in <c>build/</c> passed a list; this was the only call site that did not,
///         in the one target that had never been executed. Nothing prevented the next one, because
///         <c>build/_build.csproj</c> is outside the solution and has no test project of its own —
///         which is why this lives here, beside <c>AotProbeProjectFileTests</c> and
///         <c>CoverageReportTests</c>, the other two build files with a fixture.
///     </para>
///     <para>
///         Syntax only. Nothing here compiles <c>build/</c> — the defect is visible in the shape of
///         the call, and asking Roslyn for the shape is exact where a grep for <c>$"</c> would be a
///         guess.
///     </para>
/// </remarks>
public sealed class BuildArgumentQuotingTests {
    /// <summary>
    ///     A command line in one element is one argument. Several arguments need several elements.
    /// </summary>
    [Fact]
    public void NoApplicationArgumentsAreAWholeCommandLineInOneString() {
        var offenders = new List<string>();

        foreach (var (file, root) in BuildSources()) {
            foreach (var call in Calls(root, "SetApplicationArguments")) {
                if (call.ArgumentList.Arguments.Count != 1) {
                    continue;
                }

                var expression = call.ArgumentList.Arguments[0].Expression;

                if (LiteralText(expression) is { } text && text.Trim().Contains(' ', StringComparison.Ordinal)) {
                    offenders.Add($"{file}({Line(call)}): {text.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Nuke quotes each element of ApplicationArguments, so a whole command line in one string "
            + "reaches the process as ONE argv entry and the tool refuses or ignores it — silently, "
            + "until somebody runs the target. Pass a List<string> with one element per argument:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders)
        );
    }

    /// <summary>
    ///     ⚠ The same trap wearing the opposite shape. <c>DotNet(…)</c> takes an
    ///     <c>ArgumentStringHandler</c>, which quotes the <em>interpolation holes</em> and leaves the
    ///     literal text as written — so an interpolated string is the correct form and a single
    ///     already-built <c>string</c> is not: the handler receives it as one hole and quotes the
    ///     whole command line.
    /// </summary>
    [Fact]
    public void NoDotNetCommandLineIsBuiltBeforeItIsPassed() {
        var offenders = new List<string>();

        foreach (var (file, root) in BuildSources()) {
            foreach (var call in Calls(root, "DotNet")) {
                if (call.ArgumentList.Arguments.Count == 0) {
                    continue;
                }

                var expression = call.ArgumentList.Arguments[0].Expression;

                if (expression is InterpolatedStringExpressionSyntax or LiteralExpressionSyntax) {
                    continue;
                }

                offenders.Add($"{file}({Line(call)}): DotNet({expression})");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "DotNet takes an ArgumentStringHandler, which quotes each interpolation hole and leaves "
            + "the literal text alone. A command line built into a string first arrives as one hole "
            + "and is quoted whole, so the process gets a single argument, runs nothing and exits "
            + "non-zero in under a second. Write the interpolated string at the call:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders)
        );
    }

    /// <summary>
    ///     The guard both of the above need: a walk that parsed nothing, or found neither call, would
    ///     agree with everything.
    /// </summary>
    [Fact]
    public void TheWalkAboveActuallyReadsTheBuildAndFindsBothCalls() {
        var sources = BuildSources();

        Assert.True(sources.Count > 10, $"Only {sources.Count} files under build/ — the walk is wrong.");

        Assert.NotEmpty(sources.SelectMany(source => Calls(source.Root, "SetApplicationArguments")));
        Assert.NotEmpty(sources.SelectMany(source => Calls(source.Root, "DotNet")));
    }

    /// <summary>
    ///     The literal text of a string expression, with the interpolation holes left out; null when
    ///     the expression is not a string written at the call site.
    /// </summary>
    /// <remarks>
    ///     The holes are dropped rather than rendered because a hole is exactly what Nuke quotes
    ///     correctly — a path with a space in it is not the defect. What is being looked for is a
    ///     space in the text the author typed, which is an argument separator.
    /// </remarks>
    static string? LiteralText(ExpressionSyntax expression) =>
        expression switch {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) =>
                literal.Token.ValueText,
            InterpolatedStringExpressionSyntax interpolated => string.Concat(
                interpolated.Contents.OfType<InterpolatedStringTextSyntax>().Select(text => text.TextToken.ValueText)
            ),
            _ => null
        };

    static IEnumerable<InvocationExpressionSyntax> Calls(SyntaxNode root, string method) =>
        root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(call => NameOf(call.Expression) == method);

    static string? NameOf(ExpressionSyntax expression) =>
        expression switch {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => null
        };

    static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    static List<(string File, SyntaxNode Root)> BuildSources() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "build"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(RepositoryRoot(), file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj")
            )
            .Order(StringComparer.Ordinal)
            .Select(file => (
                File: Path.GetFileName(file),
                Root: CSharpSyntaxTree.ParseText(System.IO.File.ReadAllText(file)).GetRoot()
            ))
            .ToList();

    static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (System.IO.File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
