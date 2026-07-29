// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>Writing a new project: what lands, what is refused, and what is never overwritten.</summary>
/// <remarks>
///     ⚠ <b>The decisions moved here from <c>Tools/Vixen.Cli</c> because the editor's New Project
///     needed them</b>, so this is where they are tested. What the CLI still owns — the listing, the
///     exit code, the "what to type next" lines — is covered by <c>Vixen.Cli.Tests</c> driving the
///     real parser.
/// </remarks>
public class ProjectScaffoldTests : IDisposable {
    readonly string root = Directory.CreateTempSubdirectory("vixen-scaffold").FullName;

    public void Dispose() {
        GC.SuppressFinalize(this);

        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    ///     ⚠ The one that matters, and the regression it pins is a real one: the editor's New Project
    ///     made two directories, so every project it created had no <c>.csproj</c> — and Build and Run
    ///     was greyed for all of them, pointing at a terminal command.
    /// </summary>
    [Fact]
    public void A_new_game_project_has_a_project_file_named_after_it() {
        var result = ProjectScaffold.Write("game", "Asteroids", root);

        Assert.True(result.Succeeded);
        Assert.Contains("Asteroids.csproj", result.Written);
        Assert.True(File.Exists(Path.Combine(root, "Asteroids.csproj")));

        // And it is the SDK-shaped one, which is what makes `dotnet publish` work with nothing else
        // written down — see Tools/Vixen.Sdk.
        Assert.Contains(
            $"Sdk=\"Vixen.Sdk/{ProjectScaffold.SdkVersion}\"",
            File.ReadAllText(Path.Combine(root, "Asteroids.csproj")),
            StringComparison.Ordinal
        );

        // The other half of a game: something to run, and somewhere for content to go.
        Assert.Contains("Program.cs", result.Written);
        Assert.Contains(result.Written, path => path.StartsWith("Assets/", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ Every collision is found before anything is written. A half-scaffolded directory is
    ///     worse than an untouched one, because the second is obviously a no-op and the first is not.
    /// </summary>
    [Fact]
    public void A_directory_that_already_has_one_of_the_files_is_left_entirely_alone() {
        File.WriteAllText(Path.Combine(root, "Program.cs"), "// mine");

        var result = ProjectScaffold.Write("game", "Asteroids", root);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Written);
        Assert.Contains("Program.cs", result.Collisions);

        Assert.Equal("// mine", File.ReadAllText(Path.Combine(root, "Program.cs")));
        Assert.False(File.Exists(Path.Combine(root, "Asteroids.csproj")));
    }

    [Fact]
    public void An_unusable_name_is_refused_before_anything_is_written() {
        var result = ProjectScaffold.Write("game", "2 Fast", root);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Written);
        Assert.Contains("namespace", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public void An_unknown_template_is_refused_by_name() {
        var result = ProjectScaffold.Write("mmo", "Asteroids", root);

        Assert.False(result.Succeeded);
        Assert.Contains("mmo", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The editor does not ask for a name — it gets whatever folder somebody picked in a file
    ///     dialog, and refusing that would be the editor rejecting a directory it had just watched
    ///     them create. The CLI keeps refusing, because there the name is an argument.
    /// </summary>
    [Theory]
    [InlineData("Asteroids", "Asteroids")]
    [InlineData("my game (2)", "mygame2")]
    [InlineData("2024-jam", "Game2024jam")]
    [InlineData("", "Game")]
    [InlineData("...", "Game")]
    public void A_folder_name_is_cleaned_into_something_that_can_be_a_namespace(string folder, string expected) {
        var name = ProjectScaffold.NameFrom(folder);

        Assert.Equal(expected, name);
        Assert.True(ProjectScaffold.IsUsableName(name));
    }
}
