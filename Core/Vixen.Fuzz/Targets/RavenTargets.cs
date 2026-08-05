// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Text;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;

namespace Vixen.Fuzz.Targets;

/// <summary>A shader, taken as far through the compiler as it will go.</summary>
/// <remarks>
///     <para>
///         <b>The deepest target here by a distance, and the only one where "it did not crash" is
///         nearly worthless on its own.</b> Every pass in this pipeline is total by design — the lexer
///         and parser have no <c>throw</c> in them at all, the binder reports and continues, and a
///         backend that cannot express something says so through a diagnostic rather than emitting
///         something that will not compile. So a run watching only for exceptions is watching for
///         something the design says will not happen, and would come back clean while every one of
///         those passes quietly did the wrong thing.
///     </para>
///     <para>
///         What is checked instead is that each stage <i>agrees with itself</i>:
///     </para>
///     <list type="bullet">
///         <item>the tree reproduces the file, byte for byte, valid or not;</item>
///         <item>an incremental reparse equals a full one — the classic bug farm;</item>
///         <item>every diagnostic that escapes is a <c>Diagnostic</c> with an id the language owns;</item>
///         <item>and a module that generated is a module <c>spirv-val</c> accepts.</item>
///     </list>
///     <para>
///         ⚠ <b>The last one is the reason to do any of this, and it catches a class the others
///         cannot.</b> A backend defect does not have to produce a crash or an invalid module — it can
///         produce a <i>valid</i> one that means something else. An implicit-LOD sample in a compute
///         entry point had been silently substituting level zero since July: nothing threw, nothing
///         was reported, every golden file matched because they were regenerated from the same
///         emitter, and the shader was simply not the shader anybody wrote. A validity oracle over a
///         real validator is the only thing in this harness that is not marking its own homework.
///     </para>
/// </remarks>
public sealed class RavenTarget : IFuzzTarget {
    /// <summary>What the parse is told the file is called.</summary>
    /// <remarks>Fixed, because diagnostics carry it and two parses are compared on their diagnostics.</remarks>
    const string Path = "Fuzz.rvn";

    /// <inheritdoc />
    public string Name => "raven";

    /// <inheritdoc />
    public string What => "Raven parsed, reparsed, bound, lowered and generated, with the module validated";

    /// <inheritdoc />
    public IFuzzDomain Domain { get; } = new SyntaxDomain(
        "Raven shaders, one syntax node at a time",
        text => SyntaxTree.ParseText(text, path: Path).GetRoot(),
        RavenSeeds.Fragments,
        0x5241_5645_4E00_0805ul
    );

    /// <summary>Whether novelty is worth keeping an input for.</summary>
    /// <remarks>
    ///     No, and this is the target the question was asked for. A compiler's behaviour is every
    ///     combination of declarations, types, diagnostics and emitted instructions there is; the
    ///     signature table saturates in seconds and what it selected before then is arbitrary. The
    ///     domain reaches the passes that guidance was buying, by construction.
    /// </remarks>
    public bool NoveltyGuides => false;

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The largest multiple in this harness, and the honest reading of what a compiler
    ///         is.</b> A packet decoder's output is smaller than its input; a compiler's is a
    ///         syntax tree, a symbol table, a bound tree, an IR module and a SPIR-V module, each of
    ///         which is larger than the source that produced it. Two extra parses go on top for the
    ///         reparse oracle.
    ///     </para>
    ///     <para>
    ///         It is still a multiple, which is what makes it an assertion rather than a formality:
    ///         a monomorphiser that instantiates per call site instead of per signature, or an
    ///         inliner with no depth bound, turns a linear compiler into an exponential one — and a
    ///         shader is a file an artist can be handed, so that is a real failure and not a
    ///         theoretical one.
    ///     </para>
    /// </remarks>
    public long AllowanceFor(int inputLength) => (1L << 20) + (8192L * inputLength);

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        foreach (var source in RavenSeeds.Sources) {
            corpus.Add(Encoding.UTF8.GetBytes(source));
        }
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var text = Encoding.UTF8.GetString(input);
        var tree = SyntaxTree.ParseText(text, path: Path);
        var printed = tree.GetRoot().ToFullString();

