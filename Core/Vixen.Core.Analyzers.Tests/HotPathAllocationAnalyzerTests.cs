// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.Core.Analyzers.Tests;

/// <summary>
///     The positives and the id-named negatives for <c>VXHP0001</c>.
/// </summary>
/// <remarks>
///     ⚠ The negatives are the half this rule can lose without anyone noticing. A rule that reported
///     <em>every</em> <c>new</c> would pass every positive here and be switched off inside a week,
///     because a <c>new Vector3</c> and a capture-free lambda are not allocations and the frame loop is
///     made of them.
/// </remarks>
public sealed class HotPathAllocationAnalyzerTests {
    const string Marked = """
                          using Vixen.Core;

                          """;

    /// <summary>
    ///     ⚠ Verifying the instrument before anything trusts it: the rule is keyed on
    ///     <c>GetTypeByMetadataName("Vixen.Core.HotPathAttribute")</c> and returns immediately when
    ///     that resolves to nothing. Every negative test in this file would pass against a harness
    ///     that could not see the attribute, so this asks the harness's own compilation whether it
    ///     can.
    /// </summary>
    [Fact]
    public void TheHarnessCompilationCanNameTheRealAttribute() {
        var compilation = AnalyzerHarness.Compile("public static class Empty { }");

        var attribute = compilation.GetTypeByMetadataName("Vixen.Core.HotPathAttribute");

        Assert.NotNull(attribute);
        Assert.Equal("Vixen.Core", attribute.ContainingNamespace.ToDisplayString());
        Assert.Equal(typeof(HotPathAttribute).Assembly.GetName().Name, attribute.ContainingAssembly.Name);
    }

    /// <summary>The id, the category and the severity <c>AnalyzerReleases.Unshipped.md</c> declares.</summary>
    [Fact]
    public void TheRuleIsTheOneTheReleaseFileDeclares() {
        var rule = Assert.Single(new HotPathAllocationAnalyzer().SupportedDiagnostics);

        Assert.Equal(HotPathAllocationAnalyzer.DiagnosticId, rule.Id);
        Assert.Equal("VXHP0001", rule.Id);
        Assert.Equal("Vixen.Performance", rule.Category);
        Assert.Equal(DiagnosticSeverity.Warning, rule.DefaultSeverity);
        Assert.True(rule.IsEnabledByDefault);
    }

