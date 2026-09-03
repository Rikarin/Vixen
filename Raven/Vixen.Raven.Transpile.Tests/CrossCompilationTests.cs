// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Tests;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;

namespace Vixen.Raven.Transpile.Tests;

/// <summary>
///     The cross-compilation pass: every shader the engine ships, through SPIRV-Cross, into a real
///     GLSL ES front end.
/// </summary>
/// <remarks>
///     <para>
///         <b>What this suite is for.</b> Not that the transpiler runs — that it produces something
///         a GLES driver will accept. Those are different claims, and the second is the only one
///         worth anything: SPIRV-Cross returns a string for almost any module, and a string that
///         fails at <c>glCompileShader</c> on a phone is indistinguishable at build time from one
///         that does not.
///     </para>
///     <para>
///         So the oracle is <c>glslangValidator</c> reading <c>#version 300 es</c>, and
///         <see cref="The_oracle_is_installed_so_this_file_means_something" /> is a hard failure
///         rather than a skip. ⚠ That is not defensive style, it is this repository's own history:
///         the SPIR-V differential oracle was green on two CI legs that had never installed
///         shaderc, for months.
///     </para>
/// </remarks>
public class CrossCompilationTests {
    /// <summary>
    ///     The oracle is installed, so everything else in this file means something.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the instrument check, and it is the first test for a reason.</b> Every other
    ///     case here calls <c>EsslOracle.Validate</c>, which asserts the tool is present — so
    ///     without this one, a machine with no glslang would fail nine tests with nine confusing
    ///     messages, and a machine where the tool was found but did nothing would pass. This one says
    ///     which it is, in one line.
    /// </remarks>
    [Fact]
    public void The_oracle_is_installed_so_this_file_means_something() =>
        Assert.True(EsslOracle.Validator is not null, EsslOracle.HowToInstall);

    /// <summary>
    ///     ⚠ Raven's own GLSL is Vulkan GLSL, and a GLSL ES front end rejects it three ways over.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the test that says why the project exists</b>, and it is written as a
    ///         refutation rather than as a demonstration: it takes the <em>same shader</em> that the
    ///         case below cross-compiles successfully, hands the ES front end Raven's own GLSL for
    ///         it, and requires that to fail. If it ever passes, SPIRV-Cross has stopped earning
    ///         its keep and this whole project should be deleted rather than maintained.
    ///     </para>
    ///     <para>
    ///         ⚠ It also pins a claim that was <em>wrong</em> when this work started: the GLES head
    ///         was believed to already have shaders, because
    ///         <c>Platform/Vixen.Graphics.OpenGL/GlslTranslator.cs</c> rewrites Raven's GLSL at
    ///         program-load time. It does — but it is a regex over <em>declaration qualifiers</em>,
    ///         and none of the three errors below is a qualifier. A separate texture and sampler is
    ///         a syntax error in every GL profile; a <c>precision</c> line it does not add cannot be
    ///         inferred; and <c>layout(std140, set = 2, binding = 0)</c> does not even match its
    ///         pattern, so the <c>set =</c> survives.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Ravens_own_vulkan_glsl_is_what_a_gles_front_end_rejects() {
        var glsl = Assert.Single(Generate(Lambert, "glsl"), unit => unit.Stage == ShaderStage.Fragment);

        // The version line is the only thing GlslTranslator would certainly have replaced, so
        // replacing it here is the most generous possible reading of the existing path — everything
        // that fails below fails on the language rather than on the directive.
        var asEs = Regex.Replace(glsl.Code, @"^\s*#version[^\n]*\n", "#version 300 es\n");

        var (accepted, log) = EsslOracle.Validate(asEs, ShaderStage.Fragment);

        Assert.False(
            accepted,
            "Raven's Vulkan GLSL now compiles as GLSL ES 3.00. If that is genuinely true, "
            + "Vixen.Raven.Transpile is redundant and should be deleted rather than kept.\n\n"
            + asEs
        );

