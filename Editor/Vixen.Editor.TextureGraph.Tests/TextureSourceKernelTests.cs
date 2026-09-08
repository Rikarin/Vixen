// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § 4.1's source kernels, through the real Raven front end, with no device.
/// </summary>
/// <remarks>
///     <para>
///         <c>TextureKernelTests</c> already compiles every embedded kernel in every storable format,
///         so a source kernel that does not build is red there by existing. What is asserted here is
///         what that file's theories cannot reach: the <em>agreement</em> between each kernel's
///         uniform block and the <see cref="TextureSources" /> builder that fills it, and the binding
///         order the evaluator relies on positionally.
///     </para>
///     <para>
///         ⚠ <b>The agreement test is the one that earns its keep.</b>
///         <c>TexturePlanEvaluator.Uniforms</c> throws when an op omits a parameter, so a drifted
///         builder is a bake-time exception — on a device, in whichever test happens to use that
///         kernel, with a message about a uniform rather than about the builder. It also throws
///         nothing at all when an op carries a parameter the kernel does <em>not</em> declare: that
///         one is silently ignored, so renaming <c>scale</c> in a <c>.rvn</c> and forgetting the
///         builder would leave a shape drawn at its default size and no error anywhere.
///     </para>
/// </remarks>
public class TextureSourceKernelTests {
    /// <summary>The six of doc 48 § 4.1 that are implemented, and the two that are not.</summary>
    /// <remarks>
    ///     <c>Text</c> and <c>Svg Path</c> are the two § 4.1 names with no kernel: both rasterise on
    ///     the CPU — <c>Vixen.Ui.Text</c>'s outlines and <c>Core/Vixen.Ui/SvgPath.cs</c> through
    ///     <c>Rendering/PathTessellator.cs</c> — and reach a plan as an external image, which is
    ///     <c>Bitmap</c>'s path and not a kernel of their own.
    /// </remarks>
    public static TheoryData<string> Sources => ["Uniform", "Bitmap", "Gradient", "Shape", "Noise", "Checker"];

    /// <summary>The kernel every <see cref="TextureSources" /> builder emits, so a new builder is walked.</summary>
    public static TheoryData<string> Builders {
        get {
            TheoryData<string> data = [];

            foreach (var op in TextureSources.All) {
                data.Add(op.Kernel);
            }

            return data;
        }
    }

    /// <summary>Each of the six is embedded under the name a <see cref="TextureOp" /> gives.</summary>
    [Theory]
    [MemberData(nameof(Sources))]
    public void A_source_kernel_is_embedded_under_its_own_name(string kernel) =>
        Assert.Contains(kernel, TextureKernels.Names);

    /// <summary>
    ///     Every parameter a source kernel declares is one its builder supplies, and every parameter
    ///     the builder supplies is one the kernel declares.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both directions, because they fail differently.</b> A kernel member the op omits is an
    ///     exception at bake time; an op parameter the kernel does not declare is silently dropped and
    ///     the picture is drawn with a default. The second is the one that produces a plausible
    ///     picture, and it is the one only this direction of the comparison catches.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Builders))]
    public void A_builder_supplies_exactly_the_parameters_its_kernel_declares(string kernel) {
        var op = Assert.Single(TextureSources.All, candidate => candidate.Kernel == kernel);
        var data = Compile(kernel);

        var declared = data.Parameters
            .Where(member => member.Set == DescriptorSetSlot.PerMaterial)
            .Select(member => Unqualified(member.Name, data.ShaderName))
            // `seed` is the one member the evaluator fills itself, from `TexturePlan.SeedFor`.
            .Where(name => !string.Equals(name, "seed", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var supplied = op.Parameters
            .Select(parameter => parameter.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, supplied);
    }

    /// <summary>
    ///     A source kernel's textures are declared in the order the evaluator binds an op's inputs.
    /// </summary>
    /// <remarks>
    ///     ⚠ Four of the six read nothing at all — they are <em>sources</em> — and that is worth
    ///     asserting rather than assuming: a stray <c>Texture2D</c> in one of them would make the
    ///     evaluator refuse every op that runs it, with a message about input counts.
    /// </remarks>
    [Theory]
    [InlineData("Uniform")]
    [InlineData("Shape")]
    [InlineData("Checker")]
    [InlineData("Noise")]
    [InlineData("Bitmap", "source")]
    [InlineData("Gradient", "ramp")]
    public void A_source_kernel_declares_its_inputs_in_the_order_the_evaluator_binds_them(
        string kernel,
        params string[] inputs
    ) {
        var data = Compile(kernel);

        var textures = data.Bindings
            .Where(binding => binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.SampledTexture })
            .OrderBy(binding => binding.Binding)
            .Select(binding => binding.Name)
            .ToArray();

        Assert.Equal(inputs, textures);
    }

    /// <summary>
    ///     A kernel reaches the shader library through the compilation it is in, and never on its own.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This test used to assert only the first half and called it a tripwire against a
    ///         day that had already arrived.</b> Its remarks said a kernel "cannot reach the shader
    ///         library" and that the fix would be a reference path or a compiled closure —
    ///         <see href="https://github.com/Rikarin/Vixen/issues/635">#635</see> says the same. Both
    ///         describe the compiler wrongly. <c>RavenEffectCompiler.FromSources</c> takes a
    ///         <em>set</em> of texts and makes them one compilation, a package's declarations are
    ///         visible across one compilation, and nothing about a <c>.rvnlib</c> was ever load
    ///         bearing. What was true is the sentence with the subject corrected: a kernel compiled
    ///         <em>alone</em> cannot reach it.
    ///     </para>
    ///     <para>
    ///         <b>So both halves are asserted, and each is the other's instrument.</b> The same
    ///         source fails when it is the only text and compiles when
    ///         <see cref="TextureKernelPrelude" /> supplies the library beside it. A prelude that
    ///         stopped shipping its sources leaves the second half red; a compiler that resolved
    ///         imports out of thin air leaves the first half red. Neither half alone can tell those
    ///         apart from success.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_kernel_reaches_the_library_through_its_compilation_and_not_alone() {
        const string Source = """
            package Vixen.Editor.TextureGraph.Shaders

            import Vixen.Shaders.Core

            shader Reach {
                [Format("rgba16f")] var target: RWTexture2D<float4>

                [ComputeShader(8, 8, 1)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    val value = Random.Float01(id.x)

                    target.Store(int2(int(id.x), int(id.y)), float4(value, value, value, 1f))
                }
            }
            """;

        var alone = Record.Exception(
            () => RavenEffectCompiler.FromSources([("Reach.rvn", Source)]).TryGet(EffectKey.Of("Reach"))
        );

        Assert.NotNull(alone);

        // Not the message, which is the compiler's to word — only that the library did not resolve.
        Assert.Contains("Random", alone.Message, StringComparison.Ordinal);

        var beside = TextureKernelPrelude.Compile("Reach.rvn", Source).TryGet(EffectKey.Of("Reach"));

        Assert.NotNull(beside);
        Assert.NotEmpty(Assert.Single(beside.Stages).Bytecode);
    }

