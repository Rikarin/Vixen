// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO;
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
///         ⚠ <b>This paragraph used to say "everything that is not this suite", and that is no longer
///         true — the census it exists to keep has to be kept in Raven too.</b>
///         <c>Editor/Vixen.Editor.Host/Shaders/Ui.rvn</c> is a second copy, 488 lines against the
///         desktop copy's 886. The five shaders both files carry are identical today, so nothing has
///         drifted the way <c>ui-box.frag</c> did; what the editor's copy is missing is
///         <c>UiBlur</c>, <c>UiColour</c> and <c>UiMask</c> outright, so the editor composites and
///         does not blur, filter or mask. <c>CheckShaders</c> cannot see this: it proves each
///         committed module matches the source beside it, which both copies do. Nothing compares the
///         two sources.
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
///         So what stays here is the half that applies to one copy: a committed module is no older
///         than the GLSL it was compiled from — and, since there is more than one Raven copy after
///         all, <see cref="EveryRavenCopyAgreesAboutTheShadersItShares" />, which is
///         <c>Copies</c> restored in the language the shaders are written in now.
///     </para>
/// </remarks>
public class SharedUiShaderTests {
    /// <summary>Where this suite's own GLSL and its modules live, relative to the repository root.</summary>
    static readonly string Shaders = Path.Combine("Platform", "Vixen.Graphics.Golden.Tests", "Shaders");

    /// <summary>A committed module is no older than the GLSL it was compiled from.</summary>
    /// <remarks>
    ///     ⚠ <b>The half a source comparison could never see, and the half that actually shipped
    ///     broken.</b> This suite loads the <c>.spv</c>, so a correct <c>.frag</c> beside a stale
    ///     module is a shader that is right in the repository and wrong in the binary — which is
    ///     exactly the state the tree was in for the hours between the source being fixed and
    ///     <c>glslc</c> being run.
    ///     <para>
    ///         A timestamp is a weak check and deliberately so: it cannot prove the module came from
    ///         this source, only that nobody edited the source and forgot the module, which is the
    ///         mistake that is actually made. ⚠ It is also the check git cannot help with — a
    ///         checkout sets both files' times — so it can only be trusted to fire on a tree someone
    ///         has edited, and it is skipped when the two are within a second of each other.
    ///     </para>
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
    public void TheCommittedModuleIsNewerThanItsSource(string name) {
        var source = Path.Combine(RepositoryRoot(), Shaders, name);

        Assert.True(File.Exists(source), $"{Path.Combine(Shaders, name)} is missing, and the reference images were rendered with it.");

        var module = source + ".spv";

        Assert.True(File.Exists(module), $"{Path.Combine(Shaders, name)}.spv is missing, and it is the artefact this suite loads.");

        var sourceTime = File.GetLastWriteTimeUtc(source);
        var moduleTime = File.GetLastWriteTimeUtc(module);

        Assert.True(
            moduleTime >= sourceTime.AddSeconds(-1),
            $"{Path.Combine(Shaders, name)}.spv was written {(sourceTime - moduleTime).TotalSeconds:F0}s "
            + $"before the GLSL beside it, so the module this suite renders with is not this source. "
            + $"Regenerate it: `glslc Shaders/{name} -o Shaders/{name}.spv` from this project's directory."
        );
    }

    /// <summary>Every <c>Ui.rvn</c> in the tree agrees, shader for shader, with every other.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>Copies</c>, restored — the invariant did not stop mattering when the shaders
    ///         stopped being GLSL, it stopped being checked.</b> The original compared three
    ///         hand-maintained <c>.frag</c> files and caught two of them missing the whole shadow
    ///         path. Two of those three are gone; a second Raven copy has since appeared under
    ///         <c>Editor/Vixen.Editor.Host/Shaders</c>, and nothing compares it with the desktop one.
    ///         <c>CheckShaders</c> cannot: it proves each committed module matches the source beside
    ///         it, which is true of both copies independently and says nothing about the pair.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Per shader rather than per file, because the files are legitimately different
    ///         sizes.</b> The editor's copy carries five of the eight shaders and the desktop's
    ///         carries all eight, so a whole-file comparison would be red today for a reason nobody
    ///         should silence by copying three shaders into a host that does not wire them. What is
    ///         wrong is not that one is shorter — it is a shader whose *body* differs between two
    ///         files that both claim to be the interface.
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
