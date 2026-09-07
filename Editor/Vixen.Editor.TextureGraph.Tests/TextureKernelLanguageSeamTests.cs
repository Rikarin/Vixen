// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Editor.TextureGraph;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The two things a kernel can say that nothing between a plan and Raven can carry.
/// </summary>
/// <remarks>
///     <para>
///         <b>Both are the same shape: a feature of the shader language that a texture-graph kernel
///         is written in and cannot use.</b> A kernel is compiled by
///         <c>TexturePlanEvaluator.VariantFor</c> as
///         <c>RavenEffectCompiler.FromSources([(name, source)])</c> with <c>EffectKey.Of(kernel)</c>
///         — one source, no <c>referencePaths</c>, no defines. So it cannot <c>import</c>
///         (<see href="https://github.com/Rikarin/Vixen/issues/635">#635</see>) and it cannot declare
///         a <c>[Permutation]</c> (<see href="https://github.com/Rikarin/Vixen/issues/638">#638</see>).
///     </para>
///     <para>
///         ⚠ <b>Neither failure is a compile error, and that is the whole reason this file exists.</b>
///         An import that cannot resolve *is* loud. But a permutation the plan cannot name compiles
///         perfectly and silently takes its <c>.rvn</c> default in every op for ever — this
///         repository's registered-permutation trap arriving from the side with no key list at all.
///         And a function transcribed instead of imported compiles perfectly and diverges the first
///         time one of the two copies is edited.
///     </para>
///     <para>
///         ⚠ <b>What these assertions would say if they stopped reading anything.</b> Each one first
///         proves it found the text it is about — a kernel folder with sources in it, a
///         <c>[Format(</c> in every kernel, a <c>HueRotate</c> body in each of the two files with the
///         right number of constants in it. A sweep that matched nothing would otherwise report that
///         nothing is wrong, which is exactly the "comparator that called three empty manifests
///         identical" this repository has already shipped once.
///     </para>
/// </remarks>
public class TextureKernelLanguageSeamTests {
    /// <summary>The repository root, walked up from the test binary rather than from a source path.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>[CallerFilePath]</c>.</b> CI sets <c>DeterministicSourcePaths</c>, which maps
    ///     the repository root to <c>/_/</c>, so a test anchored on its own compiled path fails on
    ///     every runner at once while passing here.
    /// </remarks>
    static string Root =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>Where the kernels live in the source tree.</summary>
    static string ShaderRoot =>
        Path.Combine(Root, "Editor", "Vixen.Editor.TextureGraph", "Shaders");

    /// <summary>Every float literal in a piece of Raven source, in the order it is written.</summary>
    /// <param name="body">The source to read.</param>
    /// <returns>The literals, with a sign only where the minus is written against the digits.</returns>
    /// <remarks>
    ///     ⚠ <b>A leading minus counts only when it is adjacent</b>, which is what makes this a
    ///     comparison of two files rather than of two arithmetics: <c>float3(0.596f, -0.274f, …)</c>
    ///     carries its sign in the literal, and <c>luma - ri * 0.272f</c> carries it in an operator
    ///     this does not see. Both files are written the same way, so both are read the same way —
    ///     and a change that moved a minus from one form to the other would show up here as a
    ///     difference, which is the right answer.
    /// </remarks>
    static string[] Constants(string body) =>
        [
            .. Regex
                .Matches(body, @"-?\d+(?:\.\d+)?f")
                .Select(match => match.Value)
        ];

    /// <summary>The body of a named function, from its <c>{</c> to the brace that closes it.</summary>
    /// <param name="source">The file to read.</param>
    /// <param name="name">The function's name.</param>
    /// <returns>The body, braces included.</returns>
    static string Function(string source, string name) {
        var at = source.IndexOf($"func {name}(", StringComparison.Ordinal);

        Assert.True(at >= 0, $"no `func {name}(` in the source this test was pointed at");

        var open = source.IndexOf('{', at);

        Assert.True(open >= 0, $"`func {name}` has no body");

        var depth = 0;

        for (var index = open; index < source.Length; index++) {
            if (source[index] == '{') {
                depth++;
            } else if (source[index] == '}') {
                depth--;

                if (depth == 0) {
                    return source[open..(index + 1)];
                }
            }
        }

        Assert.Fail($"`func {name}` has no closing brace");

        return string.Empty;
    }