    /// <summary>Every <c>import</c> any kernel writes names a package the prelude supplies.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This test used to be <c>A_source_kernel_imports_nothing</c>, and the rule it
    ///         guarded is gone</b> — five kernels import today. What replaces it is the rule that
    ///         actually holds: an <c>import</c> resolves only because
    ///         <see cref="TextureKernelPrelude.Sources" /> is in the compilation, so a kernel may
    ///         name a package one of those files declares and no other.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The packages are read out of the prelude's own texts rather than listed here.</b>
    ///         A list here is a second copy of the prelude that would go on matching after the
    ///         prelude changed — and it would let an import of a package nobody supplies pass, which
    ///         is the failure this exists to catch: a kernel that imports a package no source
    ///         declares does not fail to parse, it fails to bind, at bake time, on a device.
    ///     </para>
    ///     <para>
    ///         <b>The instrument.</b> The sweep proves it found imports at all and that it found
    ///         packages at all before it compares them, because an empty set is a subset of
    ///         everything.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_import_a_kernel_writes_names_a_package_the_prelude_supplies() {
        string[] supplied = [
            .. TextureKernelPrelude
                .Sources
                .SelectMany(source => Declarations(source.Text, "package"))
                .Distinct(StringComparer.Ordinal)
        ];

        Assert.NotEmpty(supplied);

        var imports = 0;

        foreach (var kernel in TextureKernels.Names) {
            foreach (var package in Declarations(TextureKernels.Source(kernel), "import")) {
                imports++;

                Assert.True(
                    supplied.Contains(package, StringComparer.Ordinal),
                    $"`{kernel}.rvn` imports `{package}`, which no prelude source declares. A kernel is "
                    + "compiled with `TextureKernelPrelude.Sources` beside it and nothing else, so an "
                    + "import of anything else binds against nothing — add the library file to the "
                    + "prelude, or transcribe and give the copy a parity row."
                );
            }
        }

        Assert.NotEqual(0, imports);
    }

    /// <summary>Every <c>package</c> or <c>import</c> a Raven source declares.</summary>
    /// <param name="source">The text to read.</param>
    /// <param name="keyword">Which of the two.</param>
    /// <returns>The names, in order.</returns>
    /// <remarks>
    ///     Anchored at the start of a line, so that a header discussing an import in prose — which
    ///     five of these kernels do at length — is not read as one.
    /// </remarks>
    static IEnumerable<string> Declarations(string source, string keyword) =>
        source
            .Split('\n')
            .Where(line => line.StartsWith(keyword + " ", StringComparison.Ordinal))
            .Select(line => line[(keyword.Length + 1)..].Trim());

    static string Unqualified(string name, string shader) =>
        name.Length > shader.Length + 1
        && name.StartsWith(shader, StringComparison.Ordinal)
        && name[shader.Length] == '.'
            ? name[(shader.Length + 1)..]
            : name;

    static EffectData Compile(string kernel) {
        var data = TextureKernelPrelude
            .Compile(
                TextureKernels.VariantName(kernel, TextureFormat.Rgba8),
                TextureKernels.Variant(kernel, TextureFormat.Rgba8)
            )
            .TryGet(EffectKey.Of(kernel));

        Assert.NotNull(data);

        return data;
    }
}
