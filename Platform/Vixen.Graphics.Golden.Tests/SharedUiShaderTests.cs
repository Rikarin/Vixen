// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The GLSL this suite renders with, and the committed modules beside it.</summary>
/// <remarks>
///     <para>
///         <b>This file used to compare three hand-maintained copies of the same eight shaders and
///         it now checks one.</b> The invariant was real and it had already been broken: on
///         2026-08-09 <c>ui-box.frag</c> here was sixteen lines longer than the copies under
///         <c>Samples/02-HelloUi</c> and the <c>vixen-app</c> template, and the missing lines were
///         the whole shadow path. The struct is shared and its own comment reserves <c>axis.z</c>
///         for "a shadow's blur", so the two stale copies declared that field and never read it — a
///         shape asking for a soft shadow got a hard-edged box exactly where the shadow should have
///         been, at full opacity, on two of three copies, with nothing rendering blank.
///     </para>
///     <para>
///         ⚠ <b>There is one <i>GLSL</i> copy because the other two stopped being GLSL.</b> An
///         application draws the interface from <c>Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn</c>,
///         compiled by this repository's own compiler and gated by <c>./build.sh CheckShaders</c>,
///         which is a far stronger check than a byte comparison of two files somebody has to keep
///         equal by hand: it recompiles the source and fails if the committed module differs.
///     </para>
///     <para>
///         ⚠ <b>There was a second Raven copy and it is gone, which is what
///         <see cref="EveryRavenCopyAgreesAboutTheShadersItShares" /> was written to make safe to
///         delete.</b> <c>Editor/Vixen.Editor.Host/Shaders/Ui.rvn</c> carried five of the desktop
///         copy's eight shaders — every line of the five identical, so nothing had drifted the way
///         <c>ui-box.frag</c> did, and <c>UiBlur</c>, <c>UiColour</c> and <c>UiMask</c> simply
///         absent, so the editor composited and never blurred, filtered or masked. <c>CheckShaders</c>
///         could not see that: it proves each committed module matches the source beside it, which
///         both copies did. The test below is what compared the sources, and it now guards a census
///         of one against the next copy somebody adds.
///     </para>
///     <para>
///         ⚠ <b>What is left uncovered is worth naming rather than papering over.</b> The reference
///         images in this suite were rendered with the GLSL below, and every shipping application
///         renders with the Raven modules. They are two implementations of one specification in two
///         languages, so no byte comparison can settle it, and the only <i>sufficient</i> check is a
///         golden image rendered through each. The right end state is this suite driving the Raven
///         modules too, which is a change that regenerates every reference image in it and belongs
///         on its own.
///     </para>
///     <para>
///         ⚠ <b>"Nothing compares the two" stood here and is no longer true — one necessary
///         condition of the comparison is checked, and the distinction is the point.</b>
///         <see cref="EveryConstantInTheGlslCopyIsOneTheRavenHoldsToo" /> holds the numbers: the
///         eighteen Oklab coefficients, sRGB's five, and every threshold the shape and shadow paths
///         branch on are the same in both files or one of them is wrong. Constants are the part of a
///         specification that survives translation between two languages unchanged, so they are the
///         part a laptop can check. An expression rearranged around the same numbers still passes,
///         which is why the golden through each stays owed rather than closed.
///     </para>
///     <para>
///         ⚠ <b>That check ran over one file of eight and now runs over all eight, and widening it
///         turned up exactly the rearrangement its own remark predicted.</b> Nothing made
///         <c>ui-box.frag</c> special about <i>constants</i> — it is special about the <i>record</i>,
///         which is a different claim and <c>UiShapeLayoutTests</c>' — and the other seven transcribe
///         the same Raven out of the same specification, <c>ui-mask.frag</c> alone holding ten
///         numbers. Widening it needed <c>layout(…)</c> qualifiers dropped, whose numbers are an ABI
///         stated in one language and not in the other; and it found one number spelled two ways —
///         <c>ui-mask.frag</c> divides a conic sweep by <c>6.28318531</c> where the Raven multiplies
///         by its reciprocal. Same rotation, and no comparison of numbers can see through it, so it
///         is an exception of one carrying its reason, on a list that is allowed to shrink and never
///         to grow quietly.
///     </para>
///     <para>
///         So what stays here is the half that applies to one copy: every committed module is the one
///         built from the GLSL beside it as that GLSL now reads — and, since there is more than one
///         Raven copy after all, <see cref="EveryRavenCopyAgreesAboutTheShadersItShares" />, which is
///         <c>Copies</c> restored in the language the shaders are written in now.
///     </para>
///     <para>
///         ⚠ <b>And the record of the shared struct's five agreeing places lives here rather than in
///         the shader, which is #588's answer.</b> <c>Shape</c> is 144 bytes and four things have to
///         agree about that: <c>Vixen.Ui.Rendering.UiShape</c>, <c>UiRenderer</c>'s buffer stride,
///         <c>SoftwareUiRasterizer</c>, and <c>Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn</c> — plus
///         <c>ui-box.frag</c> here, which is the fifth. <c>UiShapeLayoutTests</c> pins the first
///         against the Raven copy's reflection and parses this GLSL to hold it to <c>UiShape</c>'s
///         lanes in order, on any machine; the rest are pinned by this suite on a real device, which
///         is how an 80-byte stride was caught.
///     </para>
///     <para>
///         ⚠ <b>The shader's own header used to say all that and had three sentences of it wrong</b> —
///         that there were three GLSL copies (there is one; the other two became Raven), that one of
///         the agreeing places is "the editor's <c>Ui.rvn</c>" (deleted; the desktop copy is what the
///         editor draws with too), and that nothing but a device pinned the rest. It sat there
///         because <em>correcting it was expensive</em> under the timestamp check this file used to
///         carry. Prose belongs where editing it is free.
///     </para>
/// </remarks>
public partial class SharedUiShaderTests {
    /// <summary>Where this suite's own GLSL and its modules live, relative to the repository root.</summary>
    static readonly string Shaders = Path.Combine("Platform", "Vixen.Graphics.Golden.Tests", "Shaders");