        // ⚠ The three reasons are asserted on the ARTEFACT rather than on glslang's message,
        // because a front end stops at the first error and which one that is depends on
        // declaration order. Asserting the message would make this test a claim about
        // glslang's error ordering; asserting the source makes it a claim about Raven's output,
        // which is the one being made.
        Assert.Contains("uniform texture2D", glsl.Code, StringComparison.Ordinal);
        Assert.Contains("uniform sampler ", glsl.Code, StringComparison.Ordinal);
        Assert.Contains("set = ", glsl.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("precision ", glsl.Code, StringComparison.Ordinal);

        Assert.False(string.IsNullOrWhiteSpace(log), "The ES front end refused it and said nothing.");
    }

    /// <summary>
    ///     And the same shader, cross-compiled, is accepted.
    /// </summary>
    /// <remarks>
    ///     The other half of the pair above. Together they are a differential rather than a
    ///     smoke test: one input, two paths, and the assertion is that they disagree.
    /// </remarks>
    [Fact]
    public void The_same_shader_cross_compiled_is_accepted() {
        foreach (var unit in Generate(Lambert, EsslBackend.TargetName)) {
            var (accepted, log) = EsslOracle.Validate(unit.Code, unit.Stage);

            Assert.True(accepted, $"{unit.Name} was refused by the ES front end:\n{log}\n\n{unit.Code}");
        }
    }

    /// <summary>
    ///     Every raster entry point in <c>Raven/Library</c> cross-compiles, and the ES front end
    ///     takes all of them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The whole library rather than a fixture</b>, because a fixture proves the tool is
    ///         wired and the library proves the engine's shaders can reach a phone. They are
    ///         different questions and the second is the one #63 asks.
    ///     </para>
    ///     <para>
    ///         ⚠ Failures are collected and reported together. Reporting the first would make this a
    ///         one-shader-per-run instrument on the day something broad breaks, which is exactly the
    ///         day the list is what you want.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_raster_entry_point_in_the_library_survives_the_es_front_end() {
        var bag = new DiagnosticBag();
        var units = Backend(GlslDialect.Essl300).Generate(Library(), bag);

        // A floor rather than an exact number: the library grows, and a test that had to be edited
        // for every new shader would be edited without being read. Zero units with a green result
        // is the failure this guards — an empty sweep is the shape an instrument takes on the day
        // it stops running.
        Assert.True(units.Count > 20, $"Only {units.Count} entry points cross-compiled; the sweep is not running.");

        List<string> refused = [];

        foreach (var unit in units) {
            var (accepted, log) = EsslOracle.Validate(unit.Code, unit.Stage);

            if (!accepted) {
                refused.Add($"--- {unit.Name} ({unit.Stage})\n{log}");
            }
        }

