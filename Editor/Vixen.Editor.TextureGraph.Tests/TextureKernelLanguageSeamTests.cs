// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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
///         ⚠ <b>The transcription is not one function, it is thirteen copies across five kernels,
///         and until now one of them was held.</b> <c>Hsl</c> carries <c>ComputeColor.HueRotate</c>;
///         <c>Noise</c>, <c>FloodFill</c>, <c>Splatter</c> and <c>TileSampler</c> each carry
///         <c>Random.Hash</c>, <c>Random.Combine</c> (twice under the name <c>Mix</c>) and
///         <c>Random.ToFloat01</c> together with the three constants they stand on; <c>Checker</c>
///         carries <c>ComputeColor.Checker</c>'s return expression. <see cref="Parity" /> is the
///         table and <see cref="Every_transcription_of_the_library_still_matches_its_original" /> is
///         the gate over all of it.
///     </para>
///     <para>
///         ⚠ <b>A table is only a gate while it is complete, so the completeness is asserted too.</b>
///         <see cref="Every_kernel_that_copies_Random_rvn_is_in_the_parity_table" /> sweeps every
///         kernel for the three constants <c>Random.rvn</c> declares — read out of
///         <c>Random.rvn</c> rather than written here — and requires the kernels carrying one to be
///         exactly the kernels the table names. A sixth kernel copying the hash goes red on the day
///         it lands rather than on the day the two copies disagree.
///     </para>
///     <para>
///         ⚠ <b>What these assertions would say if they stopped reading anything.</b> Each one first
///         proves it found the text it is about — a kernel folder with sources in it, a
///         <c>[Format(</c> in every kernel, an arithmetic of a stated size in each of the two files
///         it compares. A sweep that matched nothing would otherwise report that nothing is wrong,
///         which is exactly the "comparator that called three empty manifests identical" this
///         repository has already shipped once.
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

    /// <summary>One of the shader library's sources, read off disk.</summary>
    /// <param name="parts">The path under <c>Raven/Library</c>, a segment at a time.</param>
    /// <returns>The Raven text.</returns>
    /// <remarks>
    ///     ⚠ <b>The library half of every comparison here comes from the file rather than from a
    ///     copy in this test</b>, because a copy in this test is a third copy of the arithmetic and
    ///     the one this file exists to argue against. It asserts the file is where it looks, so a
    ///     move shows up as "the original is gone" rather than as a comparison of nothing.
    /// </remarks>
    static string Library(params string[] parts) {
        var path = Path.Combine([Root, "Raven", "Library", .. parts]);

        Assert.True(
            File.Exists(path),
            $"the shader library source this test compares against is not at {path} — one of the two "
            + "files moved, which is a state where this would otherwise compare nothing"
        );

        return File.ReadAllText(path);
    }

    /// <summary>Raven source with its line comments removed.</summary>
    /// <param name="source">The text to strip.</param>
    /// <returns>The same text with everything after a <c>//</c> on each line dropped.</returns>
    /// <remarks>
    ///     Both <c>///</c> doc comments and plain <c>//</c> ones, because the transcriptions carry
    ///     prose about the original and the original carries prose about itself, and neither is the
    ///     arithmetic. No kernel compared here contains a string literal, so there is nothing a
    ///     <c>//</c> can hide inside.
    /// </remarks>
    static string Uncommented(string source) =>
        string.Join('\n', source.Split('\n').Select(line => Comment.Replace(line, string.Empty)));

    /// <summary>Every <c>const val</c> a source declares, by name.</summary>
    /// <param name="source">The file to read.</param>
    /// <returns>The literal each constant is declared as.</returns>
    /// <remarks>
    ///     ⚠ <b>This is what makes the comparison spelling-independent in the one place it has to
    ///     be.</b> <c>Random.rvn</c> writes <c>0x2C9277B5u</c> into a constant called
    ///     <c>Multiplier</c> and <c>Noise.rvn</c> writes the same digits into a constant it also
    ///     calls <c>Multiplier</c>, but <c>Combine</c>'s golden-ratio operand is a bare literal in
    ///     the library and a constant called <c>Golden</c> in every kernel. Substituting the
    ///     declarations back into the body before reading its numbers compares the arithmetic
    ///     instead of the naming — and checks the constants themselves for free, because a copy that
    ///     mistyped one substitutes a different number.
    /// </remarks>
    static Dictionary<string, string> Declared(string source) {
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in ConstantDeclaration.Matches(Uncommented(source))) {
            declared[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return declared;
    }

    /// <summary>The body of a named function, however it is spelled.</summary>
    /// <param name="source">The file to read.</param>
    /// <param name="name">The function's name.</param>
    /// <returns>The body: braces included for a block, the expression alone for a <c>=&gt;</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>Both forms, because the three <c>Random</c> helpers are written in both.</b>
    ///     <c>Hash</c> is a block and <c>Combine</c> and <c>ToFloat01</c> are expression bodies in
    ///     every one of the five files. An extractor that only knew braces would walk past a
    ///     <c>=&gt;</c> function and return the *next* function's block, which is the failure mode
    ///     where a comparison passes while reading the wrong thing — so the arrow is taken only when
    ///     it comes before the next brace, and the body then ends at the newline, which in Raven is
    ///     what ends a statement.
    /// </remarks>
    static string Function(string source, string name) {
        var text = Uncommented(source);
        var at = text.IndexOf($"func {name}(", StringComparison.Ordinal);

        Assert.True(at >= 0, $"no `func {name}(` in the source this test was pointed at");

        var open = text.IndexOf('{', at);
        var arrow = text.IndexOf("=>", at, StringComparison.Ordinal);

        if (arrow >= 0 && (open < 0 || arrow < open)) {
            var line = text.IndexOf('\n', arrow);

            return line < 0 ? text[(arrow + 2)..] : text[(arrow + 2)..line];
        }

        Assert.True(open >= 0, $"`func {name}` has no body");

        var depth = 0;

        for (var index = open; index < text.Length; index++) {
            if (text[index] == '{') {
                depth++;
            } else if (text[index] == '}') {
                depth--;

                if (depth == 0) {
                    return text[open..(index + 1)];
                }
            }
        }

        Assert.Fail($"`func {name}` has no closing brace");

        return string.Empty;
    }

    /// <summary>What one function computes, with everything about how it is written removed.</summary>
    /// <param name="Numbers">Every numeric literal, in order, as a value rather than as digits.</param>
    /// <param name="Operators">Every arithmetic, shift and bitwise operator, in order.</param>
    /// <param name="Calls">Every name that is called, in order — <c>max</c>, <c>dot</c>, <c>float3</c>.</param>
    /// <remarks>
    ///     <para>
    ///         <b>Three sequences rather than the text, because the two copies of every one of these
    ///         functions are deliberately spelled differently and always were.</b> Parameters are
    ///         renamed (<c>seed</c> against <c>value</c>), the library's helpers are <c>static</c>
    ///         and a kernel's are not, two kernels call <c>Random.Combine</c> <c>Mix</c>, and
    ///         <c>float3(0f)</c> and <c>float3(0f, 0f, 0f)</c> are the same vector. A text diff
    ///         between them would be red today and would stay red, which is a gate nobody keeps.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The three together are the arithmetic, and each one alone is not.</b> Numbers
    ///         alone cannot see <c>&gt;&gt;</c> become <c>&lt;&lt;</c> or <c>max</c> become
    ///         <c>min</c> — the first is why the operators are read and the second is why the calls
    ///         are, and this file's previous version admitted the <c>max</c>/<c>min</c> gap in as
    ///         many words. What none of the three can see is a renamed *variable* being read in
    ///         place of another of the same type, which no comparison short of compiling both and
    ///         diffing the SPIR-V could.
    ///     </para>
    /// </remarks>
    sealed record Arithmetic(string[] Numbers, string[] Operators, string[] Calls);

    /// <summary>Reduces a named function to what it computes.</summary>
    /// <param name="source">The file the function is in.</param>
    /// <param name="name">The function's name.</param>
    /// <returns>Its numbers, operators and calls.</returns>
    /// <remarks>
    ///     ⚠ <b>Zeros are dropped from the numbers and nothing else is.</b> The two spellings of the
    ///     clamp differ in how many zeros they write and in nothing else, so counting them would
    ///     compare the punctuation. A change from <c>max(…, 0f)</c> to <c>max(…, 1f)</c> is still
    ///     caught, because 1 is not a zero — and a change from <c>max</c> to <c>min</c> is caught by
    ///     <see cref="Arithmetic.Calls" />.
    /// </remarks>
    static Arithmetic Reduce(string source, string name) {
        var body = Function(source, name);

        // Longest name first, so a constant whose name is a prefix of another cannot be substituted
        // into the middle of it. `\b` would mostly cover it; ordering costs nothing and does not
        // depend on what a word boundary thinks an identifier is.
        foreach (var (constant, literal) in Declared(source).OrderByDescending(pair => pair.Key.Length)) {
            // `$` is the substitution character in a replacement, and a Raven literal never contains
            // one — escaping it anyway costs nothing and keeps a future hexadecimal spelling honest.
            var replacement = literal.Replace("$", "$$", StringComparison.Ordinal);

            body = Regex.Replace(body, $@"\b{Regex.Escape(constant)}\b", replacement);
        }

        string[] numbers = [
            .. Number
                .Matches(body)
                .Select(match => Value(match.Value))
                .Where(value => value != 0d)
                .Select(value => value.ToString("R", CultureInfo.InvariantCulture))
        ];

        // The operators are read from a body the numbers have been taken out of, so that the minus
        // written against a literal — `float3(0.596f, -0.274f, …)` — and the minus written as an
        // operator are the same character to this and are counted the same way in both files.
        var bare = Number.Replace(body, " ");

        return new(
            numbers,
            [.. Operator.Matches(bare).Select(match => match.Value)],
            [.. Call.Matches(body).Select(match => match.Groups[1].Value)]
        );
    }

    /// <summary>A Raven numeric literal as a number, whichever base it is written in.</summary>
    static double Value(string literal) =>
        literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt64(literal.TrimEnd('u', 'U'), 16)
            : double.Parse(literal.TrimEnd('f', 'F', 'u', 'U'), CultureInfo.InvariantCulture);

    /// <summary>A line comment, doc or plain.</summary>
    static readonly Regex Comment = new(@"//.*$", RegexOptions.Multiline);

    /// <summary>A <c>const val</c> declaration and the literal it is given.</summary>
    static readonly Regex ConstantDeclaration =
        new(@"const\s+val\s+([A-Za-z_]\w*)\s*(?::\s*[A-Za-z_]\w*\s*)?=\s*(\S+)");

    /// <summary>A numeric literal, hexadecimal or decimal, not part of an identifier.</summary>
    /// <remarks>
    ///     ⚠ The lookbehind is what stops <c>float2</c>, <c>uint3</c> and <c>ToFloat01</c> from
    ///     reading as numbers — the commonest way a comparison like this ends up comparing type
    ///     names. Hexadecimal is first in the alternation so <c>0x2C9277B5u</c> is one token and not
    ///     a zero followed by an identifier.
    /// </remarks>
    static readonly Regex Number =
        new(@"(?<![A-Za-z0-9_.])(?:0[xX][0-9A-Fa-f]+[uU]?|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?[fFuU]?)");

    /// <summary>An arithmetic, shift or bitwise operator.</summary>
    /// <remarks>The two-character shifts come first, so <c>&gt;&gt;</c> is one token and not two.</remarks>
    static readonly Regex Operator = new(@">>|<<|\^|\*|/|%|\+|-|&|\|");

    /// <summary>A name being called.</summary>
    static readonly Regex Call = new(@"(?<![A-Za-z0-9_])([A-Za-z_]\w*)\s*\(");

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
    ///         Doc 48 § 4.1 specified <c>Noise</c>'s basis as a permutation; it is implemented as an
    ///         <c>int</c> uniform with a branch, <c>TextureNoiseBasis</c> says why — four bases times
    ///         three storable formats is twelve modules for a branch every invocation in a dispatch
    ///         takes the same way — and § 4.1 now says the same. What was missing was not the
    ///         decision but anything that would notice when somebody makes the other one. This is
    ///         that, and the message is where the work is written down.
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
    ///     A kernel that declares a permutation compiles, takes its default, and says nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The demonstration behind the refusal above, because a ban whose reason is only
    ///         written down is a ban somebody lifts.</b> This puts a source declaring
    ///         <c>[Permutation] val Fancy</c> through the *same* call the evaluator makes — one
    ///         source, no defines — and reads what comes back. It compiles. There is no diagnostic,
    ///         at any severity. Nothing at this call site could have said which branch it wanted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And "took the default" is asserted rather than assumed, by compiling the same
    ///         shader twice with the two defaults and requiring the modules to differ.</b> That is
    ///         the half that makes this a finding: if the two came out identical, the permutation
    ///         would be dead text and there would be nothing to lose. They do not, so the choice is
    ///         real, is made at compile time, and is made by the only party that can — whoever last
    ///         edited the <c>.rvn</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_kernel_that_declares_a_permutation_silently_takes_its_default() {
        var data = RavenEffectCompiler.FromSources([("Chosen.rvn", Declaring)]).TryGet(EffectKey.Of("Chosen"));

        // ⚠ Not an error, not a warning, not a missing effect. This is the entire finding: the
        // language accepted a switch the layer above has no way to throw.
        Assert.NotNull(data);
        Assert.Equal("Chosen", data.ShaderName);

        var chosen = Assert.Single(data.Stages);

        Assert.NotEmpty(chosen.Bytecode);

        var flipped = RavenEffectCompiler
            .FromSources([("Chosen.rvn", Declaring.Replace("Fancy: bool = false", "Fancy: bool = true", StringComparison.Ordinal))])
            .TryGet(EffectKey.Of("Chosen"));

        Assert.NotNull(flipped);
        Assert.NotEqual(chosen.Bytecode, Assert.Single(flipped.Stages).Bytecode);
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

    /// <summary>One function a kernel transcribed, and the library function it was copied from.</summary>
    /// <param name="Kernel">The kernel that carries the copy.</param>
    /// <param name="Copy">What the copy is called there.</param>
    /// <param name="LibraryFile">The library source under <c>Raven/Library</c>.</param>
    /// <param name="Original">What the original is called.</param>
    /// <param name="Numbers">How many non-zero literals the arithmetic has, as the instrument check.</param>
    /// <remarks>
    ///     ⚠ <b><see cref="Numbers" /> is the half that stops this being a comparator of two empty
    ///     strings.</b> Two arithmetics that were both read as nothing are equal, and an extractor
    ///     pointed at a function that has moved returns exactly that. Every row states the size it
    ///     expects, so a reader who changes one of these functions has to say what the new size is.
    /// </remarks>
    sealed record Transcription(string Kernel, string Copy, string LibraryFile, string Original, int Numbers);

    /// <summary>Every function this assembly transcribed out of the shader library.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Thirteen rows across five kernels, and the reason each exists is
    ///         <see href="https://github.com/Rikarin/Vixen/issues/635">#635</see>: a kernel binds
    ///         against nothing but itself.</b> The hue rotation was the first and is the one the
    ///         issue names; the four copies of <c>Random</c>'s hash are the worse ones, because
    ///         <c>Random.rvn</c>'s header argues every choice in it from exactness — wrapping 32-bit
    ///         arithmetic only, no float in the state, a multiply by a power of two rather than a
    ///         division — so a copy that drifts by one shift is a field that is subtly different on
    ///         one backend and impossible to attribute.
    ///     </para>
    ///     <para>
    ///         <b>Two kernels call <c>Random.Combine</c> <c>Mix</c></b>, which is why the row carries
    ///         both names rather than assuming they agree.
    ///     </para>
    /// </remarks>
    public static TheoryData<string, string, string, string, int> Parity =>
        Rows.Aggregate(
            new TheoryData<string, string, string, string, int>(),
            (data, row) => {
                data.Add(row.Kernel, row.Copy, row.LibraryFile, row.Original, row.Numbers);

                return data;
            }
        );

    /// <summary>The same table, in the shape the completeness sweep reads it.</summary>
    static readonly Transcription[] Rows = [
        new("Hsl", "HueRotate", "Material/ComputeColor.rvn", "HueRotate", 15),
        new("Noise", "Hash", "Core/Random.rvn", "Hash", 3),
        new("Noise", "Combine", "Core/Random.rvn", "Combine", 1),
        new("Noise", "ToFloat01", "Core/Random.rvn", "ToFloat01", 2),
        new("FloodFill", "Hash", "Core/Random.rvn", "Hash", 3),
        new("FloodFill", "Combine", "Core/Random.rvn", "Combine", 1),
        new("FloodFill", "ToFloat01", "Core/Random.rvn", "ToFloat01", 2),
        new("Splatter", "Hash", "Core/Random.rvn", "Hash", 3),
        new("Splatter", "Mix", "Core/Random.rvn", "Combine", 1),
        new("Splatter", "ToFloat01", "Core/Random.rvn", "ToFloat01", 2),
        new("TileSampler", "Hash", "Core/Random.rvn", "Hash", 3),
        new("TileSampler", "Mix", "Core/Random.rvn", "Combine", 1),
        new("TileSampler", "ToFloat01", "Core/Random.rvn", "ToFloat01", 2)
    ];

    /// <summary>
    ///     Every transcribed function still computes what the library function it was copied from does.
    /// </summary>
    /// <param name="kernel">The kernel carrying the copy.</param>
    /// <param name="copy">What the copy is called there.</param>
    /// <param name="libraryFile">The library source, under <c>Raven/Library</c>.</param>
    /// <param name="original">What the original is called.</param>
    /// <param name="numbers">How many non-zero literals both are expected to have.</param>
    /// <remarks>
    ///     <para>
    ///         <see href="https://github.com/Rikarin/Vixen/issues/635">#635</see>, widened from the
    ///         one function the issue names to every copy in the assembly. ⚠ <b>The failure mode is
    ///         not a compile error, it is a disagreement</b>, and it has no symptom until an artist
    ///         matches a hue in one editor and watches it shift in the other, or until a noise field
    ///         baked here stops matching the same noise in a material graph. Nothing would fail when
    ///         one copy is edited. This is that nothing, filled in — it is not the fix the issue asks
    ///         for and it is what makes the fix optional rather than urgent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The prelude the issue's second answer proposes would not remove the need for
    ///         this.</b> An embedded prelude puts one copy inside the texture graph instead of one
    ///         per kernel, which is a real improvement to thirteen rows; it is still a second copy
    ///         of the arithmetic, and the thing that has to exist either way is something that goes
    ///         red when the two disagree.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Parity))]
    public void Every_transcription_of_the_library_still_matches_its_original(
        string kernel,
        string copy,
        string libraryFile,
        string original,
        int numbers
    ) {
        var mine = Reduce(TextureKernels.Source(kernel), copy);
        var theirs = Reduce(Library(libraryFile.Split('/')), original);

        // The instrument, twice over. Two arithmetics read as nothing are equal, and an extractor
        // that walked past a `=>` body and returned the next function's block would hand back a
        // plausible-looking one — so the size is stated by the row rather than inferred from either
        // file, and both sides are held to it.
        Assert.Equal(numbers, theirs.Numbers.Length);
        Assert.Equal(numbers, mine.Numbers.Length);
        Assert.NotEmpty(theirs.Calls.Concat(theirs.Operators));

        Assert.Equal(theirs.Numbers, mine.Numbers);
        Assert.Equal(theirs.Operators, mine.Operators);
        Assert.Equal(theirs.Calls, mine.Calls);
    }

    /// <summary>
    ///     The kernels that copy <c>Random.rvn</c> are exactly the kernels the parity table names.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A table of copies is a gate only while it is complete, and nothing else in this
    ///         repository would notice a sixth kernel reaching for the hash.</b> Four already do —
    ///         <c>Noise</c> came first, then <c>FloodFill</c>, <c>Splatter</c> and
    ///         <c>TileSampler</c>, each in a different batch, each with a comment saying it copied
    ///         because it could not import. A fifth will be written the same way. This is what makes
    ///         that land red on the day it lands rather than on the day the copies disagree.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The three constants it looks for are read out of <c>Random.rvn</c>, not written
    ///         here.</b> A needle written into the test is a fourteenth copy of the thing the test
    ///         exists to count, and one that would go on matching after the library changed.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_kernel_that_copies_Random_rvn_is_in_the_parity_table() {
        var random = Library("Core", "Random.rvn");
        var declared = Declared(random);

        // The golden-ratio operand is a bare literal in the library and a named constant in every
        // kernel, so it is read out of `Combine`'s body rather than out of the declarations.
        var golden = Number.Match(Function(random, "Combine"));

        Assert.True(golden.Success, "`Random.Combine` has no literal in it — the library moved under this test");

        string[] needles = [declared["Multiplier"], declared["InvTwo24"], golden.Value];

        // The instrument: three distinct needles, each of which really is in the file they were read
        // from. A sweep whose needles were empty strings would report every kernel; one whose needles
        // were nothing would report none, and reporting none is what "no drift" looks like.
        Assert.Equal(3, needles.Distinct(StringComparer.Ordinal).Count());
        Assert.All(needles, needle => Assert.NotEmpty(needle));
        Assert.All(needles, needle => Assert.Contains(needle, random, StringComparison.OrdinalIgnoreCase));

        string[] copying = [
            .. TextureKernels
                .Names
                .Where(kernel =>
                    needles.Any(needle =>
                        TextureKernels.Source(kernel).Contains(needle, StringComparison.OrdinalIgnoreCase)
                    )
                )
                .OrderBy(kernel => kernel, StringComparer.Ordinal)
        ];

        string[] covered = [
            .. Rows
                .Where(row => row.LibraryFile.EndsWith("Random.rvn", StringComparison.Ordinal))
                .Select(row => row.Kernel)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(kernel => kernel, StringComparer.Ordinal)
        ];

        Assert.NotEmpty(covered);

        Assert.True(
            copying.SequenceEqual(covered, StringComparer.Ordinal),
            $"These kernels carry `Random.rvn`'s constants: {string.Join(", ", copying)}.\n"
            + $"These are the ones the parity table holds: {string.Join(", ", covered)}.\n"
            + "A copy nothing compares is a copy that drifts silently — #635. Add the kernel's `Hash`, "
            + "`Combine`/`Mix` and `ToFloat01` to `Rows`, or make it import once a kernel can."
        );
    }

    /// <summary>
    ///     <c>Checker.rvn</c> still folds the cell the way <c>ComputeColor.Checker</c> does.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The one transcription that is not a function, so it is the one row the table above
    ///     cannot hold.</b> `Checker.rvn` folds the library's two lines into its own `Main` around a
    ///     rotation and an offset the library's has no parameter for, so there is no `func Checker`
    ///     here to reduce. What survives verbatim is the return expression — the parity of a
    ///     checkerboard is entirely in `mod(… , 2f)` — and that is what is compared, read out of the
    ///     library rather than written down.
    /// </remarks>
    [Fact]
    public void The_transcribed_checkerboard_still_folds_the_cell_the_same_way() {
        var body = Function(Library("Material", "ComputeColor.rvn"), "Checker");
        var at = body.IndexOf("return ", StringComparison.Ordinal);

        Assert.True(at >= 0, "`ComputeColor.Checker` has no `return` — the library moved under this test");

        var expression = body[(at + "return ".Length)..].Trim().TrimEnd('}').Trim();

        // The instrument. An empty expression is contained in every string, so a `Function` that
        // returned the wrong span would pass silently; the fold is what the comparison is about and
        // it has to be in there.
        Assert.Contains("mod(", expression, StringComparison.Ordinal);

        Assert.Contains(expression, TextureKernels.Source("Checker"), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The kernels that weight a luminance still weight it the way the shader library does.
    /// </summary>
    /// <param name="kernel">The kernel carrying the copy.</param>
    /// <param name="declaration">What the weights look like where that kernel writes them.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The third shape a transcription takes, and the one that reads least like a
    ///         copy.</b> <c>Grayscale.rvn</c> writes Rec. 709 as three separate parameter
    ///         *defaults* and <c>Hsl.rvn</c> writes it as a <c>float3</c> inside <c>Main</c> —
    ///         neither is a function, so neither can be a row of <see cref="Parity" />, and both
    ///         carry a header saying in as many words that they use these three numbers "so a graph
    ///         and a shader graph agree about what grey is". Three numbers agreeing is the whole of
    ///         that claim, so three numbers is what is checked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Each row carries the pattern that finds the weights, and the first version of
    ///         this test did not — which is why it is written this way.</b> It asked only whether
    ///         the three literals appeared anywhere in the file, and drifting
    ///         <c>Grayscale</c>'s <c>weightG</c> default left it green: the kernel writes the same
    ///         triple a second time as the fallback for a zero-sum weight set, and a search over the
    ///         whole file cannot tell the declaration from the fallback. So the pattern names where
    ///         the weights are declared, and every number it finds has to be the library's, in
    ///         order.
    ///     </para>
    ///     <para>
    ///         ⚠ Rec. 601's 0.299 / 0.587 / 0.114 is the other common answer and differs by up to 6%
    ///         on saturated green, which is what makes a silent drift here a colour bug rather than
    ///         a rounding one. That triple is checked too — it is <c>HueRotate</c>'s first three
    ///         constants, and the table above holds it.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("Grayscale", @"var weight[RGB]: float = \S+")]
    [InlineData("Hsl", @"dot\(rotated, float3\([^)]*\)")]
    public void The_luminance_weights_are_still_the_library_s(string kernel, string declaration) {
        var luminance = Reduce(Library("Material", "ComputeColor.rvn"), "Saturation").Numbers;

        // The instrument, one half. A `Saturation` that moved would reduce to nothing, and two empty
        // sequences are equal.
        Assert.Equal(3, luminance.Length);

        var matches = Regex.Matches(Uncommented(TextureKernels.Source(kernel)), declaration);

        // The other half, and the one this test failed on first: a pattern that matched nothing
        // finds no wrong weights either. The header of both kernels also writes the triple in prose,
        // which is why the comments are stripped before the pattern runs.
        Assert.NotEmpty(matches);

        string[] declared = [
            .. matches
                .SelectMany(match => Number.Matches(match.Value).Select(number => Value(number.Value)))
                .Select(value => value.ToString("R", CultureInfo.InvariantCulture))
        ];

        Assert.Equal(luminance, declared);
    }
}