    /// <summary>The eight shaders this suite renders with, source and module alike.</summary>
    static readonly string[] Names = [
        "ui-blur.frag",
        "ui-box.frag",
        "ui-colour.frag",
        "ui-image.frag",
        "ui-mask.frag",
        "ui-solid.frag",
        "ui-text.frag",
        "ui.vert"
    ];

    /// <summary>The committed record of which source each committed module was built from.</summary>
    static string Ledger => Path.Combine(RepositoryRoot(), Shaders, "modules.sha256");

    /// <summary>Rewrites the ledger instead of checking it, for the commit that regenerates a module.</summary>
    static bool Updating =>
        Environment.GetEnvironmentVariable("VIXEN_UPDATE_SHADER_DIGESTS") is "1" or "true" or "TRUE";

    /// <summary>Every committed module is the one built from the GLSL beside it as that GLSL now reads.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half a source comparison could never see, and the half that actually shipped
    ///         broken.</b> This suite loads the <c>.spv</c>, so a correct <c>.frag</c> beside a stale
    ///         module is a shader that is right in the repository and wrong in the binary — which is
    ///         exactly the state the tree was in for the hours between the source being fixed and
    ///         <c>glslc</c> being run.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This was a timestamp comparison, and a timestamp is a property of a checkout
    ///         rather than of the repository.</b> Git carries content and not mtimes, so the old check
    ///         could only fire in the tree where the edit happened — and it fired there on
    ///         <i>any</i> edit, a comment included. That is not a theoretical cost: correcting three
    ///         false sentences in <c>ui-box.frag</c>'s header (#588) turned this suite red for
    ///         everyone whose merge rewrote the source and left the module's bytes alone, and the way
    ///         out on offer was a <c>glslc</c> run and a new committed binary to fix a comment.
    ///     </para>
    ///     <para>
    ///         <b>So the record is committed rather than inferred.</b> <c>Shaders/modules.sha256</c>
    ///         holds, per shader, the digest of the source <em>with its comments removed</em> and the
    ///         digest of the module. A comment-only edit changes neither and needs nothing
    ///         regenerated; a change to a single expression changes the first and is red until
    ///         <c>glslc</c> has run and the ledger has been rewritten. ⚠ And unlike the timestamps it
    ///         replaces, it says so on <i>every</i> checkout, which is where CI reads it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it still cannot prove is that the module was compiled from that source</b> —
    ///         only a compiler can, and this assembly has none. It proves the pair is the pair
    ///         somebody last regenerated together, which is the mistake that is actually made.
    ///         <c>VIXEN_UPDATE_SHADER_DIGESTS=1</c> rewrites the ledger, deliberately an environment
    ///         variable and not a default: accepting a module is a decision.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryCommittedModuleMatchesTheSourceItWasBuiltFrom() {
        var recorded = Updating ? [] : Recorded();

