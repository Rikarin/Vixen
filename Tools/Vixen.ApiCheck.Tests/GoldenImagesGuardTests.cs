// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ That the one target whose entire subject is a picture refuses to pass without a device.
/// </summary>
/// <remarks>
///     <para>
///         <b>Ask what <c>GoldenImages</c> printed on the day it did not run.</b> Every fixture in
///         <c>Vixen.Graphics.Golden.Tests</c> skips itself when no Vulkan device opens — correctly,
///         because a machine with no driver is not a broken renderer — and the target then compared
///         no pictures, exited 0, and reported success. A green <c>GoldenImages</c> was evidence
///         about the machine and not about the renderer, and nothing said so.
///     </para>
///     <para>
///         ⚠ <b><c>ci.yml</c> does not cover this, which is why the hole survived.</b> It sets
///         <c>VIXEN_REQUIRE_VULKAN</c> on the ubuntu <c>Test</c> leg and never invokes
///         <c>GoldenImages</c> at all — so the target's only caller is a developer at a terminal, on
///         exactly the machine most likely to have no device.
///     </para>
///     <para>
///         <b>Text, because the subject is a Nuke build class this assembly cannot reference.</b>
///         <c>build/_build.csproj</c> is outside <c>Vixen.slnx</c> and needs Nuke to instantiate,
///         which is the same reason <c>TestCostDrift</c> and <c>AotProbeProjectFile</c> are linked
///         in as source rather than referenced. <c>Build.cs</c> cannot be linked — it derives from
///         <c>NukeBuild</c> — so what is committed is read as text.
///     </para>
///     <para>
///         ⚠ <b>The value is asserted and not only the name, which the first draft of this file got
///         wrong.</b> A search stopping at the variable's closing quote is satisfied by
///         <c>SetEnvironmentVariable("VIXEN_REQUIRE_VULKAN", "0")</c> — one character, and the hole
///         this file exists to hold shut is open again with the test green, because every reader in
///         the tree accepts only <c>"1"</c>, <c>"true"</c> or <c>"TRUE"</c>.
///     </para>
///     <para>
///         Comments are stripped before the assertion. The target's own prose names the variable, so
///         the stripping is cheap insurance rather than the thing that makes this falsifiable — the
///         call and its argument are. <see cref="TargetBody" /> drops every <c>//</c> line first, and
///         the slice is checked for a landmark of its own afterwards so that a rename which cut the
///         wrong text cannot pass by having found nothing.
///     </para>
/// </remarks>
public sealed class GoldenImagesGuardTests {
    /// <summary>The suite the target runs, and the landmark that says the slice is the right one.</summary>
    const string GoldenSuite = "Vixen.Graphics.Golden.Tests";

    /// <summary>
    ///     A skipped suite may not be reported as a compared one.
    /// </summary>
    /// <remarks>
    ///     The guard is <c>VIXEN_REQUIRE_VULKAN</c>, which the golden fixtures already read: it turns
    ///     a missing <em>device</em> into a failure and says nothing about a missing
    ///     <em>capability</em>, so the suites that legitimately skip on MoltenVK still skip. Setting
    ///     it here rather than asking every caller to remember it is what makes the default honest.
    /// </remarks>
    [Fact]
    public void GoldenImagesRefusesToPassWithoutADevice() {
        var body = TargetBody("GoldenImages");

        Assert.True(
            body.Contains(GoldenSuite, StringComparison.Ordinal),
            $"The GoldenImages target read out of build/Build.cs does not name {GoldenSuite}, so the "
            + "slice is not the target this file is about and the assertion below would mean nothing."
        );

        Assert.True(
            body.Contains("SetEnvironmentVariable(\"VIXEN_REQUIRE_VULKAN\", \"1\")", StringComparison.Ordinal),
            "GoldenImages does not set VIXEN_REQUIRE_VULKAN, so on a machine with no Vulkan device "
            + "every fixture skips, the target exits 0, and a run that compared no pictures at all "
            + "reports that the golden images are fine."
        );
    }

    /// <summary>
    ///     ⚠ The instrument's own check: the slice has to be a slice of something.
    /// </summary>
    /// <remarks>
    ///     A reader that stopped finding the target would return the empty string, and the empty
    ///     string contains no violation — which is the shape of every check in this repository that
    ///     has ever passed by reading nothing. A body short enough to be a declaration and no
    ///     statements is that failure, so it is named here rather than left to the test above, where
    ///     it would be indistinguishable from a target that carries the guard.
    /// </remarks>
    [Fact]
    public void TheTargetIsFoundAtAll() {
        var body = TargetBody("GoldenImages");

        Assert.True(
            body.Length > 200,
            $"Read {body.Length} character(s) of the GoldenImages target out of build/Build.cs. The "
            + "target has been renamed or the declarations no longer read `Target <Name> =>`, so this "
            + "file is asserting about nothing."
        );
    }

    /// <summary>The text of one Nuke target's declaration, with its comment lines removed.</summary>
    /// <param name="name">The target's property name.</param>
    /// <returns>Everything from that declaration up to the next one, comments stripped.</returns>
    /// <remarks>
    ///     ⚠ Delimited by the next <c>Target</c> declaration rather than by brace matching, because
    ///     the bodies contain braces inside strings and interpolations and a counter would have to
    ///     lex C# to be right. The last target in the file is bounded by the end of the file, which
    ///     is a superset of its body and therefore only ever makes this check weaker in the safe
    ///     direction — it cannot manufacture a pass for a target that has no guard while some later
    ///     one does, because there is no later one.
    /// </remarks>
    static string TargetBody(string name) {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "build", "Build.cs"));
        var start = source.IndexOf($"Target {name} =>", StringComparison.Ordinal);

        if (start < 0) {
            return string.Empty;
        }

        var next = source.IndexOf("    Target ", start + 1, StringComparison.Ordinal);
        var slice = next < 0 ? source[start..] : source[start..next];

        return string.Join(
            '\n',
            slice.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
        );
    }

    /// <summary>The repository root, found by walking up from the compiled assembly.</summary>
    /// <remarks>
    ///     ⚠ Not <c>[CallerFilePath]</c>: CI sets <c>DeterministicSourcePaths</c>, which rewrites the
    ///     repository root to <c>/_/</c> in everything the compiler bakes in, so a path anchored that
    ///     way names a directory that exists on no runner (#943).
    /// </remarks>
    static string RepositoryRoot() {
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