        // ⚠ Byte-exact over arbitrary text, not only over shaders that compile — the parser fabricates
        // missing tokens zero-width and carries unusable source as trivia precisely so that this
        // holds for a broken file too. Which makes a mismatch here a *wrong parse* rather than a
        // crash: it is asserted next to the compiler over some seventy snippets and the shipped
        // library, so anything this finds is a shape none of those has.
        if (!string.Equals(printed, text, StringComparison.Ordinal)) {
            throw new InvalidOperationException($"The parse does not reproduce its source: {Where(text, printed)}.");
        }

        long signature = (17 * 31) + tree.Diagnostics.Count;

        foreach (var diagnostic in tree.Diagnostics) {
            signature = (signature * 31) + Check(diagnostic, "the parser");
        }

        signature = (signature * 31) + Reparse(tree, text);

        // Create does no work — it holds the trees and defers everything — so the pass under test is
        // GetDiagnostics, which forces declaration building, binding and flow analysis. That is the
        // deepest code any of this reaches and the only place it can take unbounded time.
        var compilation = Compilation.Create("Fuzz", tree);
        var diagnostics = compilation.GetDiagnostics();

        signature = (signature * 31) + diagnostics.Count;

        foreach (var diagnostic in diagnostics) {
            signature = (signature * 31) + Check(diagnostic, "the compiler");
        }