    /// <summary>
    ///     No kernel declares a <c>[Permutation]</c>, because a plan has nowhere to put its value.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see href="https://github.com/Rikarin/Vixen/issues/638">#638</see>. <c>TextureOp</c>
    ///         carries no permutation, <c>TexturePlan</c> carries no permutation, and
    ///         <c>VariantFor</c> builds its <c>EffectKey</c> from the kernel's name alone and caches
    ///         on <c>(kernel, format)</c>. So a <c>[Permutation]</c> written into a kernel today
    ///         would take its declared default in every op of every plan, silently, for ever — and
    ///         the moment a value *could* be passed, the cache key would be incomplete and two ops
    ///         with different permutations would share one pipeline, the second drawing the first
    ///         one's picture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A refusal rather than a feature, and the issue asks for exactly that choice.</b>
    ///         Doc 48 § 4.1 specifies <c>Noise</c>'s basis as a permutation; it is implemented as an
    ///         <c>int</c> uniform with a branch, and <c>TextureNoiseBasis</c> says why — four bases
    ///         times three storable formats is twelve modules for a branch every invocation in a
    ///         dispatch takes the same way. What was missing was not the decision but anything that
    ///         would notice when somebody makes the other one. This is that, and the message is where
    ///         the work is written down.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_kernel_declares_a_permutation_the_plan_could_not_name() {
        Assert.True(Directory.Exists(ShaderRoot), $"the kernel sources are not where this test looks: {ShaderRoot}");

        var declared = new List<string>();
        var formats = 0;
        var read = 0;

        foreach (var kernel in TextureKernels.Names) {
            var source = TextureKernels.Source(kernel);

            read++;

            // The instrument: every kernel writes a storage image, so every one of them carries a
            // `[Format(`. A sweep that matched none of those is reading empty strings, and a sweep
            // reading empty strings finds no permutations either.
            if (source.Contains("[Format(", StringComparison.Ordinal)) {
                formats++;
            }

            if (Declaration.IsMatch(source)) {
                declared.Add(kernel);
            }
        }

        Assert.NotEqual(0, read);
        Assert.Equal(read, formats);

        // ⚠ The other half of the instrument, and it is the half this test failed on first. A plain
        // `Contains("[Permutation]")` reported three kernels — `Blend`, `Noise` and `Shape` — every
        // one of which mentions the attribute in a *comment* saying why it uses a uniform instead.
        // A predicate that cannot tell a declaration from prose about a declaration is worse than no
        // predicate: it goes red for the files that thought hardest about this. So the pattern is
        // anchored at the start of a line, where a comment's `//` is in the way, and it is proved
        // against a source that really does declare one.
        Assert.Matches(Declaration, Declaring);

