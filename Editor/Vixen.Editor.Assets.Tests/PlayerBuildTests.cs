// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.Content;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>The half of a player build that is a decision rather than a process.</summary>
/// <remarks>
///     ⚠ <b>Nothing here starts <c>dotnet</c>.</b> What is worth testing about a publish is what it
///     would be told to do — the runtime identifier, the framework, the configuration a variant
///     compiles as, and which targets are refused by name before a minute is spent finding out.
///     Running one is <c>Vixen.Cli.Tests</c>' business and it does not do that either, for the same
///     reason: a test that publishes is a test that needs a network and eight seconds.
/// </remarks>
public class PlayerBuildTests {
    /// <summary>Doc 20's Part C names six targets on the Build menu, and this is that list.</summary>
    [Fact]
    public void EveryTargetTheBuildMenuOffersIsOnTheList() =>
        Assert.Equal(["Windows", "Linux", "MacOS", "Android", "iOS", "Web"], PlayerBuild.Targets);

    /// <summary>And doc 17's variants, less the Editor one, which this build cannot produce.</summary>
    [Fact]
    public void TheVariantsAreDocSeventeensLessTheEditorOne() {
        Assert.Equal(["Debug", "Development", "Release", "Server"], PlayerBuild.Variants);
        Assert.DoesNotContain("Editor", PlayerBuild.Variants);
    }

    /// <summary>
    ///     ⚠ Development is optimised and keeps its diagnostics, which is the whole reason doc 17
    ///     lists it separately — building it as Debug would make every number measured in a playtest
    ///     a lie.
    /// </summary>
    [Theory]
    [InlineData("Debug", "Debug")]
    [InlineData("Development", "Release")]
    [InlineData("Release", "Release")]
    [InlineData("Server", "Release")]
    public void OnlyDebugIsCompiledUnoptimised(string variant, string configuration) =>
        Assert.Equal(configuration, PlayerBuild.ConfigurationFor(variant));

    /// <summary>
    ///     ⚠ Web is on the menu and is not publishable, and the two facts have to be reachable
    ///     together: doc 20's bar is that a verb which is not implemented is <i>visibly</i> not
    ///     implemented, so the target carries the sentence the editor greys it with.
    /// </summary>
    [Fact]
    public void WebIsOfferedAndRefusedWithItsOwnReason() {
        Assert.Contains("Web", PlayerBuild.Targets);
        Assert.False(PlayerBuild.TryDescribe("Web", out _));

        var why = PlayerBuild.WhyNotPublishable("Web");

        Assert.NotNull(why);
        Assert.Contains("wasm", why, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unknown target is refused by name, and the sentence says what to try instead.</summary>
    [Fact]
    public void AnUnknownTargetSaysWhatIsPublishable() {
        var why = PlayerBuild.WhyNotPublishable("Dreamcast");

        Assert.NotNull(why);
        Assert.Contains("Dreamcast", why, StringComparison.Ordinal);
        Assert.Contains("Windows", why, StringComparison.Ordinal);
    }

    /// <summary>Every publishable target has one, and it is null for the ones that do not.</summary>
    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    [InlineData("MacOS")]
    [InlineData("Android")]
    [InlineData("iOS")]
    public void APublishableTargetHasNoRefusal(string target) {
        Assert.True(PlayerBuild.TryDescribe(target, out _));
        Assert.Null(PlayerBuild.WhyNotPublishable(target));
    }

    /// <summary>
    ///     ⚠ A directory with two project files is a solution, and guessing which one is the game is
    ///     how a tool publishes the wrong thing quietly.
    /// </summary>
    [Fact]
    public void TwoProjectFilesAreRefusedRatherThanPickedBetween() {
        var root = Directory.CreateTempSubdirectory("vixen-build").FullName;

        try {
            Assert.False(PlayerBuild.TryFindProjectFile(root, out _));

            File.WriteAllText(Path.Combine(root, "Game.csproj"), "<Project />");
            Assert.True(PlayerBuild.TryFindProjectFile(root, out var found));
            Assert.Equal("Game.csproj", Path.GetFileName(found));

            File.WriteAllText(Path.Combine(root, "Tests.csproj"), "<Project />");
            Assert.False(PlayerBuild.TryFindProjectFile(root, out _));
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    ///     ⚠ <b>The two halves joined, because separately they both passed while the editor was
    ///     broken.</b> A new project used to be two directories, so this call answered false for
    ///     every project the editor made and Build and Run was greyed for all of them. What has to be
    ///     true is not "the scaffold writes files" and not "this finds a project file" — it is that
    ///     what the first writes is what the second finds.
    /// </summary>
    [Fact]
    public void AScaffoldedProjectIsOneThisCanPublish() {
        var root = Directory.CreateTempSubdirectory("vixen-scaffold-build").FullName;

        try {
            Assert.False(PlayerBuild.TryFindProjectFile(root, out _));
            Assert.True(Core.ProjectScaffold.Write("game", "Asteroids", root).Succeeded);

            Assert.True(PlayerBuild.TryFindProjectFile(root, out var found));
            Assert.Equal("Asteroids.csproj", Path.GetFileName(found));
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A directory that is not there is the same answer as one with nothing in it.</summary>
    /// <remarks>
    ///     Worth a line because the editor asks this every frame the Build menu is open — the
    ///     enablement of Build and Run is this call — and a scratch project whose root has not been
    ///     created yet is the ordinary first-run state.
    /// </remarks>
    [Fact]
    public void AMissingDirectoryHasNoProjectFileRatherThanThrowing() =>
        Assert.False(PlayerBuild.TryFindProjectFile(Path.Combine(Path.GetTempPath(), "vixen-not-here"), out _));
}