        return diagnostics.Count == 0 ? (signature * 31) + Generate(compilation) : signature;
    }

    /// <summary>Lowers, verifies and emits, and validates whatever came out.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only for a compilation with nothing to report, which is the same bar
    ///         <c>CodeGenTestBase</c> holds every backend test to.</b> Lowering a tree the binder
    ///         rejected means lowering a tree with error types in it, and a crash there would be a
    ///         finding about a pass being run on input no compiler would give it.
    ///     </para>
    ///     <para>
    ///         <b>That also makes this the rarest path in the run, and rarity is the point rather
    ///         than a limitation.</b> A mutant that still compiles clean is a mutant one edit away
    ///         from a shader somebody wrote — which is exactly the population where an emitter
    ///         quietly substitutes something, because it is the population the emitter has a path
    ///         for. A mutant that does not compile tells the backend nothing it did not already know.
    ///     </para>
    /// </remarks>
    static long Generate(Compilation compilation) {
        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        IrVerifier.Verify(module, bag);

        var lowered = bag.ToArray().Length;

        if (TargetBackends.Create("spirv") is not { } backend) {
            throw new InvalidOperationException("TargetBackends no longer knows what 'spirv' is.");
        }

        var generated = backend.Generate(module, bag);
        long signature = 17;

        foreach (var diagnostic in bag.ToArray()) {
            signature = (signature * 31) + Check(diagnostic, "the backend");
        }

        signature = (signature * 31) + lowered;
        signature = (signature * 31) + generated.Count;

        foreach (var unit in generated) {
            signature = (signature * 31) + (int)unit.Stage;
            signature = (signature * 31) + (unit.Binary?.Length ?? -1);

            if (unit.Binary is { Length: > 0 } binary) {
                Spirv.Validate(unit.Name, binary);
            }
        }

        return signature;
    }

    /// <summary>Edits the file and requires the incremental reparse to equal a full one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ The edit is a pure function of the input's own bytes, not of where the run had got
    ///         to. A finding carries the input and nothing else, so an oracle that consulted the
    ///         generator would hand somebody a corpus file that does not reproduce it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="Shape" /> is the check that earns this its keep, and the printed
    ///         comparison above it would have come back clean.</b> Two of the corpus entries are
    ///         reparses that built a <i>different tree</i> and printed the same characters:
    ///         <c>raven/f756b11be5af90ca.bin</c> renames an enum to the <c>shader</c> keyword, and
    ///         <c>raven/6adb822d47389491.bin</c> eats an enum header entirely — either way the
    ///         members underneath keep their text, lex token for token as they did, and land where
    ///         the member grammar is reading, so the blender lent an enum member to a loop that
    ///         cannot parse one. Both took forty thousand cases to reach, well past the fifteen
    ///         hundred the gate runs, which is what the corpus is for.
    ///     </para>
    /// </remarks>
    static long Reparse(SyntaxTree tree, string text) {
        if (tree.Text is not { } source) {
            return 0;
        }

        var choice = Corpus.Fingerprint(Encoding.UTF8.GetBytes(text));
        var start = (int)(choice % (ulong)(text.Length + 1));
        var length = (int)((choice >> 21) % (ulong)(text.Length - start + 1));
        var inserted = Insertions[(int)((choice >> 42) % (ulong)Insertions.Length)];

        var edited = source.WithChanges(new TextChange(new(start, length), inserted));
        var incremental = tree.WithChangedText(edited);
        var full = SyntaxTree.ParseText(edited.ToString(), path: Path);

        var one = incremental.GetRoot();
        var two = full.GetRoot();

        if (!string.Equals(one.ToFullString(), two.ToFullString(), StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"An incremental reparse printed something a full parse did not, "
                + $"after replacing [{start}, {start + length}) with '{inserted}'."
            );
        }

        if (Shape(one, 0) != Shape(two, 0)) {
            throw new InvalidOperationException(
                $"An incremental reparse built a different tree than a full parse, "
                + $"after replacing [{start}, {start + length}) with '{inserted}'."
            );
        }

        if (incremental.Diagnostics.Count != full.Diagnostics.Count) {
            throw new InvalidOperationException(
                $"An incremental reparse reported {incremental.Diagnostics.Count} diagnostics where a full parse "
                + $"reported {full.Diagnostics.Count}, after replacing [{start}, {start + length}) with '{inserted}'."
            );
        }

        // Raven's tree does not count what it reused, unlike VXML's, so the fold is over whether an
        // edit that changed nothing was recognised as changing nothing.
        return ReferenceEquals(incremental, tree) ? 1 : 2;
    }

    /// <summary>What the edit puts in, chosen to make the tokens after it read differently.</summary>
    /// <remarks>
    ///     A reparser keeps a subtree when the new token stream reads the old characters the same
    ///     way, so an edit that inserts a letter exercises the fast path and nothing else. A brace, a
    ///     quote or a comment opener is what forces the decision this is checking.
    /// </remarks>
    static readonly string[] Insertions =
        ["", "{", "}", "(", ")", "\"", ";", "//", "/*", "*/", " ", "\n", "shader ", "float "];

    /// <summary>Holds one diagnostic to being a diagnostic, and folds it into the signature.</summary>
    /// <remarks>
    ///     ⚠ <b>The id's shape is worth checking rather than assuming.</b> An id is what a
    ///     suppression file names, what a documentation page is keyed on and what a build log is
    ///     grepped for; one built by concatenation somewhere, or a descriptor left with a
    ///     placeholder, is a diagnostic none of those three can refer to. There are a hundred and
    ///     twenty-three of them across five classes and no test asserts the shape of the set.
    /// </remarks>
    static long Check(Diagnostic diagnostic, string who) {
        var id = diagnostic.Descriptor.Id;

        if (!IsRavenId(id)) {
            throw new InvalidOperationException($"{who} reported '{id}', which is not a Raven diagnostic id.");
        }

        // The message is formatted rather than stored, so a descriptor whose format string does not
        // match the arguments it is given throws here rather than in whatever tries to print it.
        _ = diagnostic.GetMessage();

        return (Fold(id) * 31) + (int)diagnostic.Severity;
    }

    static bool IsRavenId(string id) =>
        id.Length == 7
        && id.StartsWith("RVN", StringComparison.Ordinal)
        && id[3] is >= '1' and <= '5'
        && char.IsAsciiDigit(id[4])
        && char.IsAsciiDigit(id[5])
        && char.IsAsciiDigit(id[6]);

    /// <summary>A number standing for the tree's shape, folded over the slots.</summary>
    /// <remarks>
    ///     Token text as well as kind: two trees of identical shape whose tokens differ is exactly
    ///     what a reparser produces when it keeps a subtree it should have rebuilt, so folding kinds
    ///     alone would miss the thing this is here to catch.
    /// </remarks>
    static long Shape(SyntaxNode node, int depth) {
        // Bounded, because a mutant nests arbitrarily and this runs on the case's own stack. A stack
        // overflow in the fuzzer is a run that reports nothing at all.
        if (depth > 128) {
            return 1;
        }

        long shape = node.RawKind;

        if (node is SyntaxToken token) {
            shape = (shape * 31) + (token.IsMissing ? 1 : 2);

            return (shape * 31) + Fold(token.Text);
        }

        shape = (shape * 31) + node.SlotCount;

        for (var i = 0; i < node.SlotCount; i++) {
            shape = (shape * 31) + (node.GetSlot(i) is { } child ? Shape(child, depth + 1) : 0);
        }

        return shape;
    }

    /// <summary>A string as a number, without <c>GetHashCode</c>, which is randomised per process.</summary>
    static long Fold(string text) {
        long folded = 17;

        foreach (var character in text) {
            folded = (folded * 31) + character;
        }

        return folded;
    }

    static string Where(string expected, string actual) {
        var shared = Math.Min(expected.Length, actual.Length);

        for (var i = 0; i < shared; i++) {
            if (expected[i] != actual[i]) {
                return $"first difference at {i}, expected '{expected[i]}' and printed '{actual[i]}'";
            }
        }

        return $"identical for {shared} characters, then {expected.Length} against {actual.Length}";
    }
}