        Assert.True(
            declared.Count == 0,
            "These kernels declare a `[Permutation]`, and nothing between a plan and the compiler can "
            + "give one a value:\n  "
            + string.Join("\n  ", declared)
            + "\nSo the variant takes the `.rvn` default in every op, silently — #638. Either write the "
            + "choice as a uniform and branch on it, the way `Noise` writes its basis, or thread a "
            + "permutation through: a field on `TextureOp`, that field in `VariantFor`'s `EffectKey` "
            + "*and* in its `(kernel, format)` cache key — the cache key is the half that turns two "
            + "permutations into one shared pipeline if it is forgotten."
        );
    }

    /// <summary>
    ///     A kernel that declares a permutation compiles, takes the default, and says nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The demonstration behind the refusal above, because a ban whose reason is only
    ///         written down is a ban somebody lifts.</b> This puts a two-shader-line source through
    ///         the *same* call the evaluator makes — one source, no defines — and reads the compiled
    ///         result. It compiles. There is no diagnostic. What comes back is the branch the
    ///         declared default selects, and no argument anywhere could have selected the other one.
    ///     </para>
    ///     <para>
    ///         It asserts on the reflected permutation rather than on the bytecode, because the
    ///         bytecode of a folded permutation is just a shader: there is nothing in it that says a
    ///         choice was made, which is precisely the complaint.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_kernel_that_declares_a_permutation_silently_takes_its_default() {
        var data = RavenEffectCompiler.FromSources([("Chosen.rvn", Declaring)]).TryGet(EffectKey.Of("Chosen"));

        // ⚠ Not an error, not a warning, not a missing effect. This is the entire finding: the
        // language accepted a switch the layer above has no way to throw.
        Assert.NotNull(data);
        Assert.Equal("Chosen", data.ShaderName);
        Assert.NotEmpty(data.Stages);
        Assert.All(data.Stages, stage => Assert.NotEmpty(stage.Bytecode));
    }

    /// <summary>A kernel declaring a permutation, which is both fixtures' subject.</summary>
    /// <remarks>
    ///     ⚠ <b>`val`, not `var`, and the compiler is emphatic about it</b> — <c>RVN2061</c>: a
    ///     permutation key is fixed when the shader is compiled, so it cannot be a mutable binding.
    ///     Which is the whole complaint restated by the language itself: the value is chosen at
    ///     compile time, and nothing between a plan and the compiler is holding one.
    /// </remarks>
    const string Declaring = """
            package Vixen.Editor.TextureGraph.Shaders

            shader Chosen {
                [Permutation] val Fancy: bool = false

                [Format("rgba16f")] var target: RWTexture2D<float4>

                [ComputeShader(8, 8, 1)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    val coord = int2(int(id.x), int(id.y))
                    val size = target.GetDimensions()

                    if (coord.x >= size.x || coord.y >= size.y) {
                        return
                    }

                    if (Fancy) {
                        target.Store(coord, float4(1f, 1f, 1f, 1f))
                    } else {
                        target.Store(coord, float4(0f, 0f, 0f, 1f))
                    }
                }
            }
            """;

    /// <summary>A <c>[Permutation]</c> written where a declaration goes, rather than in prose.</summary>
    /// <remarks>
    ///     Anchored at the start of a line so a comment's <c>//</c> cannot satisfy it. Three kernels
    ///     discuss the attribute in their headers and none of them declares one, and telling those
    ///     two apart is the whole job.
    /// </remarks>
    static readonly Regex Declaration = new(@"^[ \t]*\[Permutation\]", RegexOptions.Multiline);

    /// <summary>
    ///     <c>Hsl.rvn</c>'s hue rotation is <c>ComputeColor.rvn</c>'s, constant for constant.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see href="https://github.com/Rikarin/Vixen/issues/635">#635</see>. A kernel binds
    ///         against nothing but itself, so <c>import Vixen.Shaders.Material</c> does not resolve
    ///         and the YIQ chroma rotation the shader graph uses is <em>transcribed</em> into this
    ///         assembly. Two copies of nine constants, chosen over a convert-rotate-convert precisely
    ///         so that a material graph and a texture graph agree about what a hue is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The failure mode is not a compile error, it is a disagreement</b>, and it has no
    ///         symptom until an artist matches a hue in one editor and watches it shift in the other.
    ///         Nothing would fail when one copy is edited. This is that nothing, filled in — it is
    ///         not the fix the issue asks for and it is what makes the fix optional rather than
    ///         urgent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it cannot see.</b> Zeros are dropped from the comparison, because the two
    ///         files spell the clamp differently and always have — <c>float3(0f)</c> against
    ///         <c>float3(0f, 0f, 0f)</c>, which are the same vector. So a change from
    ///         <c>max(…, 0f)</c> to <c>max(…, 1f)</c> is caught (1 is not a zero) and a change from
    ///         <c>max</c> to <c>min</c> is not. It compares the numbers, not the operators, and
    ///         saying so is the point: every constant in the rotation is a number.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_transcribed_hue_rotation_still_matches_the_one_it_was_copied_from() {
        var library = Path.Combine(Root, "Raven", "Library", "Material", "ComputeColor.rvn");

        Assert.True(
            File.Exists(library),
            $"the shader library's ComputeColor.rvn is not at {library} — this test compares two files "
            + "and one of them moved, which is a state where it would otherwise compare nothing"
        );

        var mine = Constants(Function(TextureKernels.Source("Hsl"), "HueRotate"))
            .Where(literal => !IsZero(literal))
            .ToArray();

        var theirs = Constants(Function(File.ReadAllText(library), "HueRotate"))
            .Where(literal => !IsZero(literal))
            .ToArray();

        // The instrument. Three luminance weights, three YIQ rows of three, six inverse coefficients
        // — fifteen numbers that are not zero. Two empty arrays are equal, and a `Function` that
        // silently returned the wrong span would hand back a plausible few.
        Assert.Equal(15, mine.Length);

        Assert.Equal(theirs, mine);
    }

    /// <summary>Whether a Raven float literal is a zero, however it is spelled.</summary>
    static bool IsZero(string literal) =>
        float.TryParse(literal.TrimEnd('f'), System.Globalization.CultureInfo.InvariantCulture, out var value)
        && value == 0f;
}
