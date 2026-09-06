// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>A builder's <c>&lt;param&gt;</c> docs and the <c>.rvn</c> uniform they write are one
/// description, so they agree on the unit.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/797">#797</a>, which is
///         <a href="https://github.com/Rikarin/Vixen/issues/735">#735</a>'s other half.</b>
///         <c>TextureAngleUnitTests</c> closed the <c>.rvn</c> side mechanically — every uniform whose
///         name is an angle must say radians in its own doc comment. ⚠ <b>The C# side had no such
///         check and was wrong for three batches after the fix</b>: six <c>&lt;param&gt;</c> docs in
///         <c>TextureKernels.Placement.cs</c> said "in turns" for values the same commit had made
///         radians, and they are what a C# caller reads. A builder and the kernel it dispatches are
///         two descriptions of one parameter and only one of them was gated.
///     </para>
///     <para>
///         <b>The pairing is derived and not listed</b>, which is the point — four transcribed
///         subject sets in this workstream have each turned out narrower than the rule they stood
///         for. A builder's body says which kernel it writes (<c>Kernel = …</c>, resolved through the
///         project's own <c>const string</c>s) or calls a builder that does, so a delegating overload
///         is paired through the one it delegates to; the <c>.rvn</c> says which uniforms exist. What
///         is left is a name in both, and every such name is a row here.
///     </para>
///     <para>
///         ⚠ <b>One direction, deliberately: what the kernel says the C# must say.</b> A uniform the
///         kernel documents in radians whose <c>&lt;param&gt;</c> says nothing about units is the
///         quiet version of #797's six lines, and requiring the C# to carry the word is what makes a
///         unit impossible to lose in the translation. Beside it, and independent of any kernel: no
///         builder may say a parameter is <em>in</em> turns or <em>in</em> degrees, which is the loud
///         version and the half that was actually in the tree.
///     </para>
///     <para>
///         ⚠ <b>And no exemption list, which is a choice rather than an omission.</b> Every pair in
///         the tree passes both halves today, so a table of excused disagreements would be a
///         mechanism with nothing in it — this workstream's own commonest defect, one layer up. A
///         disagreement that is genuinely meant can be argued when there is one.
///     </para>
///     <para>
///         ⚠ <b>Ask what this prints on the day the parse stops matching.</b> Nothing, twice over: a
///         regex that found no methods and one that found no uniforms both leave an empty pair set
///         and a green suite. So the pairs that are known to exist are required by name, and — like
///         <c>TextureAngleUnitTests.Known</c> — as a floor rather than an exact set, because an
///         exact set over a surface every slice grows is red on the merge and green on every branch.
///     </para>
/// </remarks>
public class TextureKernelUnitDocTests {
    /// <summary>The unit of record for every angle in this folder — #735.</summary>
    const string Radians = "radian";

    /// <summary>The unit words a builder's <c>&lt;param&gt;</c> doc may carry, and what each means.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Whole words and either case</b>, so "radians" matches "radian" and "Radians" does
    ///         too. <c>turn</c> and <c>degree</c> are here as the units that must appear
    ///         <em>nowhere</em>: they are what #735 removed and what #797's six lines still named.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Angle units only, and lengths deliberately not.</b> "Texel" is a word a doc
    ///         comment uses in prose about what a parameter does, not only about what it is measured
    ///         in — <c>Gradient</c>'s <c>angle</c> and <c>RadialBlur</c>'s <c>amount</c> both carry it
    ///         in a sentence that is not a unit — so requiring the builder to echo it reports four
    ///         disagreements that are not ones. An angle is the case where the doc comment is the
    ///         only carrier of the unit and where being wrong is silent, which is why #735 and #797
    ///         are both about angles.
    ///     </para>
    /// </remarks>
    static readonly string[] Units = [Radians, "degree", "turn"];

    /// <summary>Pairs that must be found, or the parse below has stopped working.</summary>
    /// <remarks>
    ///     The three angle parameters #797 and #735 are actually about, in two builder files and
    ///     resolved through two different <c>const string</c>s — enough that a parse which had
    ///     stopped finding methods, uniforms or kernel names fails here rather than silently
    ///     asserting over nothing. ⚠ Deliberately a floor — see the remark on the class.
    /// </remarks>
    static readonly (string Kernel, string Parameter)[] Known = [
        ("TileSampler", "rotationJitter"),
        ("Splatter", "rotationMapAmount"),
        ("Emboss", "elevation")
    ];