        var written = new List<string>();

        foreach (var name in Names) {
            var source = Path.Combine(RepositoryRoot(), Shaders, name);

            Assert.True(File.Exists(source), $"{Path.Combine(Shaders, name)} is missing, and the reference images were rendered with it.");

            var module = source + ".spv";

            Assert.True(File.Exists(module), $"{Path.Combine(Shaders, name)}.spv is missing, and it is the artefact this suite loads.");

            var code = Digest(Encoding.UTF8.GetBytes(Code(File.ReadAllText(source))));
            var binary = Digest(File.ReadAllBytes(module));

            if (Updating) {
                written.Add($"{name} {code} {binary}");
                continue;
            }

            Assert.True(
                recorded.TryGetValue(name, out var pair),
                $"{name} has no line in Shaders/modules.sha256, so nothing says which source its committed "
                + "module was built from. Add one with `VIXEN_UPDATE_SHADER_DIGESTS=1`."
            );

            Assert.True(
                string.Equals(pair.Code, code, StringComparison.Ordinal),
                $"{Path.Combine(Shaders, name)} has changed since its module was built — its code, not its "
                + $"comments, which are stripped before this digest. The module this suite renders with is "
                + $"not this source. Regenerate it and the ledger: `glslc Shaders/{name} -o Shaders/{name}.spv` "
                + "from this project's directory, then rerun with `VIXEN_UPDATE_SHADER_DIGESTS=1`."
            );

            Assert.True(
                string.Equals(pair.Module, binary, StringComparison.Ordinal),
                $"{Path.Combine(Shaders, name)}.spv is not the module the ledger records, and its source is "
                + "unchanged — so a binary moved without the source that produced it. Rerun with "
                + "`VIXEN_UPDATE_SHADER_DIGESTS=1` only if that was deliberate."
            );
        }

        if (Updating) {
            File.WriteAllLines(Ledger, written);
            return;
        }

