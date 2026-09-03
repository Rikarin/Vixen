// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Artefacts;
using Vixen.Raven.CodeGen;
using Vixen.Raven.CodeGen.Glsl;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Symbols;
using Vixen.Raven.Symbols.Metadata;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     <c>.rvnlib</c> — a compiled library: the declarations a consumer binds against and the IR its
///     bodies emit from, referenced without reparsing source.
/// </summary>
/// <remarks>
///     The claim being tested is deliberately narrow and total: a shader compiled against a
///     <c>.rvnlib</c> must produce the same thing as the same shader compiled with the library's
///     source in the compilation. Everything else here — the format's rejections, the pruning, the
///     export checks — exists to keep that claim true.
/// </remarks>
public class CompiledLibraryTests {
    const string MathSource = """
                              package Core

                              struct Ray {
                                  var origin: float3
                                  var direction: float3
                              }

                              struct MathHelpers {
                                  const val Pi = 3.14159f

                                  static func Saturate(x: float): float {
                                      return min(max(x, 0f), 1f)
                                  }

                                  static func At(r: Ray, t: float): float3 {
                                      return r.origin + r.direction * t
                                  }
                              }

                              """;

    /// <summary>Compiles a library's source and builds the artefact, asserting it came out clean.</summary>
    static CompiledLibrary BuildLibrary(string name, string source, params RavenReference[] references) {
        var library = BuildLibraryWithDiagnostics(name, source, out var diagnostics, references);

        Assert.DoesNotContain(diagnostics, d => d.IsError);
        return library;
    }

    static CompiledLibrary BuildLibraryWithDiagnostics(
        string name,
        string source,
        out IReadOnlyList<Diagnostic> diagnostics,
        params RavenReference[] references
    ) {
        var tree = SyntaxTree.ParseText(source, path: name + ".rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create(name, references, [tree]);
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.IsError);

        var bag = new DiagnosticBag();
        var lowered = Lowerer.LowerWithLinks(compilation, bag);
        var library = LibraryBuilder.Build(compilation, lowered, bag);

        diagnostics = bag.ToArray();
        return library;
    }

