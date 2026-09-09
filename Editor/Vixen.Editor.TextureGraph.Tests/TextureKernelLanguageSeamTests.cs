// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using Vixen.Editor.TextureGraph;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     What a texture-graph kernel can and cannot say, and which half of that is the language's.
/// </summary>
/// <remarks>
///     <para>
///         <b>A kernel is compiled by <c>TexturePlanEvaluator.VariantFor</c> through
///         <see cref="TextureKernelPrelude.Compile" />: the kernel, the shader-library sources, one
///         <c>EffectKey</c>, and no defines.</b> So it <em>can</em> <c>import</c>
///         (<see href="https://github.com/Rikarin/Vixen/issues/635">#635</see>) and it still cannot
///         declare a <c>[Permutation]</c>
///         (<see href="https://github.com/Rikarin/Vixen/issues/638">#638</see>).
///     </para>
///     <para>
///         ⚠ <b>The first half of that sentence was false for eighteen batches and the diagnosis was
///         the reason.</b> This file, #635, five kernel headers and two suites all said a kernel
///         "binds against nothing but itself" because <c>FromSources</c> was passed no
///         <c>referencePaths</c> — and offered a compiled <c>.rvnlib</c> or a hand-written prelude as
///         the two ways out. Neither was needed. <c>FromSources</c> takes a <em>set</em> of texts and
///         makes them one compilation, and a package's declarations are visible across one
///         compilation; the evaluator was passing one text. Thirteen transcriptions were written to
///         work around a restriction that was a property of the call. They are gone: the four copies
///         of <c>Random</c>'s hash, the hue rotation, and — with
///         <see href="https://github.com/Rikarin/Vixen/issues/1033">#1033</see> — <c>Blend</c>'s
///         overlay, hard light and soft light are all calls now.
///     </para>
///     <para>
///         ⚠ <b>A permutation the plan cannot name is still not a compile error, and that is why the
///         rest of this file exists.</b> It compiles perfectly and silently takes its <c>.rvn</c>
///         default in every op for ever — this repository's registered-permutation trap arriving
///         from the side with no key list at all.
///     </para>
///     <para>
///         ⚠ <b>Three transcriptions survive, for reasons that are not about imports.</b>
///         <c>Grayscale</c> writes Rec. 709 as three parameter <em>defaults</em>, which must be
///         literals; <c>Hsl</c>, <c>Splatter</c> and <c>TileSampler</c> write it inline as a
///         <c>float3</c>; and <c>Checker</c> folds the library's two lines into its own <c>Main</c>
///         around a rotation the library's has no parameter for. Each has its own gate below, and
///         <see cref="No_kernel_transcribes_the_library_s_hash_or_hue_rotation" /> refuses the
///         return of the eleven that went.
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

        // ⚠ And the prelude, which is the half #635 created. Every kernel is now compiled beside
        // `Raven/Library`'s sources, so a `[Permutation]` in one of *those* takes its default in
        // every op of every plan — the same defect as a kernel's own, arriving through a file this
        // assembly did not write and reaching every kernel rather than one. The library is
        // free to declare permutations for the shader graph, which binds keys; what it may not do
        // is declare one in a file the texture graph puts in its compilation.
        Assert.NotEmpty(TextureKernelPrelude.Sources);

        foreach (var (name, source) in TextureKernelPrelude.Sources) {
            Assert.True(
                !Declaration.IsMatch(source),
                $"`{name}` is in every kernel's compilation and declares a `[Permutation]`. Nothing "
                + "between a plan and the compiler can give one a value, so every kernel would take "
                + "its default silently — #638. Either the library file comes out of "
                + "`TextureKernelPrelude`, or the permutation comes out of the library file."
            );
        }

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
    ///         <c>[Permutation] val Fancy</c> through the *same* call the evaluator makes —
    ///         <see cref="TextureKernelPrelude.Compile" />, so the library beside it and no defines —
    ///         and reads what comes back. It compiles. There is no diagnostic, at any severity.
    ///         Nothing at this call site could have said which branch it wanted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It went through <c>FromSources</c> directly until #635, and the remark above said
    ///         "the same call the evaluator makes" while it did.</b> That was true when the evaluator
    ///         passed one text and stopped being true the moment it passed four — a fixture whose
    ///         claim to be the production call is written in prose rather than in the call.
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
        var data = TextureKernelPrelude.Compile("Chosen.rvn", Declaring).TryGet(EffectKey.Of("Chosen"));

        // ⚠ Not an error, not a warning, not a missing effect. This is the entire finding: the
        // language accepted a switch the layer above has no way to throw.
        Assert.NotNull(data);
        Assert.Equal("Chosen", data.ShaderName);

        var chosen = Assert.Single(data.Stages);

        Assert.NotEmpty(chosen.Bytecode);

        var flipped = TextureKernelPrelude
            .Compile(
                "Chosen.rvn",
                Declaring.Replace("Fancy: bool = false", "Fancy: bool = true", StringComparison.Ordinal)
            )
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

    /// <summary>
    ///     No kernel carries its own copy of <c>Random</c>'s hash or <c>ComputeColor</c>'s hue rotation.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This used to be <c>Every_kernel_that_copies_Random_rvn_is_in_the_parity_table</c>
    ///         — a completeness check over a table of eleven live copies.</b> The table is gone
    ///         because the copies are: a kernel is compiled beside the library now, so
    ///         <c>Random.Hash</c> and <c>ComputeColor.HueRotate</c> are calls. The sweep is the same
    ///         sweep with the expectation inverted, which is the strictly stronger gate — it says
    ///         <em>none</em> rather than <em>these, and they still agree</em>, so a sixth kernel
    ///         reaching for the constants lands red on the day it lands and there is no correct
    ///         second copy for it to be added beside.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The needles are read out of the library, not written here.</b> A needle written
    ///         into a test is another copy of the thing the test exists to count, and one that would
    ///         go on matching after the library changed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Rec. 709 is deliberately not a needle here.</b> <c>Grayscale</c> legitimately
    ///         writes those three numbers — a parameter default cannot be a call — so sweeping every
    ///         constant <c>ComputeColor</c> declares would fail on the one transcription this file
    ///         still holds a gate for. The needles are the YIQ matrix, which nothing but a copy of
    ///         <c>HueRotate</c> has a reason to contain. Rec. 709 gets its own sweep, with that one
    ///         exclusion named:
    ///         <see cref="Only_the_kernel_whose_weights_are_defaults_still_transcribes_the_library_s_luminance" />.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_kernel_transcribes_the_library_s_hash_or_hue_rotation() {
        var random = Library("Core", "Random.rvn");
        var declared = Declared(random);

        // The golden-ratio operand is a bare literal in the library, so it is read out of
        // `Combine`'s body rather than out of the declarations.
        var golden = Number.Match(Function(random, "Combine"));

        Assert.True(golden.Success, "`Random.Combine` has no literal in it — the library moved under this test");

        // The six YIQ coefficients of the chroma rotation, less the Rec. 601 luminance triple that
        // opens `HueRotate` and the two 1s the rotation itself carries.
        string[] rotation = [
            .. Reduce(Library("Material", "ComputeColor.rvn"), "HueRotate")
                .Numbers
                .Skip(3)
                .Where(number => number != "1")
        ];

        string[] needles = [declared["Multiplier"], declared["InvTwo24"], golden.Value, .. rotation];

        // The instrument: distinct needles, each of which really is in the file it was read from. A
        // sweep whose needles were empty strings would report every kernel; one whose needles were
        // nothing would report none, and reporting none is exactly what "no copies" looks like.
        Assert.Equal(needles.Length, needles.Distinct(StringComparer.Ordinal).Count());
        Assert.True(needles.Length > 3, "the hue rotation reduced to nothing — `ComputeColor` moved under this test");
        Assert.All(needles, needle => Assert.NotEmpty(needle));
        Assert.All(
            needles.Take(3),
            needle => Assert.Contains(needle, random, StringComparison.OrdinalIgnoreCase)
        );

        string[] copying = [
            .. TextureKernels
                .Names
                .Where(kernel =>
                    needles.Any(needle =>
                        Uncommented(TextureKernels.Source(kernel))
                            .Contains(needle, StringComparison.OrdinalIgnoreCase)
                    )
                )
                .OrderBy(kernel => kernel, StringComparer.Ordinal)
        ];

        Assert.True(
            copying.Length == 0,
            $"These kernels carry the library's own constants: {string.Join(", ", copying)}.\n"
            + "A kernel is compiled beside `Raven/Library` — see `TextureKernelPrelude` — so there is "
            + "nothing to copy: write `import Vixen.Shaders.Core` or `import Vixen.Shaders.Material` "
            + "and call `Random.Hash` / `ComputeColor.HueRotate`. #635."
        );
    }

    /// <summary>
    ///     <c>Blend</c>'s overlay, hard light and soft light are the library's, and hard light is
    ///     still overlay with its operands swapped.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see href="https://github.com/Rikarin/Vixen/issues/1033">#1033</see>, closed by
    ///         removal rather than by a table row. Both modes lived inside <c>Combine</c>'s
    ///         <c>if (mode == N)</c> chain as <c>float4</c> arithmetic where the library is
    ///         <c>float3</c>, which is why no reduction of numbers, operators and calls could hold
    ///         them. They are calls now, so what is left to check is that they are the right calls.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The swap is asserted where it now lives, and it is the one thing no sequence
    ///         comparison could ever have seen.</b> <c>ComputeColor.HardLight</c> is
    ///         <c>Overlay(over, under)</c>: the two halves of overlay are symmetric in their
    ///         operands, so a hard light written as <c>Overlay(under, over)</c> is <em>exactly</em>
    ///         overlay, on every image, with no artefact to notice — identical numbers, identical
    ///         operators, identical calls. Reading the argument order against the parameter order is
    ///         the only form that goes red for it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_blend_kernel_calls_the_library_and_hard_light_still_swaps_its_operands() {
        var kernel = Uncommented(TextureKernels.Source("Blend"));

        Assert.Contains("import Vixen.Shaders.Material", kernel, StringComparison.Ordinal);

        foreach (var mode in (string[])["Overlay", "HardLight", "SoftLight"]) {
            Assert.Contains($"ComputeColor.{mode}(", kernel, StringComparison.Ordinal);
        }

        // And the arithmetic is gone rather than sitting beside the call, which is the arrangement
        // that satisfies every check about the call and keeps the copy.
        Assert.DoesNotContain("sqrt(max(a,", kernel, StringComparison.Ordinal);

        var library = Uncommented(Library("Material", "ComputeColor.rvn"));
        var at = library.IndexOf("func HardLight(", StringComparison.Ordinal);

        Assert.True(at >= 0, "`ComputeColor.HardLight` is gone — the library moved under this test");

        var open = library.IndexOf('(', at) + 1;
        var close = library.IndexOf(')', open);

        string[] parameters = [
            .. library[open..close].Split(',').Select(part => part.Split(':')[0].Trim())
        ];

        // The instrument: two parameters, named, before anything is compared. A parse that came back
        // with one name or none would make the reversal below vacuous.
        Assert.Equal(2, parameters.Length);
        Assert.All(parameters, name => Assert.NotEmpty(name));

        var body = Function(library, "HardLight");
        var call = body.IndexOf("Overlay(", StringComparison.Ordinal);

        Assert.True(call >= 0, "`ComputeColor.HardLight` no longer calls `Overlay` — it is a hard light by itself now");

        var args = body[(call + "Overlay(".Length)..];

        string[] passed = [.. args[..args.IndexOf(')')].Split(',').Select(part => part.Trim())];

        Assert.Equal((string[])[parameters[1], parameters[0]], passed);
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

        // ⚠ `Uncommented`, and it is the assertion rather than tidiness. `Checker.rvn`'s header
        // says what this test holds and quotes the fold to say it, so searching the raw source
        // found the needle in a comment: the live line at `Checker.rvn:52` could be changed to
        // anything and this stayed green. A parity check that its own subject's prose satisfies is
        // the "instrument that cannot fail" this repository keeps finding.
        Assert.Contains(expression, Uncommented(TextureKernels.Source("Checker")), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The kernels that weight a luminance still weight it the way the shader library does.
    /// </summary>
    /// <param name="kernel">The kernel carrying the copy.</param>
    /// <param name="declaration">What the weights look like where that kernel writes them.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One row, and it used to be four.</b> <c>Hsl</c>, <c>Splatter</c> and
    ///         <c>TileSampler</c> wrote the triple inline in a body; all three call
    ///         <c>ColorSpaces.Luminance</c> now — <see cref="Three_kernels_read_their_luminance_out_of_the_library" />
    ///         is what holds that, and this is the one copy that stays.
    ///         <c>Grayscale.rvn</c> writes Rec. 709 as three separate parameter *defaults*, which is
    ///         not a function, so it can be neither a row of <see cref="Parity" /> nor a call: ⚠ a
    ///         parameter default has to be a literal. Its header says it uses these three numbers "so
    ///         a graph and a shader graph agree about what grey is". Three numbers agreeing is the
    ///         whole of that claim, so three numbers is what is checked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The library half is <c>ColorSpaces.Luminance</c> and it used to be
    ///         <c>ComputeColor.Saturation</c>'s body.</b> Both hold the same triple, and the second
    ///         has <em>no callers</em>: no <c>.rvn</c> in the tree calls it and no shader-graph node
    ///         emits it — <c>Tonemap.rvn</c> grades saturation with an implementation of its own. So
    ///         this test pinned a kernel against numbers no shader in the engine reads, which is the
    ///         weaker of the two anchors even where the numbers agree
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1093">#1093</a>).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pattern names where the weights are declared, and the first version of this
    ///         test did not — which is why it is written this way.</b> It asked only whether the
    ///         three literals appeared anywhere in the file, and drifting <c>Grayscale</c>'s
    ///         <c>weightG</c> default left it green: the kernel writes the same triple a second time
    ///         as the fallback for a zero-sum weight set, and a search over the whole file cannot
    ///         tell the declaration from the fallback.
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
    public void The_luminance_weights_are_still_the_library_s(string kernel, string declaration) {
        var luminance = Reduce(Library("Core", "ColorSpaces.rvn"), "Luminance").Numbers;

        // The instrument, one half. A `Luminance` that moved would reduce to nothing, and two empty
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

    /// <summary>
    ///     The three kernels that weight a luminance in a body call the library rather than
    ///     transcribing it, and no kernel but <c>Grayscale</c> carries the triple at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see href="https://github.com/Rikarin/Vixen/issues/1093">#1093</see>, and it is the
    ///         same inversion <see cref="No_kernel_transcribes_the_library_s_hash_or_hue_rotation" />
    ///         made: a sweep saying <em>none</em> rather than a table saying <em>these three, and
    ///         they still agree</em>. A fourth kernel reaching for the constants lands red on the day
    ///         it lands.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Grayscale</c> is the one exception and it is a structural one rather than a
    ///         grandfathered one.</b> Its three weights are parameter <em>defaults</em> and a default
    ///         has to be a literal, so it is the one place in this assembly where the numbers cannot
    ///         become a call whatever the library grows.
    ///         <see cref="The_luminance_weights_are_still_the_library_s" /> is what holds those.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The needle is read out of <c>ColorSpaces.rvn</c> and then checked against
    ///         <c>Grayscale</c> before the sweep runs.</b> A pattern that matched nothing would report
    ///         no copies, and "no copies" is exactly what this test says on the day it passes — so the
    ///         detector has to be shown finding the copy that is supposed to be there. That is the
    ///         instrument this file's own remarks keep asking for.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Only_the_kernel_whose_weights_are_defaults_still_transcribes_the_library_s_luminance() {
        var luminance = Reduce(Library("Core", "ColorSpaces.rvn"), "Luminance").Numbers;

        // The instrument, one half: a `Luminance` that moved reduces to nothing, and a needle built
        // from nothing matches every `float3()` in the assembly or none of them.
        Assert.Equal(3, luminance.Length);

        var triple = new Regex(
            @"float3\(\s*" + string.Join(@"f?\s*,\s*", luminance.Select(Regex.Escape)) + @"f?\s*\)"
        );

        // The other half. `Grayscale` writes the triple as the fallback for a zero-sum weight set,
        // so the detector has a known positive in the tree and a run where it stopped matching says
        // so here rather than by reporting a clean sweep.
        Assert.Matches(triple, Uncommented(TextureKernels.Source("Grayscale")));

        string[] copying = [
            .. TextureKernels
                .Names
                .Where(kernel => !string.Equals(kernel, "Grayscale", StringComparison.Ordinal))
                .Where(kernel => triple.IsMatch(Uncommented(TextureKernels.Source(kernel))))
                .OrderBy(kernel => kernel, StringComparer.Ordinal)
        ];

        Assert.True(
            copying.Length == 0,
            $"These kernels write Rec. 709 out rather than calling it: {string.Join(", ", copying)}.\n"
            + "`Core/ColorSpaces.rvn` is in `TextureKernelPrelude.Sources`, so a kernel writes "
            + "`import Vixen.Shaders.Core` and calls `ColorSpaces.Luminance`. #1093."
        );
    }

    /// <summary>
    ///     The set a kernel is compiled against declares the Rec. 709 weights exactly once, in
    ///     <c>Core/ColorSpaces.rvn</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><see href="https://github.com/Rikarin/Vixen/issues/1114" />, and it is the sweep
    ///         the three tests above cannot make.</b> Each of those asks a <em>kernel</em> not to
    ///         transcribe the triple; none of them asks the same of the library sources compiled
    ///         beside it, and the library was carrying a second copy —
    ///         <c>Material/ComputeColor.rvn</c>'s <c>Saturation</c>, whose body held the weights and
    ///         which nothing in the engine called. "No kernel copies it" was true and "a kernel binds
    ///         against one luminance" was not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The prelude and not <c>Raven/Library</c>, because the library legitimately holds
    ///         two.</b> <c>PostFx/Fullscreen.rvn</c> declares a <c>Luminance</c> of its own with the
    ///         same triple, and a post-effect chain and a texture kernel are not one compilation, so
    ///         a sweep over the whole library would be red on a duplication that costs nothing. What
    ///         matters is the set <see cref="TextureKernelPrelude.Compile" /> hands a kernel: two
    ///         spellings inside <em>that</em> are two answers a kernel could call.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it would say if it read nothing.</b> A prelude that came back empty finds no
    ///         copies at all and would report the library clean, so the file that is supposed to
    ///         carry the weights is named and asserted first — the same shape
    ///         <see cref="Only_the_kernel_whose_weights_are_defaults_still_transcribes_the_library_s" />
    ///         uses for <c>Grayscale</c>. ⚠ And the needle is read off <c>Core/ColorSpaces.rvn</c> on
    ///         disk rather than written here, so a triple that moved reduces to nothing and fails the
    ///         length check instead of matching every file.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_prelude_declares_the_luminance_weights_once() {
        var luminance = Reduce(Library("Core", "ColorSpaces.rvn"), "Luminance").Numbers;

        Assert.Equal(3, luminance.Length);

        var triple = new Regex(
            @"float3\(\s*" + string.Join(@"f?\s*,\s*", luminance.Select(Regex.Escape)) + @"f?\s*\)"
        );

        string[] carrying = [
            .. TextureKernelPrelude
                .Sources
                .Where(source => triple.IsMatch(Uncommented(source.Text)))
                .Select(source => source.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
        ];

        // The instrument's other half: the file that is meant to hold the weights has to be one of
        // the ones found, or "exactly one" is a statement about a pattern that matches nothing.
        Assert.Contains("Core.ColorSpaces.rvn", carrying);

        Assert.True(
            carrying.Length == 1,
            "These files in the set a kernel is compiled against each spell Rec. 709 out: "
            + string.Join(", ", carrying)
            + ".\nOne of them is `Core/ColorSpaces.rvn`'s `Luminance` and the rest are copies a "
            + "kernel could call by mistake. #1114."
        );
    }

    /// <summary>
    ///     <c>Core/ColorSpaces.rvn</c> is in what a kernel binds against, verbatim, and the three
    ///     kernels that want a luminance call it.
    /// </summary>
    /// <param name="kernel">The kernel whose body weighs a luminance.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The whole of <see href="https://github.com/Rikarin/Vixen/issues/1093">#1093</see>
    ///         was this list, and the claim that hid it was a claim about the wrong set.</b> Five
    ///         kernel headers and #1077 said the shader library had no
    ///         <c>Luminance(colour: float3): float</c> to call. It has had one at
    ///         <c>Core/ColorSpaces.rvn</c> since long before any of them — in the same package
    ///         <c>Vixen.Shaders.Core</c> the prelude already carried. "The library does not have it"
    ///         was a measurement of <see cref="TextureKernelPrelude.Sources" /> written as though it
    ///         were a measurement of <c>Raven/Library</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Byte-equal with the file on disk, which is the prelude's own claim and nothing
    ///         checked it.</b> The <c>EmbeddedResource</c> points at <c>Raven/Library/**</c> so that
    ///         editing the library edits what a kernel compiles against; a copy taken into this
    ///         assembly would satisfy every other assertion here and would drift silently.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("Hsl")]
    [InlineData("Splatter")]
    [InlineData("TileSampler")]
    public void The_kernels_that_weigh_a_luminance_call_the_library_s(string kernel) {
        var library = Library("Core", "ColorSpaces.rvn");

        Assert.Contains("func Luminance(", library, StringComparison.Ordinal);

        var embedded = Assert.Single(
            TextureKernelPrelude.Sources.Where(source =>
                string.Equals(source.Name, "Core.ColorSpaces.rvn", StringComparison.Ordinal)
            )
        );

        Assert.Equal(library.ReplaceLineEndings("\n"), embedded.Text.ReplaceLineEndings("\n"));

        Assert.Contains(
            "ColorSpaces.Luminance(",
            Uncommented(TextureKernels.Source(kernel)),
            StringComparison.Ordinal
        );
    }
}