        // ⚠ The instrument. A ledger the parser failed to read is an empty dictionary, and an empty
        // dictionary would have failed the first `TryGetValue` above — but only if `Names` is not
        // itself empty, and a census of nothing agrees with everything.
        Assert.Equal(8, Names.Length);
        Assert.Equal(Names.Length, recorded.Count);
    }

    /// <summary>The ledger, by shader name.</summary>
    static Dictionary<string, (string Code, string Module)> Recorded() {
        Assert.True(
            File.Exists(Ledger),
            $"'{Ledger}' is missing, and it is the only thing that says which source each committed module "
            + "was built from. Write it with `VIXEN_UPDATE_SHADER_DIGESTS=1`."
        );

        var found = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

        foreach (var line in File.ReadAllLines(Ledger)) {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 3) {
                found[parts[0]] = (parts[1], parts[2]);
            }
        }

        return found;
    }

    /// <summary>A GLSL source with its comments taken out, so a comment is free to edit.</summary>
    /// <remarks>
    ///     ⚠ <b>Blank lines and trailing spaces go too, or removing a comment leaves the line it was
    ///     on behind and the digest moves anyway.</b> GLSL has no string literals, so there is nothing
    ///     a <c>//</c> can hide inside and the scan needs no third state.
    /// </remarks>
    internal static string Code(string source) {
        var text = new StringBuilder(source.Length);
        var block = false;

        foreach (var raw in source.Split('\n')) {
            var line = new StringBuilder(raw.Length);

            for (var at = 0; at < raw.Length; at++) {
                if (block) {
                    if (at + 1 < raw.Length && raw[at] == '*' && raw[at + 1] == '/') {
                        block = false;
                        at++;
                    }

                    continue;
                }

                if (at + 1 < raw.Length && raw[at] == '/' && raw[at + 1] == '/') {
                    break;
                }

                if (at + 1 < raw.Length && raw[at] == '/' && raw[at + 1] == '*') {
                    block = true;
                    at++;
                    continue;
                }

                line.Append(raw[at]);
            }

            var kept = line.ToString().TrimEnd();

            if (kept.Length > 0) {
                text.Append(kept).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>A lower-case hexadecimal SHA-256, which is what the ledger holds.</summary>
    static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Every <c>Ui.rvn</c> in the tree agrees, shader for shader, with every other.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>Copies</c>, restored — the invariant did not stop mattering when the shaders
    ///         stopped being GLSL, it stopped being checked.</b> The original compared three
    ///         hand-maintained <c>.frag</c> files and caught two of them missing the whole shadow
    ///         path. Two of those three are gone, and so is the second Raven copy this test was
    ///         written against — <c>Editor/Vixen.Editor.Host/Shaders/Ui.rvn</c>, which
    ///         <c>CheckShaders</c> could not compare with the desktop one because it proves each
    ///         committed module matches the source beside it, which was true of both copies
    ///         independently and said nothing about the pair.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It keeps running over a census of one, and that is the point rather than a
    ///         leftover.</b> A walk that finds one file compares nothing, so what holds this up is the
    ///         count assertion and the three names below: the day somebody adds a second
    ///         <c>Ui.rvn</c>, this is what says the two agree, and it costs nothing until then.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Per shader rather than per file, because two copies can legitimately be different
    ///         sizes.</b> The editor's carried five of the eight shaders, so a whole-file comparison
    ///         would have been red for a reason nobody should silence by copying three shaders into a
    ///         host that does not wire them. What is wrong is not that one is shorter — it is a
    ///         shader whose *body* differs between two files that both claim to be the interface.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it asserts it found the files.</b> A walk that matched nothing agrees with
    ///         itself perfectly; this suite has shipped that mistake before, which is what the
    ///         remarks above are about.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryRavenCopyAgreesAboutTheShadersItShares() {
        var root = RepositoryRoot();

        var sources = Directory.EnumerateFiles(root, "Ui.rvn", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))

            // ⚠ <b>And not another checkout of this repository, which is what a dot directory under
            // the root is.</b> `.claude/worktrees` holds a git worktree per parallel agent, each a
            // full tree with its own `Ui.rvn` at whatever commit that branch is on — so this walk
            // was comparing *old versions of this file with each other* and reporting drift that is
            // not in the tree under test. It failed exactly that way, naming two agent worktrees,
            // and it would have gone on doing so however correct the working tree was. The reverse
            // is the worse half: a walk whose first disagreement is between two other checkouts
            // stops before it reaches this one.
            .Where(path => !Relative(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.StartsWith('.')))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            sources.Count > 0,
            $"no Ui.rvn was found under '{root}', so this test compared nothing and would have passed whatever the shaders said."
        );

        var seen = new Dictionary<string, (string Path, string Body)>(StringComparer.Ordinal);

        foreach (var path in sources) {
            foreach (var (name, body) in Blocks(File.ReadAllText(path))) {
                if (seen.TryGetValue(name, out var first)) {
                    Assert.True(
                        string.Equals(first.Body, body, StringComparison.Ordinal),
                        $"shader `{name}` differs between '{Relative(root, first.Path)}' and '{Relative(root, path)}'. "
                        + "Two files claiming to be the interface is how `ui-box.frag` lost the shadow path on two of "
                        + "three copies; the fix is one source, not two that match."
                    );

                    continue;
                }

                seen[name] = (path, body);
            }
        }

        // The census, so a copy that quietly stopped declaring anything cannot pass by being empty.
        Assert.Contains("UiBox", seen.Keys);
        Assert.Contains("UiText", seen.Keys);
        Assert.Contains("UiSolid", seen.Keys);
    }

    /// <summary>Every number the GLSL copy holds is a number the Raven it transcribes holds too.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the first thing in the tree that compares the two languages at all, and it
    ///         is a <i>necessary</i> condition rather than a sufficient one — which is the whole of
    ///         what it claims.</b> The class remark above says the only real check is a golden
    ///         rendered through each, and that is still true and still owed. What is available
    ///         without a device is the half of the specification that survives translation unchanged:
    ///         the constants. Björn Ottosson's eighteen Oklab coefficients, sRGB's <c>0.0031308</c>,
    ///         <c>12.92</c>, <c>1.055</c>, <c>0.04045</c> and <c>2.4</c>, and every threshold the
    ///         shape and shadow paths branch on are the same numbers in both files or one of them is
    ///         wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it cannot catch, stated so nobody reads more into a green run than is
    ///         there.</b> An expression rearranged around the same constants passes. A term dropped
    ///         from a sum whose coefficient appears elsewhere passes. The historical defect this
    ///         file exists for — <c>ui-box.frag</c> losing the whole shadow path on two copies —
    ///         <i>would</i> have been caught, because that path carries constants nothing else uses;
    ///         but that is a property of that defect and not a general guarantee.
    ///     </para>
    ///     <para>
    ///         <b>One direction, and the direction is the argument.</b> The Raven is the source every
    ///         shipping application draws through and it carries eight shaders; the GLSL is one
    ///         transcription of one of them. So the Raven legitimately holds numbers this file does
    ///         not, and the containment that means anything is GLSL ⊆ Raven. The reverse would be a
    ///         gate red on every shader the copy does not transcribe.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Preprocessor lines are dropped rather than their numbers allow-listed</b>, which
    ///         matters more than it looks. <c>#version 450</c> is the one number in this file that is
    ///         not a constant, and an allow-list holding <c>450</c> would be a hole any future drift
    ///         to that value could hide in — and allow-lists here rot. Dropping the directive drops it
    ///         for the reason it is not a constant.
    ///     </para>
    ///     <para>
    ///         <b>What this prints on the day it does not run.</b> A regex that stopped matching
    ///         returns an empty set, and an empty set is contained in everything — the exact shape of
    ///         the "comparator that called three empty manifests identical" this repository has
    ///         already shipped once. So the extractor is checked before it is trusted: the sweep must
    ///         find one named coefficient it is impossible to be right without.
    ///     </para>
    /// </remarks>
    /// <summary>The one number a copy spells differently, and why it is not drift.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An exception list of one, and it is the case this whole check's own remark
    ///         predicted it could not see.</b> <c>ui-mask.frag</c> normalises a conic sweep as
    ///         <c>angle / 6.28318531</c> and the Raven as <c>angle * 0.15915494309189535f</c> — the
    ///         same rotation, spelled as a division and as a multiplication by the reciprocal, and no
    ///         comparison of numbers can see through that. Admitting reciprocals generally would be
    ///         the wrong fix: it would weaken every file's containment to catch one legitimate
    ///         difference, and this repository's own history is of allow-lists that rot into holes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The list can only shrink, which is what stops it being a hiding place.</b> An
    ///         entry that is no longer missing fails, so the day somebody rewrites either expression
    ///         to match the other, this says so instead of quietly excusing a number that is now
    ///         present. Filed as an issue rather than fixed here: editing the GLSL changes its code
    ///         digest, and <see cref="EveryCommittedModuleMatchesTheSourceItWasBuiltFrom" /> then
    ///         wants <c>glslc</c> and a new committed module for what is arithmetically a no-op.
    ///     </para>
    /// </remarks>
    static readonly Dictionary<string, float[]> Spelled = new(StringComparer.Ordinal) {
        ["ui-mask.frag"] = [6.28318531f]
    };

    /// <summary>Every number each GLSL copy holds is a number the Raven it transcribes holds too.</summary>
    /// <remarks>
    ///     ⚠ <b>All eight, where this was <c>ui-box.frag</c> alone.</b> The record's layout is what
    ///     <c>ui-box.frag</c> is special about — it is the file <c>UiShapeLayoutTests</c> parses — and
    ///     nothing made it special about <i>constants</i>: the other seven transcribe the same Raven
    ///     out of the same specification, and <c>ui-mask.frag</c> alone holds ten of them. A check
    ///     that compared one file of eight was answering a narrower question than the one this class
    ///     exists to ask.
    /// </remarks>
    [Theory]
    [InlineData("ui-blur.frag")]
    [InlineData("ui-box.frag")]
    [InlineData("ui-colour.frag")]
    [InlineData("ui-image.frag")]
    [InlineData("ui-mask.frag")]
    [InlineData("ui-solid.frag")]
    [InlineData("ui-text.frag")]
    [InlineData("ui.vert")]
    public void EveryConstantInTheGlslCopyIsOneTheRavenHoldsToo(string name) {
        var root = RepositoryRoot();
        var glsl = Path.Combine(root, Shaders, name);
        var raven = Path.Combine(root, "Platform", "Vixen.Ui.Desktop", "Shaders", "Ui.rvn");

        Assert.True(File.Exists(glsl), $"'{Relative(root, glsl)}' is missing, and it is the copy this compares.");
        Assert.True(File.Exists(raven), $"'{Relative(root, raven)}' is missing, and it is what every application draws through.");

        var text = File.ReadAllText(glsl);

        var copy = Constants(text);
        var source = Constants(File.ReadAllText(raven));

        // ⚠ The instrument's own check, and it runs before the comparison rather than after it. This
        // is the first coefficient of Ottosson's linear-to-LMS matrix: no correct version of the
        // Raven can be without it, so a sweep that does not find it has stopped reading rather than
        // found agreement.
        Assert.Contains(0.4122214708f, source);

        // ⚠ <b>And the per-file anchor is that the file was READ, not that it held a number.</b>
        // `ui-image.frag` samples a texture and holds no arithmetic constant at all once the
        // `layout(…)` qualifiers are dropped, so an empty set is its right answer and is trivially
        // contained — while an empty set arrived at by an extractor that stopped working is the
        // "comparator that called three empty manifests identical" this repository has shipped once.
        // What separates the two is whether there was any code to read.
        Assert.NotEqual(0, Code(text).Trim().Length);

        var spelled = Spelled.TryGetValue(name, out var known) ? known : [];
        var missing = copy.Where(value => !source.Contains(value)).Order().ToList();

        // ⚠ Before the comparison, so the list can only shrink: an exception whose number the Raven
        // now holds is an exception that has outlived its reason.
        foreach (var value in spelled) {
            Assert.True(
                missing.Contains(value),
                $"'{name}' is excused {value.ToString("R", CultureInfo.InvariantCulture)} and no longer needs to be — "
                + $"'{Relative(root, raven)}' holds it now, or the copy has stopped holding it. Drop the entry from "
                + $"`{nameof(Spelled)}`; that list is allowed to shrink and never to grow quietly."
            );
        }

        missing.RemoveAll(spelled.Contains);

        Assert.True(
            missing.Count == 0,
            $"'{Relative(root, glsl)}' holds {missing.Count} number(s) that '{Relative(root, raven)}' does not: "
            + string.Join(", ", missing.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))
            + ". They are two implementations of one specification, and a constant in one and not the "
            + "other is drift between them — which is what the reference images in this suite would "
            + "then be rendered against."
        );
    }

    /// <summary>Every numeric literal in a shader source, as the <c>float</c> the GPU would hold.</summary>
    /// <remarks>
    ///     ⚠ <b>Compared as <c>float</c> values and not as text, because the two languages spell the
    ///     same number differently and neither spelling is wrong.</b> Raven writes <c>0.5f</c> where
    ///     GLSL writes <c>0.5</c>, and a comparison of the characters would report drift on every
    ///     line. Rounding to single precision is also what the shaders themselves do, so two
    ///     spellings a GPU cannot tell apart are two spellings this cannot either — which is the
    ///     right resolution for a check about what the hardware computes.
    ///
    ///     ⚠ <b>Found by a sabotage that came back green, and it is the sharpest thing this test
    ///     has to say about itself.</b> Changing <c>0.5363325363</c> to <c>0.5363325364</c> in the
    ///     GLSL does not fail this — the two are the same <c>float</c>, and Ottosson's coefficients
    ///     are written to ten digits where single precision holds about seven. That looked like a
    ///     hole and is not one: <c>glslc</c> rounds the literal the same way, so a difference this
    ///     cannot see is a difference the compiled module does not contain. The check is exactly as
    ///     sensitive as the hardware, which is the sensitivity a picture is rendered at. The
    ///     sabotage that <i>does</i> go red is <c>0.5363325</c>.
    /// </remarks>
    static HashSet<float> Constants(string source) {
        var values = new HashSet<float>();

        foreach (var line in Code(source).Split('\n')) {
            // The directives, dropped whole — see the remark above.
            if (line.TrimStart().StartsWith('#')) {
                continue;
            }

            // ⚠ A `layout(offset = 16)` is an ABI, not a constant of the specification, and the two
            // languages state it in different places — the Raven declares a push-constant block and
            // spells no byte offsets at all. Left in, every GLSL file carrying one reports the offset
            // as drift; and the thing those numbers actually have to agree with is the record, which
            // `UiShapeLayoutTests` parses out of this same file. So the qualifier is dropped whole,
            // for `#version`'s reason: it is not the kind of number this is about.
            foreach (Match match in Literal().Matches(Qualifier().Replace(line, " "))) {
                if (float.TryParse(match.Value.TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    /// <summary>A numeric literal, and never the tail of an identifier or a swizzle.</summary>
    /// <remarks>
    ///     ⚠ The leading guard is what keeps <c>c.b</c>, <c>float2</c> and <c>radiiX[1]</c> from
    ///     contributing digits. A bare <c>\d</c> sweep over either of these files reports the <c>2</c>
    ///     of <c>float2</c> and the <c>4</c> of <c>vec4</c> as constants, which is noise on both sides
    ///     and would make the containment hold by accident.
    /// </remarks>
    [GeneratedRegex(@"(?<![\w.])\d+(?:\.\d*)?(?:[eE][-+]?\d+)?[fF]?")]
    private static partial Regex Literal();

    /// <summary>A GLSL <c>layout(…)</c> qualifier, whose numbers are an ABI rather than arithmetic.</summary>
    [GeneratedRegex(@"layout\s*\([^)]*\)")]
    private static partial Regex Qualifier();

    /// <summary>Every <c>shader Name { … }</c> block in a Raven source, by name.</summary>
    /// <remarks>
    ///     Brace counting rather than a regular expression over the whole block: a shader body
    ///     contains braces, and the closing one is only recognisable by depth. Whitespace is
    ///     normalised away so a reformat is not a failure and a changed expression is.
    /// </remarks>
    static IEnumerable<(string Name, string Body)> Blocks(string source) {
        const string keyword = "\nshader ";

        for (var at = source.IndexOf(keyword, StringComparison.Ordinal); at >= 0; at = source.IndexOf(keyword, at + 1, StringComparison.Ordinal)) {
            var nameAt = at + keyword.Length;
            var brace = source.IndexOf('{', nameAt);

            if (brace < 0) {
                yield break;
            }

            var name = source[nameAt..brace].Trim();
            var depth = 0;
            var end = brace;

            for (; end < source.Length; end++) {
                if (source[end] == '{') {
                    depth++;
                } else if (source[end] == '}' && --depth == 0) {
                    break;
                }
            }

            yield return (name, Squeezed(source[brace..Math.Min(end + 1, source.Length)]));
        }
    }

    /// <summary>A body with every run of whitespace collapsed to one space.</summary>
    static string Squeezed(string body) => string.Join(' ', body.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A path as it reads in a failure: relative to the repository root.</summary>
    static string Relative(string root, string path) => Path.GetRelativePath(root, path);

    /// <summary>The repository root, found by walking up rather than by counting directories.</summary>
    static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