        Assert.True(
            refused.Count == 0,
            $"{refused.Count} of {units.Count} cross-compiled units were refused by GLSL ES 3.00:\n"
            + string.Join("\n", refused)
        );
    }

    /// <summary>
    ///     At ES 3.20 the only thing in the library that cannot be expressed is the one shader
    ///     needing a feature GLSL ES has at no version.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the claim worth making about the whole library</b>, and it is a stronger
    ///         one than the ES 3.00 sweep above: not "the shaders ES 3.0 can express are correct",
    ///         but "every shader the engine ships reaches GLES except the ones a named feature keeps
    ///         out, and the compiler says which".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>There is exactly one such shader today and it was a surprise:</b>
    ///         <c>ClusterSoftwareRaster</c> needs <c>Int64</c>, for the 64-bit atomic min that makes
    ///         its depth-and-payload word one compare. GLSL ES has no 64-bit integer at any version,
    ///         so software rasterisation is a thing GLES does not get — which is a fact about the
    ///         profile rather than a gap in this project, and is the kind of thing that is far
    ///         better found by a sweep than by a bug report from a phone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>RVN4002</c> at any dialect</b>, which is the assertion that actually
    ///         caught something: SPIRV-Cross refuses a whole module carrying a <c>texelFetch</c> on
    ///         an unsampled image unless a dummy sampler is built for it first, and three shipped
    ///         compute shaders take that path. Every fixture in this file passed without that call.
    ///     </para>
    ///     <para>
    ///         ⚠ The refusal count at ES 3.00 is asserted to be <em>non-zero</em> too. A gate that
    ///         refuses nothing is indistinguishable from a gate that is not running, and this library
    ///         genuinely contains compute shaders, storage buffers and a bindless texture array — so
    ///         zero refusals means <c>EsslBackend.Refuses</c> has stopped being asked.
    ///     </para>
    /// </remarks>
    [Fact]
    public void At_es_320_only_a_feature_gles_lacks_entirely_keeps_a_library_shader_out() {
        var atEs300 = new DiagnosticBag();
        var raster = Backend(GlslDialect.Essl300).Generate(Library(), atEs300);

        Assert.True(
            atEs300.ToArray().Any(d => d.Id == "RVN4001"),
            "ES 3.00 refused nothing, which cannot be right: the library has compute shaders, "
            + "storage buffers and a bindless texture array in it. The dialect gate is not running."
        );

        var everything = new DiagnosticBag();
        var all = Backend(GlslDialect.Essl320).Generate(Library(), everything);

        // A transpile that failed rather than a feature that is absent. This one has no acceptable
        // instances: every RVN4002 here is a module SPIRV-Cross could not take, which is a defect in
        // this project rather than a limit of the profile.
        Assert.DoesNotContain(everything.ToArray(), d => d.Id == "RVN4002");

        // The two things GLSL ES has at no version, named rather than counted — a third appearing
        // here should be read and understood rather than added to this list reflexively.
        Assert.All(
            everything.ToArray().Where(d => d.Id == "RVN4001"),
            refusal => Assert.True(
                refusal.ToString().Contains("Int64", StringComparison.Ordinal)
                || refusal.ToString().Contains("bindless", StringComparison.Ordinal),
                $"ES 3.20 refused something for a new reason, which is worth reading: {refusal}"
            )
        );

        Assert.True(
            all.Count > raster.Count,
            $"ES 3.20 emitted {all.Count} units and ES 3.00 emitted {raster.Count}; the higher "
            + "profile must reach at least the shaders the lower one refused."
        );

        List<string> unexpected = [];
        List<string> nowPassing = [];

        foreach (var unit in all) {
            var (accepted, log) = EsslOracle.Validate(unit.Code, unit.Stage);
            var owed = OwedAtEs320.ContainsKey(unit.Name);

            if (!accepted && !owed) {
                unexpected.Add($"--- {unit.Name} ({unit.Stage})\n{log}");
            }

            if (accepted && owed) {
                nowPassing.Add(unit.Name);
            }
        }

        Assert.True(
            unexpected.Count == 0 && nowPassing.Count == 0,
            $"{unexpected.Count} of {all.Count} units were refused by GLSL ES 3.20 and are not on "
            + $"the owed list:\n{string.Join("\n", unexpected)}\n\n"
            + $"And these are on it but now pass, so delete their lines: {string.Join(", ", nowPassing)}"
        );
    }

    /// <summary>
    ///     The units GLSL ES 3.20 still refuses, each with the reason, and the list only shrinks.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the honest boundary of this work, written down where it fails rather
    ///         than in a report.</b> Every raster entry point in the library cross-compiles and is
    ///         accepted; four compute ones are not, for two reasons, and both are fixable in
    ///         <c>Vixen.Raven</c> rather than here.
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 <b>An unqualified storage image.</b> GLSL ES only lets an image be both read
    ///                 and written when its format is one of the 32-bit-per-channel single-component
    ///                 ones; anything else must be <c>readonly</c> or <c>writeonly</c>. SPIRV-Cross
    ///                 writes those qualifiers from SPIR-V's <c>NonReadable</c> / <c>NonWritable</c>
    ///                 decorations — and Raven's SPIR-V backend emits neither, so an image these
    ///                 shaders only ever store into looks read-write to the translator. The fix is a
    ///                 decoration in the emitter, which changes committed <c>.spv</c> bytes and so
    ///                 belongs in its own change with <c>CheckShaders</c> run against it.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>A name used twice.</b> <c>AutoExposure</c> has a <c>groupshared</c>
    ///                 variable and a function-scope one that SPIRV-Cross emits under one name; GLSL
    ///                 ES's scoping rules make that a redefinition where Vulkan GLSL's do not.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠ Held in <em>both</em> directions: a new refusal fails, and so does a listed one that
    ///         starts passing. A list that only grows would let this rot into a mute button, which is
    ///         what <c>docs/DocsExempt.txt</c>'s header says about the same shape.
    ///     </para>
    /// </remarks>
    static readonly Dictionary<string, string> OwedAtEs320 = new(StringComparer.Ordinal) {
        ["IrradianceFill.comp"] = "rgba32f storage image with no NonReadable/NonWritable decoration",
        ["IrradianceRepair.comp"] = "rgba32f storage image with no NonReadable/NonWritable decoration",
        ["ImpostorFinish.comp"] = "rgba8 storage image with no NonReadable/NonWritable decoration",
        ["AutoExposure.comp"] = "'average' emitted twice — a groupshared and a local under one name"
    };

    /// <summary>
    ///     A compute entry point is refused by ES 3.00 with <c>RVN4001</c>, and emitted by ES 3.10.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because the refusal is the interesting one.</b> SPIRV-Cross will emit a
    ///     compute shader under <c>#version 300 es</c> quite happily — a file naming a stage the
    ///     version does not define, which fails on a device rather than on a desk. The dialect gate
    ///     is <c>EsslBackend.Supports</c>, and this is what holds it up: same module, two dialects,
    ///     and the difference is a named diagnostic rather than a silently different file.
    /// </remarks>
    [Fact]
    public void A_compute_entry_point_is_refused_by_es_300_and_emitted_by_es_310() {
        var module = Lower(Compute);

        var refusals = new DiagnosticBag();
        var atEs300 = Backend(GlslDialect.Essl300).Generate(module, refusals);

        Assert.Empty(atEs300);

        var refusal = Assert.Single(refusals.ToArray(), d => d.Id == "RVN4001");
        Assert.Contains("Reduce", refusal.ToString(), StringComparison.Ordinal);
        Assert.Contains("ESSL 3.00", refusal.ToString(), StringComparison.Ordinal);

        var accepted = new DiagnosticBag();
        var atEs310 = Backend(GlslDialect.Essl310).Generate(Lower(Compute), accepted);

        Assert.DoesNotContain(accepted.ToArray(), d => d.IsError);

        var unit = Assert.Single(atEs310);
        Assert.StartsWith("#version 310 es", unit.Code, StringComparison.Ordinal);

        var (valid, log) = EsslOracle.Validate(unit.Code, ShaderStage.Compute);
        Assert.True(valid, $"The ES 3.10 compute shader was refused:\n{log}\n\n{unit.Code}");
    }

    /// <summary>
    ///     A storage buffer is refused by ES 3.00 even in a vertex stage, and emitted by ES 3.10.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The refusal that is not about a stage</b>, and the one that actually bit: five
    ///     shipped library shaders read a <c>Buffer&lt;T&gt;</c> from a <em>raster</em> stage, and
    ///     SPIRV-Cross emits <c>layout(std430) readonly buffer</c> for them under
    ///     <c>#version 300 es</c> without complaint. GLSL ES has no shader storage block before
    ///     3.10, so that file is <i>"'std430' : not supported for this version"</i> on a device.
    ///     A storage buffer is not an <c>IrCapability</c> — SPIR-V has no feature bit for it, it is
    ///     just a block — so <c>EsslBackend.Refuses</c> has to ask the bindings directly, and this
    ///     is what holds that arm up.
    /// </remarks>
    [Fact]
    public void A_storage_buffer_is_refused_by_es_300_even_in_a_vertex_stage() {
        var refusals = new DiagnosticBag();

        Assert.Empty(Backend(GlslDialect.Essl300).Generate(Lower(StorageBufferVertex), refusals));

        var refusal = Assert.Single(refusals.ToArray(), d => d.Id == "RVN4001");
        Assert.Contains("storage buffer", refusal.ToString(), StringComparison.Ordinal);

        var accepted = new DiagnosticBag();
        var unit = Assert.Single(Backend(GlslDialect.Essl310).Generate(Lower(StorageBufferVertex), accepted));

        Assert.DoesNotContain(accepted.ToArray(), d => d.IsError);

        var (valid, log) = EsslOracle.Validate(unit.Code, ShaderStage.Vertex);
        Assert.True(valid, $"The ES 3.10 vertex shader was refused:\n{log}\n\n{unit.Code}");
    }

    /// <summary>
    ///     A combined sampler carries the name of the texture it came from.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Without the naming pass the uniform is called <c>_112</c>.</b> SPIRV-Cross creates
    ///     the combined object with no name and falls back to its SPIR-V id, and every GL profile
    ///     below 3.1 binds samplers by name after the link because it has no
    ///     <c>layout(binding = …)</c> to read. So the failure this pins is not a cosmetic one: it is
    ///     a texture the host cannot find, on exactly the profiles that need this translation most,
    ///     and it fails silently as texture unit zero rather than as an error.
    /// </remarks>
    [Fact]
    public void A_combined_sampler_takes_the_name_of_its_texture() {
        var fragment = Assert.Single(
            Generate(Lambert, EsslBackend.TargetName),
            unit => unit.Stage == ShaderStage.Fragment
        );

        Assert.Contains("sampler2D albedo;", fragment.Code, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"uniform\s+\w*\s*sampler2D\s+_\d+;", fragment.Code);
    }

    /// <summary>
    ///     The target is reachable by the name the CLI uses, once registered.
    /// </summary>
    /// <remarks>
    ///     ⚠ The guard against this repository's commonest defect — a finished thing nothing calls.
    ///     <c>EsslBackend</c> is reached only through <see cref="TargetBackends" />, and only because
    ///     <c>Vixen.Raven.Cli</c>'s entry point registers it; nothing in <c>Vixen.Raven</c> mentions
    ///     it. A registration that stopped happening would leave every other test in this file green,
    ///     because they all construct the backend directly.
    /// </remarks>
    [Fact]
    public void The_target_is_reachable_by_name_once_registered() {
        Assert.DoesNotContain(EsslBackend.TargetName, TargetBackends.Names);

        EsslBackend.Register();

        Assert.Contains(EsslBackend.TargetName, TargetBackends.Names);
        Assert.IsType<EsslBackend>(TargetBackends.Create(EsslBackend.TargetName));
    }

    // --- The shaders and the plumbing ---------------------------------------

    /// <summary>
    ///     A shader with a texture and a sampler — the pair GL cannot keep apart.
    /// </summary>
    /// <remarks>
    ///     The same source as <c>Raven/Vixen.Raven.Tests/Fixtures/lambert.rvn</c>, which is the
    ///     compiler's own golden fixture and therefore the one shape known to reach both backends
    ///     cleanly. Inlined rather than read from that directory: this suite would then fail on a
    ///     path, several directories away, for a reason that has nothing to do with transpiling.
    /// </remarks>
    const string Lambert = """
        package Vixen.Shaders

        shader Lambert {
            const val Ambient = 0.1

            var world: mat4
            var baseColor: float4 = float4(1, 1, 1, 1)
            var albedo: Texture2D
            var albedoSampler: Sampler

            func Diffuse(normal: float3, light: float3): float {
                val ndotl = dot(normalize(normal), normalize(-light))
                return max(ndotl, Ambient)
            }

            [VertexShader]
            [Semantic("SV_Position")]
            func Vertex(position: float3): float4 {
                return world * float4(position, 1)
            }

            [FragmentShader]
            [Semantic("SV_Target")]
            func Fragment(normal: float3, uv: float2): float4 {
                val sampled = albedo.Sample(albedoSampler, uv)
                return float4(baseColor.rgb * sampled.rgb, sampled.a)
            }
        }
        """;

    /// <summary>A compute entry point, which GLSL ES has only from 3.10.</summary>
    const string Compute = """
        package Vixen.Shaders

        shader Reduce {
            [Format("r32f")] var target: RWTexture2D<float4>

            [ComputeShader(8, 8, 1)]
            func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                target.Store(int2(int(id.x), int(id.y)), float4(1f, 0f, 0f, 0f))
            }
        }
        """;

    /// <summary>
    ///     A <em>raster</em> shader that reads a storage buffer, which GLSL ES also has only from
    ///     3.10.
    /// </summary>
    /// <remarks>
    ///     ⚠ Deliberately not a compute shader, so that the storage-buffer refusal is provably
    ///     separate from the stage refusal. A compute fixture would be refused for its stage first
    ///     and would prove nothing about the buffer.
    /// </remarks>
    const string StorageBufferVertex = """
        package Vixen.Shaders

        shader Instanced {
            var offsets: Buffer<float4>

            [VertexShader]
            [Semantic("SV_Position")]
            func Vertex(position: float3, [Semantic("SV_InstanceID")] instance: int): float4 {
                return float4(position, 1) + offsets[instance]
            }
        }
        """;

    static EsslBackend Backend(GlslDialect dialect) => new(dialect);

    static IReadOnlyList<GeneratedSource> Generate(string source, string target) {
        var bag = new DiagnosticBag();

        var backend = target == EsslBackend.TargetName
            ? Backend(GlslDialect.Essl300)
            : TargetBackends.Create(target) ?? throw new InvalidOperationException($"No '{target}' backend.");

        var generated = backend.Generate(Lower(source), bag);

        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        return generated;
    }

    static IrModule Lower(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        var diagnostics = compilation.GetDiagnostics();

        Assert.True(
            diagnostics.Count == 0,
            "The fixture does not bind cleanly:\n" + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        return module;
    }

    /// <summary>
    ///     The whole shipped library, lowered once.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One compilation over every file, for the reason
    ///         <c>LibraryTreeTests.TheWholeLibraryBindsAsOneCompilation</c> gives: the files depend
    ///         on each other, so binding one alone fails on a name that is not missing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The packages are globbed rather than listed.</b>
    ///         <c>LibraryReflectionTests</c> names its seventeen, and a copy of that list here would
    ///         silently stop covering the eighteenth. Enumerating the subdirectories cannot: a new
    ///         package is swept the day it is added. The root's own <c>Example1.rvn</c> and
    ///         <c>Example2.rvn</c> are deliberately outside, which is why this descends from the
    ///         directories rather than from the root.
    ///     </para>
    /// </remarks>
    static IrModule Library() {
        var root = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Library")
        );

        Assert.True(Directory.Exists(root), $"Raven/Library is not at {root}.");

        var files = Directory
            .EnumerateDirectories(root)
            .SelectMany(package => Directory.EnumerateFiles(package, "*.rvn", SearchOption.AllDirectories))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();

        Assert.True(files.Length > 40, $"Only {files.Length} library sources were found under {root}.");

        var trees = files
            .Select(file => SyntaxTree.ParseText(File.ReadAllText(file), path: Path.GetFileName(file)))
            .ToArray();

        var compilation = Compilation.Create(
            "Library",
            PermutationValues.Empty,
            LibraryComposition.With(PublishedComposition),
            trees
        );

        var diagnostics = compilation.GetDiagnostics();

        Assert.True(
            diagnostics.Count == 0,
            "The library does not bind cleanly:\n" + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        return module;
    }

    /// <summary>
    ///     What the shipped composition binds — the same three <c>LibraryReflectionTests</c> uses.
    /// </summary>
    /// <remarks>
    ///     The thirteen slots below these are in <c>LibraryComposition</c>, which this project
    ///     compiles from <c>Vixen.Raven.Tests</c> rather than copying. These three are the ones the
    ///     engine actually publishes with.
    /// </remarks>
    static readonly (string Slot, string Shader)[] PublishedComposition = [
        ("surface", "CompositeSurface"),
        ("CompositeSurface.first", "MetalRoughnessSurface"),
        ("shading", "StandardShading")
    ];
}