    [Fact]
    public async Task ANewReferenceTypeIsReportedAtTheCreation() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              public sealed class Frame {
                  [HotPath]
                  public object Step() => new object();
              }
              """
        );

        var reported = Assert.Single(diagnostics);

        Assert.Equal("VXHP0001", reported.Id);
        Assert.Equal("new object()", AnalyzerHarness.Underlined(reported));
        Assert.Contains("Step", reported.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArrayIsAnAllocationEvenWhenItIsEmpty() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              public sealed class Frame {
                  [HotPath]
                  public int[] Step(int count) => new int[count];
              }
              """
        );

        var reported = Assert.Single(diagnostics);

        Assert.Equal("new int[count]", AnalyzerHarness.Underlined(reported));
    }

    [Fact]
    public async Task BoxingIsNamedByWhatIsBoxed() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              public sealed class Frame {
                  [HotPath]
                  public object Step(int value) => value;
              }
              """
        );

        var reported = Assert.Single(diagnostics);

        Assert.Contains(
            "boxing 'Int32'",
            reported.GetMessage(CultureInfo.InvariantCulture),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     Doc 00's rule in as many words — "no implicit closure in a <c>[HotPath]</c> method" — and
    ///     the shape it means: not one <c>this.</c> is written and the lambda captures the receiver
    ///     anyway.
    /// </summary>
    [Fact]
    public async Task ALambdaThatCapturesTheReceiverIsAClosure() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              using System;

              public sealed class Frame {
                  int budget;

                  [HotPath]
                  public Func<int, bool> Step() => value => value < budget;
              }
              """
        );

        var reported = Assert.Single(diagnostics);

        Assert.Contains(
            "a closure",
            reported.GetMessage(CultureInfo.InvariantCulture),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ALambdaThatCapturesALocalIsAClosureToo() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              using System;

              public static class Frame {
                  [HotPath]
                  public static Func<int, bool> Step(int budget) => value => value < budget;
              }
              """
        );

        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task AnInterpolatedStringIsBuiltPerCall() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              public sealed class Frame {
                  [HotPath]
                  public string Step(int index) => $"frame {index}";
              }
              """
        );

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture)
                .Contains("an interpolated string", StringComparison.Ordinal)
        );
    }

    /// <summary>A concatenation tree is one allocation worth reporting, not three.</summary>
    [Fact]
    public async Task AConcatenationTreeIsReportedOnce() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              public sealed class Frame {
                  [HotPath]
                  public string Step(string a, string b, string c) => a + b + c;
              }
              """
        );

        var reported = Assert.Single(diagnostics);

        Assert.Equal("a + b + c", AnalyzerHarness.Underlined(reported));
    }

    /// <summary>A marked type marks the members in it, which is what its AttributeUsage promises.</summary>
    [Fact]
    public async Task MarkingTheTypeMarksItsMembers() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              [HotPath]
              public sealed class Frame {
                  public object Step() => new object();
                  public object Also() => new object();
              }
              """
        );

        Assert.Equal(2, diagnostics.Length);
    }

    /// <summary>An accessor carries no attributes of its own, so the property has to be consulted.</summary>
    [Fact]
    public async Task AMarkedPropertyReachesItsAccessor() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              public sealed class Frame {
                  [HotPath]
                  public object Step => new object();
              }
              """
        );

        Assert.Single(diagnostics);
    }

    // ---------------------------------------------------------------------------------------------
    // VXHP0001 negatives. Each of these compiles to no heap allocation, and a rule that reported any
    // of them would be reporting a cost nobody pays.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Unmarked code allocates as it likes; the rule is a contract, not a policy.</summary>
    [Fact]
    public async Task VXHP0001IsSilentOnAMemberThatDidNotSignTheContract() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            """
            public sealed class Loader {
                public object Load() => new object();
            }
            """
        );

        Assert.Empty(diagnostics);
    }

    /// <summary>⚠ The one that keeps the rule usable: a struct is bytes where they already are.</summary>
    [Fact]
    public async Task VXHP0001IsSilentOnANewStruct() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              using System.Numerics;

              public sealed class Frame {
                  [HotPath]
                  public Vector3 Step(float x) => new Vector3(x, x, x);
              }
              """
        );

        Assert.Empty(diagnostics);
    }

    /// <summary>
    ///     ⚠ A capture-free lambda is allocated once and cached in a static field, so it is not a
    ///     per-call cost — and neither is a method group over a static method, which C# 11 caches the
    ///     same way.
    /// </summary>
    [Fact]
    public async Task VXHP0001IsSilentOnACaptureFreeLambdaAndAStaticMethodGroup() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              using System;

              public sealed class Frame {
                  static bool Positive(int value) => value > 0;

                  [HotPath]
                  public Func<int, bool> Free() => value => value > 0;

                  [HotPath]
                  public Func<int, bool> Group() => Positive;
              }
              """
        );

        Assert.Empty(diagnostics);
    }

    /// <summary>
    ///     ⚠ The one this rule got wrong first, and it got it wrong on <em>every</em> marked member:
    ///     a member's own attribute list is an operation block owned by that member, so
    ///     <c>[HotPath]</c> arrived as <c>new HotPathAttribute</c> inside the very method it had just
    ///     turned the rule on for. An attribute is metadata the compiler writes once; nothing
    ///     constructs it in a frame.
    /// </summary>
    [Fact]
    public async Task VXHP0001IsSilentOnTheAttributeListThatTurnedItOn() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              using System;

              public sealed class Frame {
                  [HotPath]
                  [Obsolete("superseded", false)]
                  public int Step(int value) => value;
              }
              """
        );

        Assert.Empty(diagnostics);
    }

    /// <summary>A stack buffer is the answer the rule wants people to reach for.</summary>
    [Fact]
    public async Task VXHP0001IsSilentOnStackalloc() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              using System;

              public sealed class Frame {
                  [HotPath]
                  public int Step() {
                      Span<int> scratch = stackalloc int[8];
                      scratch[0] = 1;

                      return scratch[0];
                  }
              }
              """
        );

        Assert.Empty(diagnostics);
    }

    /// <summary>
    ///     A frame that throws has already lost. Demanding a cached exception is how a rule teaches
    ///     people to swallow errors instead of throwing them.
    /// </summary>
    [Fact]
    public async Task VXHP0001IsSilentUnderAThrow() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              using System;

              public sealed class Frame {
                  [HotPath]
                  public int Step(int value) =>
                      value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
              }
              """
        );

        Assert.Empty(diagnostics);
    }

    /// <summary>A constant string is in the metadata and interned once for the assembly.</summary>
    [Fact]
    public async Task VXHP0001IsSilentOnAConstantConcatenation() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              public sealed class Frame {
                  [HotPath]
                  public string Step() => "frame " + "zero";
              }
              """
        );

        Assert.Empty(diagnostics);
    }

    /// <summary>
    ///     A struct-to-struct conversion is not boxing, and neither is widening one number into
    ///     another.
    /// </summary>
    [Fact]
    public async Task VXHP0001IsSilentOnANumericConversion() {
        var diagnostics = await AnalyzerHarness.RunAsync(
            Marked
            + """
              public sealed class Frame {
                  [HotPath]
                  public double Step(int value) => value;
              }
              """
        );

        Assert.Empty(diagnostics);
    }
}
