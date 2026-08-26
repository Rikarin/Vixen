// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     The thirteen diagnostics that nothing in this suite had ever made fire.
/// </summary>
/// <remarks>
///     <para>
///         A rule with no negative test says it fires but not that it fires only when it should.
///         These are the worse case one step earlier: a rule nothing proves fires <em>at all</em>.
///         An id in this state is not a rule, it is a claim about the compiler with no evidence —
///         its message could be malformed, its arguments in the wrong order, its raise site behind a
///         condition that has been false since the day it was written, and every test in the
///         repository would still be green.
///     </para>
///     <para>
///         ⚠ Ten of the thirteen fire, and all ten are pinned below. The other three cannot be made
///         to fire by any input, for three different reasons, and each is written down here rather
///         than quietly left out — see <c>The_three_that_cannot_fire</c>.
///     </para>
/// </remarks>
public class UnprovenDiagnosticTests {
    // --- Plumbing ----------------------------------------------------------

    static IReadOnlyList<Diagnostic> Parsed(string source) => SyntaxTree.ParseText(source, path: "Test.rvn").Diagnostics;

    static IReadOnlyList<Diagnostic> Semantic(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        return Compilation.Create("Test", tree).GetDiagnostics();
    }

    /// <summary>Only what lowering said, with the binder's report kept separate.</summary>
    static IReadOnlyList<Diagnostic> Lowered(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var bag = new DiagnosticBag();
        Lowerer.Lower(Compilation.Create("Test", tree), bag);

        return bag.ToArray();
    }

    /// <summary>Only what the named backend said, having got there cleanly.</summary>
    static IReadOnlyList<Diagnostic> Generated(string source, string target) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        var backend = new DiagnosticBag();
        TargetBackends.Create(target)!.Generate(module, backend);

