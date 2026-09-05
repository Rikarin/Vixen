// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Security.Cryptography;
using System.Text;
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
///         renders with the Raven modules — and <i>nothing compares the two</i>. They are two
///         implementations of one specification in two languages, so no byte comparison can, and the
///         only real check is a golden image rendered through each. The right end state is this
///         suite driving the Raven modules too, which is a change that regenerates every reference
///         image in it and belongs on its own.
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
public class SharedUiShaderTests {
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