    /// <summary>A method's doc block and its name, at a class member's indentation.</summary>
    /// <remarks>
    ///     ⚠ <b>No <c>=</c> or <c>{</c> before the parenthesis</b>, which is what keeps a property
    ///     whose initializer calls a builder — <c>TexturePlacement.All</c> is one — from being read
    ///     as a method named after the first thing it calls.
    /// </remarks>
    static readonly Regex Method = new(
        @"(?<doc>(?:^[ \t]*///.*\n)+)^[ \t]*(?:public|internal|private|static|\[)[^\n(={]*?"
        + @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\(",
        RegexOptions.Multiline
    );

    /// <summary>One <c>&lt;param&gt;</c> line of a doc block.</summary>
    static readonly Regex Param = new(
        @"<param name=""(?<name>[A-Za-z_][A-Za-z0-9_]*)""\s*>(?<text>.*?)</param>",
        RegexOptions.Singleline
    );

    /// <summary>Which kernel a builder writes, as it is spelled in the initializer.</summary>
    static readonly Regex Writes = new(@"\bKernel\s*=\s*(?<kernel>[A-Za-z_][A-Za-z0-9_.]*|""[^""]+"")");

    /// <summary>A string constant anywhere in the project, so a kernel name spelled as one resolves.</summary>
    static readonly Regex Constant = new(@"\bconst string (?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*""(?<value>[^""]*)""");

    /// <summary>A uniform of a kernel, with the doc block above it.</summary>
    /// <remarks>
    ///     ⚠ Every type and not only <c>float</c>, unlike <c>TextureAngleUnitTests</c>: an
    ///     <c>int</c> count and a <c>Texture2D</c> input are both parameters a builder documents, and
    ///     a unit word is as losable on one of those as on a scalar.
    /// </remarks>
    static readonly Regex Uniform = new(
        @"(?<doc>(?:^[ \t]*///.*\n)*)^[ \t]*var[ \t]+(?<name>[A-Za-z][A-Za-z0-9_]*)[ \t]*:",
        RegexOptions.Multiline
    );

    /// <summary>Every unit word a kernel names is named again by the builder that writes it.</summary>
    [Fact]
    public void A_builders_param_doc_carries_the_unit_its_kernel_declares() {
        var pairs = Pairs();

        Assert.NotEmpty(pairs);

        foreach (var (kernel, parameter) in Known) {
            Assert.True(
                pairs.Any(pair =>
                    string.Equals(pair.Kernel, kernel, StringComparison.Ordinal)
                    && string.Equals(pair.Parameter, parameter, StringComparison.Ordinal)
                ),
                $"the pairing found no <param name=\"{parameter}\"> for '{kernel}', so the parse has stopped "
                + "matching and every assertion below it is about an empty list — #797."
            );
        }

        List<string> drifted = [];

        foreach (var (kernel, parameter, csharp, raven) in pairs) {
            // Radians is the unit of record — #735 — so it is the one word a builder owes, and it
            // owes it exactly when the kernel's own declaration carries it.
            if (Names(raven, Radians) && !Names(csharp, Radians)) {
                drifted.Add(
                    $"'{kernel}' declares {parameter} in radians and its builder does not say so: \"{csharp.Trim()}\""
                );
            }

            foreach (var unit in Units) {
                // And the other two are owed by nothing and measured in nowhere: a builder that says
                // a parameter is *in* turns or *in* degrees is either #797's drift or a unit this
                // folder does not have.
                if (string.Equals(unit, Radians, StringComparison.Ordinal) || !Measures(csharp, unit)) {
                    continue;
                }

                drifted.Add($"'{kernel}'.{parameter}'s builder measures it in {unit}s: \"{csharp.Trim()}\"");
            }
        }

        Assert.True(
            drifted.Count == 0,
            $"{drifted.Count} of {pairs.Count} builder parameters do not carry the unit their kernel declares. "
            + "A builder's docs are what a C# caller reads, and #797 is six of them that said turns for a value "
            + $"the kernel had made radians.{Environment.NewLine}"
            + string.Join(Environment.NewLine, drifted)
        );
    }

    /// <summary>Whether a doc comment names a unit, as a whole word and either case.</summary>
    static bool Names(string doc, string unit) =>
        Regex.IsMatch(doc, $@"\b{Regex.Escape(unit)}s?\b", RegexOptions.IgnoreCase);

    /// <summary>Whether a doc comment says a value is measured <em>in</em> a unit.</summary>
    /// <remarks>
    ///     ⚠ <b>The preposition and not the word, because "turn" is a verb</b> — and English is why
    ///     this is not symmetric with <see cref="Names" />. Four builders here say "turns it, under
    ///     its own amount", "a quarter turn flattens the relief" and "how far the frame turns, in
    ///     radians"; none of them measures anything in turns, and a bare-word rule calls all four
    ///     drift. #797's own six lines said "in turns", which is the shape a unit takes.
    /// </remarks>
    static bool Measures(string doc, string unit) =>
        Regex.IsMatch(doc, $@"\bin {Regex.Escape(unit)}s?\b", RegexOptions.IgnoreCase);

    /// <summary>Every parameter a builder documents that the kernel it writes also declares.</summary>
    /// <returns>The kernel, the name, the builder's sentence and the kernel's.</returns>
    static List<(string Kernel, string Parameter, string CSharp, string Raven)> Pairs() {
        var sources = Builders();
        var constants = Constants();
        var kernels = new Dictionary<string, string>(StringComparer.Ordinal);

        // The builders that name a kernel outright, and then the ones that reach it through those —
        // a short overload delegates rather than building, and #797's six lines were mostly in one.
        foreach (var (name, _, body) in sources) {
            if (Writes.Match(body) is { Success: true } match) {
                kernels[name] = Resolve(match.Groups["kernel"].Value, constants);
            }
        }

        foreach (var (name, _, body) in sources) {
            if (kernels.ContainsKey(name)) {
                continue;
            }

            foreach (var (called, kernel) in kernels.ToArray()) {
                if (Regex.IsMatch(body, $@"\b{Regex.Escape(called)}\s*\(")) {
                    kernels[name] = kernel;

                    break;
                }
            }
        }

        List<(string, string, string, string)> pairs = [];

        foreach (var (name, doc, _) in sources) {
            if (!kernels.TryGetValue(name, out var kernel) || !TextureKernels.Names.Contains(kernel)) {
                continue;
            }

            var uniforms = Uniforms(TextureKernels.Source(kernel));

            foreach (Match parameter in Param.Matches(doc)) {
                if (uniforms.TryGetValue(parameter.Groups["name"].Value, out var declared)) {
                    pairs.Add((kernel, parameter.Groups["name"].Value, parameter.Groups["text"].Value, declared));
                }
            }
        }

        return pairs;
    }

    /// <summary>Every documented method in the kernel-builder files, with its doc block and body.</summary>
    /// <remarks>
    ///     ⚠ <b>Bounded by the next member as well as by a closing brace at a member's
    ///     indentation</b>, and the brace alone is not enough: an expression-bodied overload ends in
    ///     <c>);</c> and has no such brace, so its body ran on into the next builder and took that
    ///     one's <c>Kernel =</c> with it — which is how every <c>Tile Sampler</c> parameter came out
    ///     paired against <c>Splatter</c>'s uniforms, silently and with a full pair list.
    /// </remarks>
    static List<(string Name, string Doc, string Body)> Builders() {
        List<(string, string, string)> methods = [];

        foreach (var file in Directory.GetFiles(Project(), "TextureKernels.*.cs")) {
            var text = File.ReadAllText(file);
            var found = Method.Matches(text);

            for (var index = 0; index < found.Count; index++) {
                var from = found[index].Index + found[index].Length;
                var limit = index + 1 < found.Count ? found[index + 1].Index : text.Length;
                var brace = text.IndexOf("\n    }", from, StringComparison.Ordinal);
                var to = brace < 0 || brace > limit ? limit : brace;

                methods.Add((
                    found[index].Groups["name"].Value,
                    found[index].Groups["doc"].Value,
                    text[from..to]
                ));
            }
        }

        return methods;
    }

    /// <summary>Every string constant the project declares, so a kernel named by one resolves.</summary>
    static Dictionary<string, string> Constants() {
        Dictionary<string, string> constants = new(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(Project(), "*.cs")) {
            foreach (Match constant in Constant.Matches(File.ReadAllText(file))) {
                constants[constant.Groups["name"].Value] = constant.Groups["value"].Value;
            }
        }

        return constants;
    }

    /// <summary>The kernel a <c>Kernel =</c> initializer names, whether spelled or referred to.</summary>
    static string Resolve(string spelled, Dictionary<string, string> constants) {
        if (spelled.StartsWith('"')) {
            return spelled.Trim('"');
        }

        var last = spelled[(spelled.LastIndexOf('.') + 1)..];

        return constants.TryGetValue(last, out var value) ? value : last;
    }

    /// <summary>One kernel's uniforms, by name, with the doc comment above each.</summary>
    static Dictionary<string, string> Uniforms(string source) {
        Dictionary<string, string> uniforms = new(StringComparer.Ordinal);

        foreach (Match match in Uniform.Matches(source)) {
            uniforms[match.Groups["name"].Value] = match.Groups["doc"].Value;
        }

        return uniforms;
    }

    /// <summary>Where the kernel builders and the shaders live.</summary>
    static string Project() => Path.Combine(Root(), "Editor", "Vixen.Editor.TextureGraph");

    /// <summary>Walks up from the test assembly until the repository root is recognisable.</summary>
    /// <remarks>
    ///     ⚠ <b>Up from the assembly and then down one named path</b>, never a glob from the root:
    ///     an agent's worktree under <c>.claude/worktrees</c> is a whole second checkout, and a walk
    ///     from the root reads its copies of these files instead of this tree's.
    /// </remarks>
    static string Root() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