        return backend.ToArray();
    }

    static Diagnostic Fires(string id, IReadOnlyList<Diagnostic> diagnostics) =>
        Assert.Single(diagnostics, d => d.Id == id);

    // --- RVN1002: a character the lexer cannot tokenize ---------------------

    /// <summary>
    ///     The only lexer diagnostic there is, and the only one of the thirteen that never reaches
    ///     the binder.
    /// </summary>
    [Fact]
    public void An_invalid_character_is_reported_by_the_lexer() {
        var reported = Fires(
            "RVN1002",
            Parsed(
                """
                package A

                shader S {
                    func F(): int {
                        val x = 1 # 2
                        return x
                    }
                }

                """
            )
        );

        Assert.True(reported.IsError);
        Assert.Contains("'#'", reported.GetMessage(), StringComparison.Ordinal);
    }

    // --- RVN2021: a conversion that has to be asked for and is not there ----

    /// <summary>
    ///     A cast written explicitly between two types that have no conversion at all — distinct
    ///     from <c>RVN2020</c>, which is the implicit one being missing.
    /// </summary>
    [Fact]
    public void An_explicit_cast_with_no_conversion_behind_it_is_reported() {
        var reported = Fires(
            "RVN2021",
            Semantic(
                """
                package A

                struct P {
                    var a: float
                }

                shader S {
                    func F(p: P): int {
                        return (int)p
                    }
                }

                """
            )
        );

        Assert.True(reported.IsError);
        Assert.Contains("A.P", reported.GetMessage(), StringComparison.Ordinal);
    }

    // --- RVN2023: a unary operator the operand's type does not define -------

    /// <summary>
    ///     Negation of a struct. The binary sibling <c>RVN2022</c> had a test and this one did not,
    ///     which is the shape most of these gaps have: one arm of a pair gets written.
    /// </summary>
    [Fact]
    public void A_unary_operator_with_no_definition_is_reported() {
        var reported = Fires(
            "RVN2023",
            Semantic(
                """
                package A

                struct P {
                    var a: float
                }

                shader S {
                    func F(p: P): float {
                        val q = -p
                        return q.a
                    }
                }

                """
            )
        );

        Assert.True(reported.IsError);
        Assert.Contains("'-'", reported.GetMessage(), StringComparison.Ordinal);
    }

    // --- RVN2032: two overloads that fit equally well -----------------------

    /// <summary>
    ///     A call that could go either way. <c>RVN2031</c> — nothing fits — was covered; this is
    ///     the opposite failure, where too much does.
    /// </summary>
    [Fact]
    public void An_invocation_no_overload_wins_is_reported() {
        var reported = Fires(
            "RVN2032",
            Semantic(
                """
                package A

                shader S {
                    func G(a: int, b: float): int {
                        return a
                    }

                    func G(a: float, b: int): int {
                        return b
                    }

                    func F(): int {
                        return G(1, 1)
                    }
                }

                """
            )
        );

        Assert.True(reported.IsError);
        Assert.Contains("ambiguous", reported.GetMessage(), StringComparison.Ordinal);
    }

    // --- RVN2041: an assignment target that is not a place ------------------

    /// <summary>
    ///     Assigning to a call's result. <c>RVN2040</c> — assignable but not by this shader — was
    ///     covered; this is the target having nowhere to be at all.
    /// </summary>
    [Fact]
    public void An_assignment_to_something_that_is_not_a_place_is_reported() {
        var reported = Fires(
            "RVN2041",
            Semantic(
                """
                package A

                shader S {
                    func G(): int {
                        return 1
                    }

                    func F(): int {
                        G() = 2
                        return 0
                    }
                }

                """
            )
        );

        Assert.True(reported.IsError);
    }

    // --- RVN3001: a type with no GPU representation -------------------------

    /// <summary>
    ///     A protocol as a parameter type. A protocol is how a <c>compose</c> slot is declared and
    ///     the composition is resolved before lowering — so one that survives to lowering is a value
    ///     of an interface type, which has no representation on either target.
    /// </summary>
    [Fact]
    public void A_type_with_no_representation_is_reported_at_lowering() {
        var reported = Fires(
            "RVN3001",
            Lowered(
                """
                package A

                protocol ISurface {
                    func Compute(inout value: float4)
                }

                shader Base : ISurface {
                    func Compute(inout value: float4) {
                        value += float4(1f, 1f, 1f, 1f)
                    }
                }

                shader S {
                    func Take(s: ISurface): float {
                        return 0f
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return float4(0f, 0f, 0f, 1f)
                    }
                }

                """
            )
        );

        Assert.True(reported.IsError);
        Assert.Contains("A.ISurface", reported.GetMessage(), StringComparison.Ordinal);
    }

    // --- RVN3002 / RVN3004: a member lowering has nothing to lower ----------

    /// <summary>
    ///     A method declared with no body: <c>RVN3004</c> for the declaration and <c>RVN3002</c> for
    ///     the call that cannot reach it.
    /// </summary>
    /// <remarks>
    ///     One fixture for both because they are one mistake seen twice, and because the pairing is
    ///     the interesting fact: lowering reports the hole where it is and again where it is stepped
    ///     in, which is what makes the second message worth having.
    /// </remarks>
    [Fact]
    public void A_declaration_with_no_body_is_reported_where_it_is_and_where_it_is_called() {
        var diagnostics = Lowered(
            """
            package A

            shader S {
                func Declared(): float

                [FragmentShader]
                [Semantic("SV_Target")]
                func Fragment(): float4 {
                    return float4(Declared(), 0f, 0f, 1f)
                }
            }

            """
        );

        var missing = Fires("RVN3004", diagnostics);
        Assert.True(missing.IsError);
        Assert.Contains("A.S.Declared", missing.GetMessage(), StringComparison.Ordinal);

        var call = Fires("RVN3002", diagnostics);
        Assert.True(call.IsError);
        Assert.Contains("Declared", call.GetMessage(), StringComparison.Ordinal);
    }

    // --- RVN3003: an assignment target with no address ----------------------

    /// <summary>
    ///     The lowering half of <c>RVN2041</c>. The binder refuses the source first, so this is
    ///     defence in depth against an IR that arrives from anywhere else — but it does fire, and
    ///     the two ids together are what says which layer caught it.
    /// </summary>
    [Fact]
    public void An_assignment_with_no_storage_behind_it_is_reported_at_lowering() {
        var reported = Fires(
            "RVN3003",
            Lowered(
                """
                package A

                shader S {
                    func G(): int {
                        return 1
                    }

                    func F(): int {
                        G() = 2
                        return 0
                    }
                }

                """
            )
        );

        Assert.True(reported.IsError);
    }

    // --- RVN4002: a construct one backend has not implemented ---------------

    /// <summary>
    ///     Indexing a value that never became memory — a call's result — with an index that is not a
    ///     constant.
    /// </summary>
    /// <remarks>
    ///     ⚠ The two backends disagree here, and the disagreement is the reason this test asserts
    ///     both. GLSL can write <c>Table()[i]</c> as it stands; SPIR-V cannot, because
    ///     <c>OpCompositeExtract</c> takes literal indices and there is no pointer to build an
    ///     access chain from. So the same shader is <c>RVN4002</c> on one target and clean code on
    ///     the other, which is exactly what a per-backend id is for — and asserting only the SPIR-V
    ///     half would leave the claim "GLSL has this hole too" untested.
    /// </remarks>
    [Fact]
    public void A_construct_only_one_backend_lacks_is_reported_by_that_backend() {
        const string Source = """
                              package A

                              shader S {
                                  var which: uint

                                  func Table(): float[4] {
                                      return [1f, 2f, 3f, 4f]
                                  }

                                  [FragmentShader]
                                  [Semantic("SV_Target")]
                                  func Fragment(): float4 {
                                      return float4(Table()[int(which)], 0f, 0f, 1f)
                                  }
                              }

                              """;

        var reported = Fires("RVN4002", Generated(Source, "spirv"));
        Assert.True(reported.IsError);
        Assert.Contains("SPIR-V", reported.GetMessage(), StringComparison.Ordinal);

        Assert.DoesNotContain(Generated(Source, "glsl"), d => d.Id == "RVN4002");
    }

    // --- The two that cannot fire, and the one that was deleted -------------

    /// <summary>
    ///     <c>RVN2003</c> and <c>RVN2014</c>, and why no fixture above pins them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both are the <em>defensive</em> kind of unreachable rather than the dead kind: they
    ///         have raise sites, those sites are correct, and no input gets to them. That is worth
    ///         keeping and worth writing down, because the alternative — deleting the arm — is how a
    ///         closed hierarchy silently grows a case nobody handles.
    ///     </para>
    ///     <para>
    ///         <c>RVN2003</c> — <c>NotAType</c> — has two raise sites and both are the
    ///         <c>default:</c> arm of a switch over a closed hierarchy whose other arms are
    ///         exhaustive. <c>TypeSyntax</c> has exactly six concrete forms — identifier, generic,
    ///         qualified, predefined, array, tuple — and <c>Binder.BindTypeCore</c> handles all six;
    ///         <c>BindNamespaceOrTypeQualifier</c> takes a <c>NameSyntax</c>, of which there are
    ///         exactly three, and handles all three. A name that is found but is not a type is
    ///         <c>RVN2002</c>, from a different arm.
    ///     </para>
    ///     <para>
    ///         <c>RVN2014</c> — <c>SelfOutsideType</c> — fires when a binder has no containing type,
    ///         and no binder ever has none. <c>Compilation.EnsureDeclarations</c> keeps only the
    ///         top-level members <c>TypeDeclarationInfo.From</c> yields a declaration for, so every
    ///         body that is bound belongs to a type. ⚠ A package-level <c>func</c> or
    ///         <c>const val</c> used to be <em>dropped</em> here, silently, which is what made this
    ///         paragraph's claim true and was a bug of its own: the body was never bound, so an
    ///         undefined name inside it was never reported and the file compiled clean around a
    ///         function that did not exist. It is now <c>RVN2054</c>, and still not bound — a
    ///         namespace holds namespaces and types and nothing else, so nothing could name one. So
    ///         this stays unreachable, for a stated reason instead of an accident, and becomes
    ///         reachable the day a namespace can hold a member.
    ///     </para>
    ///     <para>
    ///         ⚠ A third — <c>RVN2012</c>, <c>AmbiguousName</c> — was neither: it had no raise site
    ///         anywhere in the repository, only a declaration and a <c>PublicAPI</c> entry. It has
    ///         been deleted rather than left as a slot, and <c>SemanticDiagnostics</c> carries the
    ///         note saying so. Colliding declarations are <c>RVN2001</c>; a call two overloads fit
    ///         equally is <c>RVN2032</c>, which is pinned above.
    ///     </para>
    ///     <para>
    ///         What is left to pin for the two survivors is their message, so that is what this
    ///         does: an id that cannot be reached from source can still be reached from a decoded
    ///         <c>.rvnlib</c> or a future parser, and a descriptor whose format string outnumbers
    ///         its arguments would then throw instead of reporting.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_two_that_cannot_fire_still_carry_a_usable_message() {
        foreach (var descriptor in new[] {
                     Vixen.Raven.Diagnostics.SemanticDiagnostics.NotAType,
                     Vixen.Raven.Diagnostics.SemanticDiagnostics.SelfOutsideType
                 }) {
            var reported = Diagnostic.Create(descriptor, Location.None, "x");

            Assert.True(reported.IsError);
            Assert.NotEmpty(reported.GetMessage());
            Assert.DoesNotContain("{0}", reported.GetMessage(), StringComparison.Ordinal);
        }
    }

    // --- The guard that stops this recurring --------------------------------

    /// <summary>
    ///     Every descriptor the compiler declares is raised somewhere outside
    ///     <c>Diagnostics/</c> — and <c>RVN2012</c> stays retired.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the general form of the whole file, and the reason the file should not need
    ///         writing twice. <c>RVN2012</c> survived because nothing could see it: a descriptor
    ///         with no raise site compiles, ships, appears in the public API and reads exactly like
    ///         a rule. Reading the field names back off the type and looking for each one in the
    ///         source is what makes the difference visible, so the next descriptor added without a
    ///         <c>diagnostics.Add</c> behind it fails here on the day it lands.
    ///     </para>
    ///     <para>
    ///         ⚠ Being <em>raised</em> is a weaker claim than being <em>reachable</em>:
    ///         <c>NotAType</c> and <c>SelfOutsideType</c> both pass this and neither can fire, which
    ///         is why the paragraph above them exists. This catches the descriptor nothing mentions
    ///         at all, which is a different and cheaper mistake.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_declared_descriptor_has_a_raise_site() {
        var compiler = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Vixen.Raven")
        );

        Assert.True(Directory.Exists(compiler), $"The compiler's sources are not at {compiler}.");

        var sources = Directory
            .EnumerateFiles(compiler, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Diagnostics{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText)
            .ToArray();

        Assert.NotEmpty(sources);

        List<string> unraised = [];

        foreach (var owner in new[] {
                     typeof(Vixen.Raven.Diagnostics.SyntaxDiagnostics),
                     typeof(Vixen.Raven.Diagnostics.SemanticDiagnostics),
                     typeof(Vixen.Raven.Diagnostics.LoweringDiagnostics),
                     typeof(Vixen.Raven.Diagnostics.BackendDiagnostics),
                     typeof(Vixen.Raven.Diagnostics.LibraryDiagnostics)
                 }) {
            foreach (var field in owner.GetFields(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                     )) {
                if (field.FieldType != typeof(DiagnosticDescriptor)) {
                    continue;
                }

                var descriptor = (DiagnosticDescriptor)field.GetValue(null)!;

                if (!sources.Any(text => text.Contains($".{field.Name}", StringComparison.Ordinal))) {
                    unraised.Add($"{descriptor.Id} ({owner.Name}.{field.Name})");
                }
            }
        }

        Assert.True(
            unraised.Count == 0,
            "These descriptors are declared and never raised, so nothing can make them fire:\n"
            + string.Join("\n", unraised)
        );

        Assert.DoesNotContain(
            typeof(Vixen.Raven.Diagnostics.SemanticDiagnostics)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.FieldType == typeof(DiagnosticDescriptor))
                .Select(f => ((DiagnosticDescriptor)f.GetValue(null)!).Id),
            id => id == "RVN2012"
        );
    }
}