    /// <summary>Compiles a consumer against references, through the container so the format is exercised.</summary>
    static (Compilation Compilation, IrModule Module, IReadOnlyList<Diagnostic> Diagnostics) Consume(
        string source,
        params CompiledLibrary[] libraries
    ) {
        var references = libraries
            .Select(library => RavenReference.FromLibrary(CompiledLibraryReader.Read(CompiledLibraryWriter.Write(library))))
            .ToArray();

        var tree = SyntaxTree.ParseText(source, path: "Consumer.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Consumer", references, [tree]);
        var semantic = compilation.GetDiagnostics();

        Assert.True(
            !semantic.Any(d => d.IsError),
            "Expected no semantic errors, got:\n" + string.Join("\n", semantic.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);

        return (compilation, module, [.. semantic, .. bag.ToArray()]);
    }

    /// <summary>
    ///     <see cref="Consume" /> without the clean-compile assertion, for the fixtures whose
    ///     subject <em>is</em> a diagnostic out of lowering or the verifier.
    /// </summary>
    /// <remarks>
    ///     Separate rather than a flag on <see cref="Consume" />, so that every other caller keeps
    ///     failing on the first error instead of carrying it silently into an assertion about
    ///     something else. The semantic half is still checked here: a fixture that does not bind is
    ///     not a fixture about lowering.
    /// </remarks>
    static (Compilation Compilation, IrModule Module, IReadOnlyList<Diagnostic> Diagnostics) ConsumeWithoutChecking(
        string source,
        params CompiledLibrary[] libraries
    ) {
        var references = libraries
            .Select(library => RavenReference.FromLibrary(CompiledLibraryReader.Read(CompiledLibraryWriter.Write(library))))
            .ToArray();

        var tree = SyntaxTree.ParseText(source, path: "Consumer.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Consumer", references, [tree]);
        var semantic = compilation.GetDiagnostics();

        Assert.True(
            !semantic.Any(d => d.IsError),
            "Expected no semantic errors, got:\n" + string.Join("\n", semantic.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);

        return (compilation, module, [.. semantic, .. bag.ToArray()]);
    }

    // --- The container ----------------------------------------------------

    /// <summary>
    ///     A library survives the round trip through bytes with its declarations and IR intact.
    /// </summary>
    [Fact]
    public void RoundTripsThroughTheContainer() {
        var written = BuildLibrary("Math", MathSource);
        var read = CompiledLibraryReader.Read(CompiledLibraryWriter.Write(written));

        Assert.Equal(written.Name, read.Name);
        Assert.Equal(written.SourceHash, read.SourceHash);
        Assert.Equal(written.Types.Length, read.Types.Length);
        Assert.Equal(written.Ir.Functions.Length, read.Ir.Functions.Length);

        var ray = Assert.Single(read.Types, t => t.Name == "Ray");
        Assert.Equal("Core", ray.Namespace);
        Assert.Equal("Core.Ray", ray.QualifiedName);
        Assert.Equal(TypeKind.Struct, ray.Kind);
        Assert.Equal(["origin", "direction"], ray.Fields.Select(f => f.Name));

        // The link that makes the artefact more than a type-check.
        var saturate = Assert.Single(read.Types, t => t.Name == "MathHelpers")
            .Methods
            .Single(m => m.Name == "Saturate");

        Assert.NotNull(saturate.IrFunction);

        // By key, which is what the method records and what a call resolves; the function is still
        // called `Saturate`, and the two being different strings is the point.
        var lowered = Assert.Single(read.Ir.Functions, f => f.Key == saturate.IrFunction);
        Assert.Equal("Saturate", lowered.Name);
    }

    /// <summary>
    ///     A folded <c>const</c> travels as text with its type, so a round trip is not lossy.
    /// </summary>
    /// <remarks>
    ///     A boxed value survives <c>System.Text.Json</c> as a <c>JsonElement</c> and stops comparing
    ///     equal to what went in, which is why the artefact spells constants out.
    /// </remarks>
    [Fact]
    public void PreservesConstantValues() {
        var read = CompiledLibraryReader.Read(CompiledLibraryWriter.Write(BuildLibrary("Math", MathSource)));

        var pi = Assert.Single(read.Types, t => t.Name == "MathHelpers").Fields.Single(f => f.Name == "Pi");

        Assert.True(pi.IsConst);
        Assert.Equal(3.14159f, Assert.IsType<float>(pi.DeclaredValue?.ToObject()));
    }

    /// <summary>
    ///     The artefact is inspectable without a bespoke viewer, which is why it is JSON rather than
    ///     a packed binary — a library's bodies are structure, not bytes, so there is nothing to keep
    ///     out of the text and a diffable artefact is worth more than the space.
    /// </summary>
    [Fact]
    public void IsInspectableAsJson() {
        var library = BuildLibrary("Math", MathSource);
        var json = CompiledLibraryWriter.WriteJson(library);

        Assert.Contains("\"Saturate\"", json, StringComparison.Ordinal);
        Assert.Contains("\"op\": \"return\"", json, StringComparison.Ordinal);

        // And it is the same schema the container carries, not a second one that could drift.
        var bytes = CompiledLibraryWriter.Write(library);
        var payload = System.Text.Encoding.UTF8.GetString(bytes.AsSpan(CompiledLibraryFormat.Magic.Length + 8));
        Assert.StartsWith("{", payload, StringComparison.Ordinal);
        Assert.Equal(library.Name, CompiledLibraryReader.Read(bytes).Name);
    }

    /// <summary>
    ///     ⚠ Every enum in the artefact travels as its <em>name</em>, which refutes a claim the
    ///     compiler carried in a doc comment for as long as it had one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>SpecialType.AccelerationStructure</c>'s remark said a <c>.rvnlib</c> carries these
    ///         values as numbers, and that inserting an enum member would therefore silently retype
    ///         every resource in every already-built library. It does not:
    ///         <c>CompiledLibraryFormat.Json</c> registers a <c>JsonStringEnumConverter</c>, so the
    ///         payload spells <c>Texture2D</c>, <c>Sampler</c> and <c>SampleTexture</c> out.
    ///     </para>
    ///     <para>
    ///         Worth an assertion rather than a corrected sentence, because the correction points
    ///         the wrong way round: what these formats actually break on is a <em>rename</em>, which
    ///         the old reasoning would have called free. A test is what makes the next person's
    ///         insertion safe and their rename loud.
    ///     </para>
    /// </remarks>
    [Fact]
    public void CarriesItsEnumsAsNamesRatherThanNumbers() {
        var json = CompiledLibraryWriter.WriteJson(
            BuildLibrary(
                "Resources",
                """
                package Res

                struct Taps {
                    static func Tap(t: Texture2D, s: Sampler, uv: float2): float4 {
                        return t.Sample(s, uv)
                    }
                }

                """
            )
        );

        // The SpecialType of the two parameters, and the IrIntrinsic of the body's one call.
        Assert.Contains("Texture2D", json, StringComparison.Ordinal);
        Assert.Contains("Sampler", json, StringComparison.Ordinal);
        Assert.Contains("SampleTexture", json, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A wrong magic number, an unknown version and a truncation are each reported rather than
    ///     half-read: a partly-loaded library surfaces as a missing member on a type whose source
    ///     nobody has.
    /// </summary>
    [Fact]
    public void RejectsRatherThanHalfReads() {
        var bytes = CompiledLibraryWriter.Write(BuildLibrary("Math", MathSource));

        Assert.Throws<InvalidDataException>(() => CompiledLibraryReader.Read("not a library at all"u8));

        var wrongVersion = bytes.ToArray();
        wrongVersion[CompiledLibraryFormat.Magic.Length] = 99;
        Assert.Throws<InvalidDataException>(() => CompiledLibraryReader.Read(wrongVersion));

        Assert.Throws<InvalidDataException>(() => CompiledLibraryReader.Read(bytes.AsSpan(0, bytes.Length - 32)));

        // A .rvnfx is not a .rvnlib, which is what distinct magic numbers are for.
        Assert.Throws<InvalidDataException>(() => CompiledLibraryReader.Read(CompiledEffectFormat.Magic));
    }

    // --- Binding against a reference --------------------------------------

    /// <summary>
    ///     A library's types resolve by name and its members type-check, with none of its source in
    ///     the compilation.
    /// </summary>
    [Fact]
    public void ReferencedTypesParticipateInBinding() {
        var (compilation, _, _) = Consume(
            """
            package App

            import Core

            shader Lit {
                var amount: float

                [FragmentShader]
                func Shade(): float4 {
                    val clamped = MathHelpers.Saturate(amount)
                    return float4(clamped, clamped, clamped, 1f)
                }
            }

            """,
            BuildLibrary("Math", MathSource)
        );

        var helpers = Assert.Single(compilation.GetReferencedTypes(), t => t.Name == "MathHelpers");
        Assert.IsType<MetadataNamedTypeSymbol>(helpers);

        var saturate = Assert.Single(helpers.GetMembers("Saturate").OfType<MethodSymbol>());
        Assert.Equal(SpecialType.Float, saturate.ReturnType.SpecialType);
        Assert.Equal(SpecialType.Float, Assert.Single(saturate.Parameters).Type.SpecialType);

        // A library type is not one of this compilation's own, which is what keeps lowering from
        // lowering it a second time.
        Assert.DoesNotContain(compilation.GetAllTypes(), t => t.Name == "MathHelpers");
    }

    /// <summary>
    ///     Calling a library function emits a direct call to the library's own lowered body — the
    ///     whole point of shipping the IR alongside the symbols.
    /// </summary>
    [Fact]
    public void CallsIntoALibraryEmitTheLibrarysBody() {
        var (_, module, diagnostics) = Consume(
            """
            package App

            import Core

            shader Lit {
                var amount: float

                [FragmentShader]
                func Shade(): float4 {
                    val clamped = MathHelpers.Saturate(amount)
                    return float4(clamped, clamped, clamped, 1f)
                }
            }

            """,
            BuildLibrary("Math", MathSource)
        );

        Assert.DoesNotContain(diagnostics, d => d.IsError);

        var saturate = Assert.Single(module.AllFunctions, f => f.Name == "Saturate");
        Assert.NotEmpty(saturate.Body.Statements);

        var shade = Assert.Single(module.AllFunctions, f => f.Name == "Shade");
        Assert.Contains(saturate, CallGraph.Calls(shade.Body));

        // And it reaches the target: the backend sees ordinary IR and never learns where the callee
        // came from.
        var bag = new DiagnosticBag();
        var generated = new GlslBackend().Generate(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);
        Assert.Contains("Saturate", Assert.Single(generated).Code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Two libraries that each declare a static of the same name both link, each to its own
    ///     body.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A consumer links every library it references into one module, and a function used to
    ///         cross the boundary under the name it carried there — a name only unique inside the
    ///         module that coined it. Two packages that each declared a <c>static func Of</c>
    ///         therefore offered the same identity, the first loaded took it, and every call to the
    ///         other got the winner's body.
    ///     </para>
    ///     <para>
    ///         Identical signatures deliberately. Mismatched ones make the substitution visible as
    ///         an arity error out of the IR verifier, which is how this surfaced; matching ones
    ///         produce a shader that compiles, validates, and computes the wrong thing — so this is
    ///         the case worth pinning.
    ///     </para>
    /// </remarks>
    [Fact]
    public void SameNamedStaticsInTwoLibrariesEachKeepTheirOwnBody() {
        var geometry = BuildLibrary(
            "Geometry",
            """
            package Geometry

            struct Barycentric {
                static func Of(a: float): float {
                    return sqrt(a)
                }
            }

            """
        );

        var shading = BuildLibrary(
            "Shading",
            """
            package Shading

            struct ShadingAngles {
                static func Of(a: float): float {
                    return abs(a)
                }
            }

            """
        );

        // The claim, at the artefact: the same name, and not the same key.
        var keyOf = (CompiledLibrary library) =>
            Assert.Single(library.Ir.Functions, f => f.Name == "Of").Key;

        Assert.NotEqual(keyOf(geometry), keyOf(shading));

        var (_, module, diagnostics) = Consume(
            """
            package App

            import Geometry
            import Shading

            shader Lit {
                var amount: float

                [FragmentShader]
                func Shade(): float4 {
                    val a = Barycentric.Of(amount)
                    val b = ShadingAngles.Of(amount)
                    return float4(a, b, 0f, 1f)
                }
            }

            """,
            geometry,
            shading
        );

        Assert.DoesNotContain(diagnostics, d => d.IsError);

        var shade = Assert.Single(module.AllFunctions, f => f.Name == "Shade");
        var called = CallGraph.Calls(shade.Body).ToArray();

        // Two callees rather than one reached twice, and each holds the body its own package wrote.
        Assert.Equal(2, called.Length);
        Assert.NotSame(called[0], called[1]);
        Assert.Equal(IrIntrinsic.Sqrt, OnlyIntrinsic(called[0]));
        Assert.Equal(IrIntrinsic.Abs, OnlyIntrinsic(called[1]));
    }

    /// <summary>
    ///     ⚠ Two libraries that each declare a <em>struct</em> of the same name collapse to the
    ///     first, and this pins the defect rather than the fix.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The other half of <see cref="SameNamedStaticsInTwoLibrariesEachKeepTheirOwnBody" />,
    ///         and the half still open. A function crosses a library boundary by its artefact
    ///         <em>key</em>, derived from the declaration; a struct crosses by its bare IR name. So
    ///         two libraries' <c>Shape</c>s are one object with the first library's fields, and the
    ///         second library's function returns a value whose members belong to somebody else.
    ///         <c>LibraryIrCodec</c>'s decoder says so in its own remarks — "the cost is that two
    ///         libraries exporting the same IR name collapse to the first" — and nothing executed
    ///         that sentence until now.
    ///     </para>
    ///     <para>
    ///         <b>Two fixtures, because the failure has two severities and only one is
    ///         survivable.</b> Different field counts reach the IR verifier as <c>RVN3010</c>,
    ///         which is a refusal an author can read. Equal counts do not — see
    ///         <see cref="SameNamedStructsOfEqualWidthCollapseWithNoDiagnosticAtAll" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Why this is pinned rather than fixed, which is the part worth writing down.</b>
    ///         Sharing structs by name is not simply wrong, and a fix that qualifies every struct
    ///         name breaks two things that depend on it. A <em>tuple</em>'s name is derived from its
    ///         element types, so the name <em>is</em> its structural identity and two libraries'
    ///         <c>Tuple_f32_f32</c> have to stay one type — <c>Lowerer.LowerTuple</c> says in its
    ///         own remarks that this is necessary and not an optimisation, because a library
    ///         function returning <c>(float, float)</c> would otherwise return a different type from
    ///         the one the caller's local holds. A monomorphised generic is named the same way. So
    ///         the fix is a key beside the name, as functions already have, with structural identity
    ///         exempted and the decoder routing a reference to the library it came from — which is
    ///         more than one method. Filed as
    ///         <a href="https://github.com/Rikarin/Vixen/issues/504">#504</a>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void SameNamedStructsInTwoLibrariesCollapseToTheFirst() {
        var geometry = BuildLibrary(
            "Geometry",
            """
            package Geometry

            struct Shape {
                var radius: float
            }

            struct Shapes {
                static func Make(a: float): Shape {
                    var s: Shape
                    s.radius = a
                    return s
                }
            }

            """
        );

        var physics = BuildLibrary(
            "Physics",
            """
            package Physics

            struct Shape {
                var mass: float
                var drag: float
            }

            struct Bodies {
                static func Make(a: float): Shape {
                    var s: Shape
                    s.mass = a
                    s.drag = a * 2f
                    return s
                }
            }

            """
        );

        // ⚠ Loud, and loud is the good case: Physics.Shape became Geometry.Shape, which has one
        // field, so writing the second is an access the verifier refuses.
        var (_, module, diagnostics) = ConsumeWithoutChecking(
            """
            package App

            import Geometry
            import Physics

            shader Lit {
                var amount: float

                [FragmentShader]
                func Shade(): float4 {
                    val a = Shapes.Make(amount)
                    val b = Bodies.Make(amount)
                    return float4(a.radius, b.mass, 0f, 1f)
                }
            }

            """,
            geometry,
            physics
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN3010");

        // And the collapse itself, so the assertion above cannot be satisfied by some other error:
        // one struct object reached twice, carrying the first library's fields.
        var shade = Assert.Single(module.AllFunctions, f => f.Name == "Shade");
        var made = CallGraph.Calls(shade.Body).ToArray();

        Assert.Equal(2, made.Length);
        Assert.Same(made[0].ReturnType, made[1].ReturnType);
        Assert.Equal(["radius"], Assert.IsType<IrStructType>(made[0].ReturnType).Fields.Select(f => f.Name));
    }

    /// <summary>
    ///     ⚠ The silent half of <see cref="SameNamedStructsInTwoLibrariesCollapseToTheFirst" />:
    ///     equal field counts, so nothing complains and the shader reads the wrong member.
    /// </summary>
    /// <remarks>
    ///     This is the case that makes the collapse worth fixing rather than documenting. There is
    ///     no diagnostic, no verifier complaint and no invalid module — <c>b.height</c> lowers to a
    ///     read of <c>Shape.drag</c>, and the only symptom is a number.
    /// </remarks>
    [Fact]
    public void SameNamedStructsOfEqualWidthCollapseWithNoDiagnosticAtAll() {
        var physics = BuildLibrary(
            "Physics",
            """
            package Physics

            struct Shape {
                var mass: float
                var drag: float
            }

            struct Bodies {
                static func Make(a: float): Shape {
                    var s: Shape
                    s.mass = a
                    s.drag = a * 2f
                    return s
                }
            }

            """
        );

        var terrain = BuildLibrary(
            "Terrain",
            """
            package Terrain

            struct Shape {
                var width: float
                var height: float
            }

            struct Plots {
                static func Make(a: float): Shape {
                    var s: Shape
                    s.width = a
                    s.height = a * 3f
                    return s
                }
            }

            """
        );

        var (_, module, diagnostics) = Consume(
            """
            package App

            import Physics
            import Terrain

            shader Lit {
                var amount: float

                [FragmentShader]
                func Shade(): float4 {
                    val a = Bodies.Make(amount)
                    val b = Plots.Make(amount)
                    return float4(a.mass, b.width, b.height, 1f)
                }
            }

            """,
            physics,
            terrain
        );

        // Nothing at all, which is the finding.
        Assert.DoesNotContain(diagnostics, d => d.IsError);

        var shade = Assert.Single(module.AllFunctions, f => f.Name == "Shade");
        var made = CallGraph.Calls(shade.Body).ToArray();

        Assert.Equal(2, made.Length);
        Assert.Same(made[0].ReturnType, made[1].ReturnType);

        // `Plots.Make` returns a Shape whose fields are Physics's, so `b.height` is `drag`.
        Assert.Equal(
            ["mass", "drag"],
            Assert.IsType<IrStructType>(made[1].ReturnType).Fields.Select(f => f.Name)
        );
    }

    static IrIntrinsic OnlyIntrinsic(IrFunction function) =>
        Assert.Single(function.Body.Statements.OfType<IrIntrinsicInstruction>()).Intrinsic;

    /// <summary>
    ///     A library struct keeps one identity across the link, so a value of it flows between the
    ///     consumer's code and the library's.
    /// </summary>
    [Fact]
    public void LibraryStructsKeepOneIdentity() {
        var (_, module, diagnostics) = Consume(
            """
            package App

            import Core

            shader Lit {
                var t: float

                [FragmentShader]
                func Shade(): float4 {
                    val r = Ray(float3(0f, 0f, 0f), float3(0f, 1f, 0f))
                    val p = MathHelpers.At(r, t)
                    return float4(p.x, p.y, p.z, 1f)
                }
            }

            """,
            BuildLibrary("Math", MathSource)
        );

        // An identity mismatch would be a verifier type error rather than a missing symbol, which
        // is exactly what a per-library struct table would have produced.
        Assert.DoesNotContain(diagnostics, d => d.IsError);

        var ray = Assert.Single(module.Structs, s => s.Name == "Ray");
        Assert.Equal(["origin", "direction"], ray.Fields.Select(f => f.Name));

        var at = Assert.Single(module.AllFunctions, f => f.Name == "At");
        Assert.Same(ray, Assert.Single(at.Parameters, p => p.Type is IrStructType).Type);
    }

    /// <summary>
    ///     A referenced library is not a tax on the output: what nothing reaches is dropped.
    /// </summary>
    /// <remarks>
    ///     Not the same pass as the backends' per-entry-point reachability walk. This one decides
    ///     what the <em>module</em> holds, so the IR dump, the verifier and <c>IrCapabilities</c>
    ///     describe the shader that was compiled rather than the library it borrowed one function
    ///     from.
    /// </remarks>
    [Fact]
    public void PrunesWhatNothingReached() {
        var (_, module, _) = Consume(
            """
            package App

            import Core

            shader Lit {
                var amount: float

                [FragmentShader]
                func Shade(): float4 {
                    val clamped = MathHelpers.Saturate(amount)
                    return float4(clamped, clamped, clamped, 1f)
                }
            }

            """,
            BuildLibrary("Math", MathSource)
        );

        Assert.Contains(module.AllFunctions, f => f.Name == "Saturate");
        Assert.DoesNotContain(module.AllFunctions, f => f.Name == "At");
        Assert.DoesNotContain(module.Structs, s => s.Name == "Ray");
    }

    /// <summary>
    ///     A library function linked from an artefact lowers to exactly what compiling its source
    ///     alongside the consumer produces, instruction for instruction. This is the claim the whole
    ///     phase rests on.
    /// </summary>
    /// <remarks>
    ///     Compared per function rather than as whole emitted units, and deliberately. The two
    ///     modules are <em>not</em> byte-identical, because the referenced one is smaller: the
    ///     library's unreached declarations are pruned out of it, while a compilation that has the
    ///     source keeps everything it declared. Comparing the units would test that difference
    ///     instead of the round trip.
    /// </remarks>
    [Fact]
    public void ALinkedFunctionLowersToWhatItsSourceDid() {
        const string consumer = """
                                package Core

                                shader Lit {
                                    var t: float

                                    [FragmentShader]
                                    func Shade(): float4 {
                                        val r = Ray(float3(0f, 0f, 0f), float3(0f, 1f, 0f))
                                        val p = MathHelpers.At(r, t)
                                        return float4(p.x, p.y, p.z, 1f)
                                    }
                                }

                                """;

        var (_, referenced, _) = Consume(consumer, BuildLibrary("Math", MathSource));

        var together = Compilation.Create(
            "Consumer",
            SyntaxTree.ParseText(MathSource, path: "Math.rvn"),
            SyntaxTree.ParseText(consumer, path: "Consumer.rvn")
        );

        Assert.DoesNotContain(together.GetDiagnostics(), d => d.IsError);

        var bag = new DiagnosticBag();
        var direct = Lowerer.Lower(together, bag);
        IrVerifier.Verify(direct, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        // The library's function, and the consumer's own that calls it.
        Assert.Equal(PrintFunction(direct, "At"), PrintFunction(referenced, "At"));
        Assert.Equal(PrintFunction(direct, "Shade"), PrintFunction(referenced, "Shade"));

        // And the shape a value crosses the boundary as.
        Assert.Equal(
            Assert.Single(direct.Structs, s => s.Name == "Ray").Fields.Select(f => f.Type.Name),
            Assert.Single(referenced.Structs, s => s.Name == "Ray").Fields.Select(f => f.Type.Name)
        );
    }

    static string PrintFunction(IrModule module, string name) =>
        IrPrinter.Print(Assert.Single(module.AllFunctions, f => f.Name == name));

    static string Generate(IrModule module) {
        var bag = new DiagnosticBag();
        var generated = new GlslBackend().Generate(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);
        return string.Join("\n", generated.Select(unit => unit.Code));
    }

    /// <summary>
    ///     Every statement and access shape the IR has, pushed through the artefact and compared
    ///     against what lowering the same source directly produces.
    /// </summary>
    /// <remarks>
    ///     The encoder and decoder are two halves of one mapping, and a case added to one but not the
    ///     other loses information silently — a dropped <c>continue</c>, a swizzle read back as a
    ///     field. This is the test that would catch it: loops both ways, a conditional with an else,
    ///     <c>break</c> and <c>continue</c>, a matrix column, a swizzle, an index chain, a struct
    ///     built and read back, a tuple, and a select.
    /// </remarks>
    [Fact]
    public void RoundTripsEveryStatementShape() {
        const string source = """
                              package Core

                              struct Accumulator {
                                  var total: float
                                  var count: int
                              }

                              struct Shapes {
                                  static func Loops(n: int, weights: float[16]): float {
                                      var acc = Accumulator(0f, 0)

                                      for (i in 0 .. n) {
                                          if (i == 2) {
                                              continue
                                          }

                                          if (i > 8) {
                                              break
                                          }

                                          acc.total += weights[i]
                                          acc.count += 1
                                      }

                                      var guard = 0
                                      repeat {
                                          guard += 1
                                      } while (guard < 3)

                                      return acc.count > 0 ? acc.total / float(acc.count) : 0f
                                  }

                                  static func Vectors(m: mat3, v: float3): float3 {
                                      val column = m[1]
                                      val mixed = float3(v.zyx.x, column.y, v.y)
                                      return normalize(mixed) * length(column)
                                  }

                                  static func Pair(x: float): (lo: float, hi: float) {
                                      return (x - 1f, x + 1f)
                                  }
                              }

                              """;

        const string consumer = """
                                package Core

                                shader Lit {
                                    var n: int
                                    var weights: float[16]
                                    var m: mat3
                                    var v: float3

                                    [FragmentShader]
                                    func Shade(): float4 {
                                        val a = Shapes.Loops(n, weights)
                                        val b = Shapes.Vectors(m, v)
                                        val p = Shapes.Pair(a)
                                        return float4(b.x, b.y, p.lo, p.hi)
                                    }
                                }

                                """;

        var (_, referenced, referencedDiagnostics) = Consume(consumer, BuildLibrary("Shapes", source));
        Assert.DoesNotContain(referencedDiagnostics, d => d.IsError);

        var together = Compilation.Create(
            "Consumer",
            SyntaxTree.ParseText(source, path: "Shapes.rvn"),
            SyntaxTree.ParseText(consumer, path: "Consumer.rvn")
        );

        Assert.DoesNotContain(together.GetDiagnostics(), d => d.IsError);

        var bag = new DiagnosticBag();
        var direct = Lowerer.Lower(together, bag);
        IrVerifier.Verify(direct, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        foreach (var name in (string[])["Loops", "Vectors", "Pair", "Shade"]) {
            Assert.Equal(PrintFunction(direct, name), PrintFunction(referenced, name));
        }
    }

    // --- Composition across the boundary ----------------------------------

    /// <summary>
    ///     A <c>compose</c> slot resolves to a shader that ships in a library. A material feature is
    ///     exactly the kind of thing <c>Raven/Library</c> holds, so this is the ordinary case.
    /// </summary>
    [Fact]
    public void ComposeResolvesToALibraryShader() {
        var features = BuildLibrary(
            "Features",
            """
            package Shading

            protocol IDiffuseModel {
                func Diffuse(albedo: float4): float4
            }

            shader Lambert : IDiffuseModel {
                func Diffuse(albedo: float4): float4 {
                    return albedo * 0.5f
                }
            }

            """
        );

        var reference = RavenReference.FromLibrary(
            CompiledLibraryReader.Read(CompiledLibraryWriter.Write(features))
        );

        var tree = SyntaxTree.ParseText(
            """
            package App

            import Shading

            shader Lit {
                compose val diffuse: IDiffuseModel

                var tint: float4

                [FragmentShader]
                func Shade(): float4 {
                    return diffuse.Diffuse(tint)
                }
            }

            """,
            path: "Consumer.rvn"
        );

        var compilation = Compilation.Create(
            "Consumer",
            PermutationValues.Empty,
            ComposeBindings.Create([new("diffuse", "Lambert")]),
            [reference],
            [tree]
        );

        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.IsError);

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        // Static resolution, so the emitted unit holds a direct call and no dispatch.
        Assert.Contains("Diffuse", Generate(module), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A library built against another library works: the dependency travels as a name and the
    ///     consumer resolves it against its own references.
    /// </summary>
    [Fact]
    public void LibrariesCanBuildOnLibraries() {
        var math = BuildLibrary("Math", MathSource);
        var mathReference = RavenReference.FromLibrary(
            CompiledLibraryReader.Read(CompiledLibraryWriter.Write(math))
        );

        var shading = BuildLibrary(
            "Shading",
            """
            package Shading

            import Core

            struct Brdf {
                static func Diffuse(nDotL: float): float {
                    return MathHelpers.Saturate(nDotL) * MathHelpers.Pi
                }
            }

            """,
            mathReference
        );

        var (_, module, diagnostics) = Consume(
            """
            package App

            import Shading

            shader Lit {
                var nDotL: float

                [FragmentShader]
                func Shade(): float4 {
                    val d = Brdf.Diffuse(nDotL)
                    return float4(d, d, d, 1f)
                }
            }

            """,
            math,
            shading
        );

        Assert.DoesNotContain(diagnostics, d => d.IsError);

        // Shading's own body, and Math's, both linked in — the call across the two artefacts
        // resolved to one function rather than to a private copy.
        var diffuse = Assert.Single(module.AllFunctions, f => f.Name == "Diffuse");
        var saturate = Assert.Single(module.AllFunctions, f => f.Name == "Saturate");
        Assert.Contains(saturate, CallGraph.Calls(diffuse.Body));
    }

    /// <summary>
    ///     A library that names a type from a library the consumer did not reference says so, rather
    ///     than presenting a member that mysteriously cannot be found.
    /// </summary>
    /// <remarks>
    ///     Reported where the type is used, not when the library is loaded. Resolution inside an
    ///     artefact is lazy — the same laziness the source symbols have — so a member nobody touches
    ///     costs nothing and reports nothing. A missing reference that never mattered is not worth an
    ///     error; the one this shader depends on is.
    /// </remarks>
    [Fact]
    public void ReportsAMissingTransitiveReference() {
        var mathReference = RavenReference.FromLibrary(BuildLibrary("Math", MathSource));

        var shading = BuildLibrary(
            "Shading",
            """
            package Shading

            import Core

            struct Trace {
                static func Origin(r: Ray): float3 {
                    return r.origin
                }
            }

            """,
            mathReference
        );

        var tree = SyntaxTree.ParseText(
            """
            package App

            import Shading

            shader Lit {
                [FragmentShader]
                func Shade(): float4 {
                    val o = Trace.Origin(Ray(float3(0f, 0f, 0f), float3(0f, 1f, 0f)))
                    return float4(o.x, o.y, o.z, 1f)
                }
            }

            """,
            path: "Consumer.rvn"
        );

        // Shading alone: its Origin takes a Core.Ray, which nothing here supplies.
        var compilation = Compilation.Create("Consumer", [RavenReference.FromLibrary(shading)], [tree]);

        Assert.Contains(compilation.GetDiagnostics(), d => d.Id == "RVN5004");
    }

    // --- The export checks ------------------------------------------------

    /// <summary>
    ///     A body that reads a shader binding is refused at write time, where it can be fixed, rather
    ///     than exported to fail in every consumer.
    /// </summary>
    /// <remarks>
    ///     A binding belongs to the shader that declares it — its <c>(set, binding)</c> pair is
    ///     assigned per effect — so linking the function that reads it into another shader would name
    ///     storage that shader never declared. That was a silent GLSL miscompilation before.
    /// </remarks>
    [Fact]
    public void RefusesToExportABodyThatReadsABinding() {
        var library = BuildLibraryWithDiagnostics(
            "Leaky",
            """
            package Leaky

            shader Fog {
                var density: float

                func Direct(): float {
                    return density
                }

                func Indirect(): float {
                    return Direct() * 2f
                }
            }

            """,
            out var diagnostics
        );

        var refused = diagnostics.Where(d => d.Id == "RVN5001").ToArray();
        Assert.Equal(2, refused.Length);
        Assert.All(refused, d => Assert.Contains("density", d.GetMessage(), StringComparison.Ordinal));

        // Refused means not exported: the artefact records the declaration, with no body to link.
        var fog = Assert.Single(library.Types, t => t.Name == "Fog");
        Assert.All(fog.Methods, method => Assert.Null(method.IrFunction));
        Assert.Empty(library.Ir.Functions);
    }

    /// <summary>
    ///     A permutation key read while building a library has its value baked into the exported
    ///     bodies, and that is said — the symptom otherwise is a consumer's <c>--define</c> that
    ///     appears to be ignored.
    /// </summary>
    /// <remarks>
    ///     The one thing an artefact cannot carry, and it follows from what makes permutations work:
    ///     a key is resolved at compile time so the dead branch disappears, which means the branch is
    ///     already gone by the time the body is written down.
    /// </remarks>
    [Fact]
    public void WarnsThatAPermutationIsBakedIn() {
        BuildLibraryWithDiagnostics(
            "Baked",
            """
            package Baked

            shader Feature {
                [Permutation] val UseDetail: bool = false

                func Detail(x: float): float {
                    return UseDetail ? x * 2f : x
                }
            }

            """,
            out var diagnostics
        );

        var warning = Assert.Single(diagnostics, d => d.Id == "RVN5006");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("UseDetail", warning.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A module with a library linked into it validates as SPIR-V, which is the only signal a
    ///     binary target gives: a listing can read perfectly and still be a module no driver loads.
    /// </summary>
    [Fact]
    public void ALinkedModuleValidatesAsSpirv() {
        Assert.SkipUnless(SpirvTestBase.ValidatorAvailable, "spirv-val is not on PATH (brew install spirv-tools).");

        var (_, module, _) = Consume(
            """
            package App

            import Core

            shader Lit {
                var t: float

                [FragmentShader]
                func Shade(): float4 {
                    val r = Ray(float3(0f, 0f, 0f), float3(0f, 1f, 0f))
                    val p = MathHelpers.At(r, t)
                    return float4(p.x, p.y, MathHelpers.Saturate(t), 1f)
                }
            }

            """,
            BuildLibrary("Math", MathSource)
        );

        var bag = new DiagnosticBag();
        var generated = new Vixen.Raven.CodeGen.Spirv.SpirvBackend().Generate(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        SpirvTestBase.Validate(Assert.Single(generated));
    }

    /// <summary>
    ///     A stage entry point is not part of what a library supplies, and that is said rather than
    ///     silently dropped.
    /// </summary>
    [Fact]
    public void SaysThatAnEntryPointIsNotExported() {
        BuildLibraryWithDiagnostics(
            "Staged",
            """
            package Staged

            shader Blit {
                [FragmentShader]
                func Shade(): float4 {
                    return float4(1f, 1f, 1f, 1f)
                }
            }

            """,
            out var diagnostics
        );

        var reported = Assert.Single(diagnostics, d => d.Id == "RVN5002");
        Assert.Equal(DiagnosticSeverity.Info, reported.Severity);
        Assert.Contains("Blit.Shade", reported.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A source declaration shadows a referenced type of the same name — source wins, as it does
    ///     everywhere else — and the shadowing is reported, because otherwise a shader binds against
    ///     the definition its author was not reading.
    /// </summary>
    [Fact]
    public void ReportsASourceDeclarationShadowingAReference() {
        var reference = RavenReference.FromLibrary(BuildLibrary("Math", MathSource));

        var tree = SyntaxTree.ParseText(
            """
            package Core

            struct Ray {
                var origin: float3
            }

            shader Lit {
                [FragmentShader]
                func Shade(): float4 {
                    val r = Ray(float3(0f, 0f, 0f))
                    return float4(r.origin.x, 0f, 0f, 1f)
                }
            }

            """,
            path: "Consumer.rvn"
        );

        var compilation = Compilation.Create("Consumer", [reference], [tree]);
        var diagnostics = compilation.GetDiagnostics();

        var shadowed = Assert.Single(diagnostics, d => d.Id == "RVN5003");
        Assert.Contains("Core.Ray", shadowed.GetMessage(), StringComparison.Ordinal);

        // Shadowing, not ambiguity: exactly one Ray is in scope, and it is the source one, which
        // takes a single field.
        Assert.DoesNotContain(diagnostics, d => d.IsError);
        Assert.IsType<Vixen.Raven.Symbols.Source.SourceNamedTypeSymbol>(
            Assert.Single(compilation.GetAllTypes(), t => t.Name == "Ray")
        );
    }

    /// <summary>The same library referenced twice is one reference, and the duplicate is named.</summary>
    [Fact]
    public void ReportsADuplicateReference() {
        var library = BuildLibrary("Math", MathSource);

        var compilation = Compilation.Create(
            "Consumer",
            [RavenReference.FromLibrary(library), RavenReference.FromLibrary(library)],
            [SyntaxTree.ParseText("package App\n", path: "Consumer.rvn")]
        );

        Assert.Single(compilation.GetDiagnostics(), d => d.Id == "RVN5005");
        Assert.Single(compilation.GetReferencedTypes(), t => t.Name == "Ray");
    }
}
