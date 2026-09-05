// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.Content;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Assets.Scenes;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.Core;
using Vixen.Testing;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     What <see cref="FixtureProject" /> claims about the project it wrote, checked against the
///     pipeline that reads it.
/// </summary>
/// <remarks>
///     <para>
///         A fixture generator is an instrument, and the question this repository asks of every
///         instrument is what it reports on the day it did not run. A generator that wrote nothing
///         returns a project of nought assets, over which "everything imported" and "nothing failed"
///         are both true — so the refusals are what is pinned here, and the claim that the kinds are
///         real assets rather than files with the right suffixes.
///     </para>
///     <para>
///         ⚠ <b>The extensions are the test.</b> Nothing in this pipeline fails when a file's kind is
///         not recognised: <c>RawImporter</c> takes what nothing else claimed, succeeds, and is
///         counted — which is how a <c>.vxwaves</c> became a byte blob no runtime reader resolves,
///         and how five more attributed importers were found missing from
///         <c>BuiltInImporters</c> afterwards. So a fixture whose "textures" are not textures reads
///         exactly like one whose textures are, unless somebody asks which importer claimed them.
///     </para>
/// </remarks>
public sealed class FixtureProjectTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-fixture-project-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    /// <summary>
    ///     Each kind is imported by the importer for that kind, and the fixture's own entry count is
    ///     what the pipeline found.
    /// </summary>
    /// <remarks>
    ///     The counts are small because the claim is per kind rather than about scale — the scale
    ///     test next door drives the same generator at ten thousand. Three of each so that a
    ///     miscounted loop and a per-kind directory are both visible in the numbers.
    /// </remarks>
    [Fact]
    public async Task Each_kind_reaches_the_importer_that_claims_it() {
        var project = new FixtureProject { Root = root, Textures = 3, Models = 3, Scenes = 3 }.Write();
        List<ImportProgress> steps = [];

        var summary = await ContentPipeline.ImportAsync(
            new ProjectWorkspace(new ProjectPaths(project.Root)),
            "Windows",
            diagnostic => Assert.NotEqual(ImportSeverity.Error, diagnostic.Severity),
            steps.Add,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Nine files in three folders, and the folders import too. Asserted against the fixture's own
        // number rather than against nine, because that number is what every scale assertion in
        // ImportBudgetTests is derived from: if it did not describe the project, this passes and
        // that suite asserts the fixture's arithmetic instead of the pipeline's.
        Assert.Equal(project.Entries, summary.Imported);
        Assert.Equal(0, summary.Failed);

        // ⚠ The three claims that make the kinds mean anything. A PNG of the wrong bytes fails to
        // decode and lands in Failed above; a PNG nothing claims succeeds as a raw blob and is
        // counted as an import, which the line above cannot tell from a texture.
        Claimed(steps, ".png", new TextureImporter().Name, 3);
        Claimed(steps, ".obj", new ModelImporter().Name, 3);
        Claimed(steps, ".vxscene", new SceneImporter().Name, 3);
    }

    /// <summary>A project with nothing in it is refused rather than returned.</summary>
    /// <remarks>
    ///     The failure this stands in for is a scale variable read as zero, or a fixture written to a
    ///     directory the test then imports a different one of. Both arrive as an empty project, and
    ///     an empty project passes every assertion a scale test makes: nought imported of nought
    ///     entries, nought failed, nought cached the second time.
    /// </remarks>
    [Fact]
    public void A_project_with_no_assets_is_refused() {
        var refusal = Assert.Throws<InvalidOperationException>(() => new FixtureProject { Root = root }.Write());

        Assert.Contains("vacuous", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Writing a second fixture over the first is refused rather than counted.</summary>
    /// <remarks>
    ///     ⚠ The counts describe the directory and not the call, so a second fixture in the same
    ///     place returns numbers that are short by everything already there — and short in the
    ///     direction that still passes, because the import finds those files too and reports them as
    ///     imported.
    /// </remarks>
    [Fact]
    public void A_second_fixture_in_the_same_directory_is_refused() {
        new FixtureProject { Root = root, Blobs = 2 }.Write();

        var refusal = Assert.Throws<InvalidOperationException>(
            () => new FixtureProject { Root = root, Blobs = 2 }.Write()
        );

        Assert.Contains("already has something in it", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Every asset with this extension was imported by this importer, and there are this many.</summary>
    /// <remarks>
    ///     Both halves, because either alone is green on the failure it exists to catch: the count
    ///     alone passes when the files fell through to the fallback, and the importer alone passes
    ///     when the fixture wrote one file instead of three — or none, in which case
    ///     <c>Assert.All</c> over an empty sequence is the vacuous pass this repository keeps
    ///     finding.
    /// </remarks>
    static void Claimed(List<ImportProgress> steps, string extension, string importer, int expected) {
        var kind = steps.Where(step => step.Path.EndsWith(extension, StringComparison.Ordinal)).ToList();

        Assert.Equal(expected, kind.Count);
        Assert.All(kind, step => Assert.Equal(importer, step.Outcome.Importer));
    }
}