/// <summary>Runs <c>spirv-val</c> over a generated module.</summary>
/// <remarks>
///     <para>
///         <b>The one oracle here that is not the compiler checking itself.</b> Everything else in
///         this target compares two things Vixen wrote; this asks Khronos's own validator whether the
///         module is a module. That is what makes it able to catch a backend that emitted something
///         <i>valid and wrong</i> — the class no crash-finder finds, and the class the
///         implicit-LOD-in-a-compute-stage defect belonged to.
///     </para>
///     <para>
///         ⚠ <b>The target environment is picked from the module's own header, not hardcoded.</b> A
///         ray-query module is emitted as SPIR-V 1.4, and validating that against <c>vulkan1.0</c>
///         reports the version rather than the module — a green run that checked nothing. Same rule
///         as <c>SpirvTestBase.Validate</c>, which is where it was worked out.
///     </para>
///     <para>
///         ⚠ <b>Absence of the validator is not a silent skip.</b> The precedent is
///         <c>SpirvBackendTests.The_validator_is_installed_so_these_tests_mean_something</c>: a skip
///         would make every case vacuous while the run still printed "clean". Here the harness cannot
///         fail a test from inside a target, so absence is recorded and
///         <c>TheSpirvValidatorIsInstalled</c> beside the gate is what says so out loud. CI installs
///         it on both legs.
///     </para>
///     <para>
///         <b>It ran quarantined for exactly as long as it took to fix what it found, and that is the
///         whole of the story worth keeping.</b> Its first run turned up two one-token edits of
///         <c>Example2.rvn</c> that compiled with no diagnostic at all and emitted modules a driver
///         would reject: a <c>bool</c> in a uniform block, and <c>OpConstantNull</c> of <c>void</c>.
///         Both are fixed in the front end — <c>RVN2137</c> refuses a boolean binding and
///         <c>RVN2030</c> refuses a call to a namespace — both inputs are committed under
///         <c>Corpus/raven</c>, and both now replay on every build. There is no switch: an oracle
///         with an off position is an oracle somebody turns off.
///     </para>
/// </remarks>
public static class Spirv {
    static readonly Lazy<string?> Tool = new(() => Find("spirv-val"));

    /// <summary>Whether the validator was found, so a test can say if it was not.</summary>
    public static bool Available => Tool.Value is not null;

    /// <summary>Validates a module, or does nothing if there is no validator.</summary>
    /// <param name="name">What the unit is called, for the message.</param>
    /// <param name="binary">The module.</param>
    /// <exception cref="InvalidOperationException">The validator rejected it.</exception>
    /// <remarks>
    ///     ⚠ <b>Down a pipe rather than through a temporary file</b>, which <c>spirv-val</c> supports
    ///     by naming the path <c>-</c>. That is faster, leaves nothing behind when a case is killed
    ///     mid-run, and keeps this off the host filesystem — which <c>VXIO0001</c> reserves for
    ///     <c>Vixen.Platform</c>, the editor and the tools, and which this is neither of.
    /// </remarks>
    public static void Validate(string name, byte[] binary) {
        if (Tool.Value is not { } tool) {
            return;
        }

        // Header word 1 is the version. Anything from 1.4 needs an environment that knows about it —
        // a ray-query module validated as vulkan1.0 reports its version rather than its contents,
        // which is a green run that checked nothing. SpirvTestBase is where this was worked out.
        var environment = binary.Length >= 8 && BitConverter.ToUInt32(binary, 4) >= 0x0001_0400
            ? "vulkan1.1spv1.4"
            : "vulkan1.0";

        using var process = Process.Start(
            new ProcessStartInfo(tool) {
                ArgumentList = { "--target-env", environment, "-" },
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        ) ?? throw new InvalidOperationException($"Could not start {tool}.");

        using (var input = process.StandardInput.BaseStream) {
            input.Write(binary, 0, binary.Length);
        }

        var complaint = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0) {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"spirv-val rejected the {binary.Length:N0} B module generated for '{name}' at "
                    + $"{environment}: {complaint.Trim()}"
                )
            );
        }
    }

    /// <summary>Finds a tool on the path, or in the two places a package manager puts it.</summary>
    /// <remarks>
    ///     The fallbacks are Homebrew's and the usual local prefix, matching
    ///     <c>SpirvTestBase.FindTool</c>: a test host does not always inherit an interactive shell's
    ///     <c>PATH</c>, and a validator that is installed but not found is the silent skip this is
    ///     trying not to have.
    /// </remarks>
    static string? Find(string name) {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':')
            .Concat(["/opt/homebrew/bin", "/usr/local/bin"]);

        foreach (var directory in directories) {
            if (string.IsNullOrEmpty(directory)) {
                continue;
            }

            var candidate = $"{directory.TrimEnd('/')}/{name}";

            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>Real shaders for the corpus, and snippets for the domain to build from.</summary>
/// <remarks>
///     ⚠ <b>The whole files come out of <c>Raven/Library</c> rather than out of this file.</b> They
///     are embedded so that a change to a shipped example changes the seeds with it, which is the
///     same rule the packet targets follow by writing their seeds through the real encoders — a
///     corpus that cannot go stale. <c>Example1.rvn</c> earns its place twice over: it is the
///     language showcase, and it is the one file asserted to compile <i>the whole way through both
///     backends</i>, so it is what puts the codegen and validity oracles within one edit's reach.
/// </remarks>
static class RavenSeeds {
    /// <summary>The shipped examples, plus small shapes worth having on their own.</summary>
    public static IReadOnlyList<string> Sources { get; } = Build();

    /// <summary>Snippets the domain concatenates when it builds a shader from nothing.</summary>
    /// <remarks>
    ///     Fragments rather than whole files, because "from nothing" should reach combinations no
    ///     seed is near — two packages in one file, a statement at the top level, a shader inside a
    ///     struct. Each is text the parser has an answer for and the binder mostly does not, which is
    ///     where a compiler is least exercised.
    /// </remarks>
    public static IReadOnlyList<string> Fragments { get; } = [
        "package Vixen.Fuzz\n", "import Vixen.Core\n", "struct S {\n    var a: float\n}\n",
        "shader T {\n    var tint: float4\n}\n", "enum E {\n    Off,\n    On = 5\n}\n",
        "protocol P {\n    func F(): float\n}\n", "func G(x: float): float {\n    return x * 2\n}\n",
        "val k = 1 + 2 * 3\n", "var v: float4 = float4(1, 2, 3, 4)\n", "if (a) {\n    G(1f)\n}\n",
        "for (i in 1 .. 10) {\n    G(1f)\n}\n", "[ComputeShader(8, 8, 1)]\n", "[Permutation] val U: bool = true\n",
        "return 1\n", "val t = (count: 1, scale: 2f)\n", "val s = [1, ..other, 5]\n", "val p = c > 0 ? a : b\n",
        "// a comment\n", "/* block */\n", "{\n}\n"
    ];

    static List<string> Build() {
        var sources = new List<string>();

        foreach (var name in (string[]) ["Example1.rvn", "Example2.rvn"]) {
            if (typeof(RavenTarget).Assembly.GetManifestResourceStream(name) is { } stream) {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                sources.Add(reader.ReadToEnd());
            }
        }

        // Small shapes the examples do not have and that a mutator would take a long time to build:
        // a file that is only a package, and the shortest thing that is still a declaration. Each is
        // a branch every pass has and none of the big files reaches. The empty input is not among
        // them because FuzzSession adds it to every corpus already — and because it is the one input
        // here a Raven file may not be, since the grammar requires a package.
        //
        // ⚠ Newline-terminated, not semicolon-terminated, and fields are `var x: float`. Raven looks
        // enough like C from a distance that a seed written from memory parses into a tree full of
        // fabricated tokens — and because the parser is total, it then looks exactly like a seed that
        // works. Every seed in this list's first draft was of that kind, and
        // EveryGrammarSeedIsWellFormed beside the gate is what said so.
        sources.Add("package Vixen.Fuzz\n");
        sources.Add("package Vixen.Fuzz\n\nstruct S {\n    var a: float\n}\n");

        return sources;
    }
}
