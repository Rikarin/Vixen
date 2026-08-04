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

    /// <summary>
    ///     ⚠ The name here has to be one that will never become a template, and the previous one was
    ///     not: this asked about <c>mmo</c> until doc 27's <c>vixen-mmo</c> landed and turned the
    ///     test's premise into a real short name. A test whose example the roadmap can make true is
    ///     a test that fails on the day a feature ships.
    /// </summary>
    [Fact]
    public void An_unknown_template_is_refused_by_name() {
        var result = ProjectScaffold.Write("not-a-template", "Asteroids", root);

        Assert.False(result.Succeeded);
        Assert.Contains("not-a-template", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    ///     And the template that broke the one above is genuinely there, which is the other half of
    ///     what that failure was telling us.
    /// </summary>
    [Fact]
    public void The_mmo_template_is_one_the_editor_can_write() {
        Assert.True(TemplateCatalog.TryFind("mmo", out var template));
        Assert.NotNull(template);
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

/// <summary>The file that says a directory is a project, which doc 08 named and nothing wrote.</summary>
public class ProjectMarkerTests : IDisposable {
    readonly string root = Directory.CreateTempSubdirectory("vixen-marker").FullName;

    public void Dispose() {
        GC.SuppressFinalize(this);

        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A scaffolded game carries one, which is the only way projects come to have them.</summary>
    [Fact]
    public void A_new_project_is_marked_as_one() {
        Assert.True(ProjectScaffold.Write("game", "Asteroids", root).Succeeded);

        Assert.True(ProjectMarker.TryFind(root, out var path));
        Assert.Equal("Asteroids" + ProjectMarker.Extension, Path.GetFileName(path));

        Assert.True(ProjectMarker.TryRead(root, out var marker));
        Assert.Equal(ProjectMarker.CurrentFormat, marker.Format);
        Assert.Equal(ProjectScaffold.SdkVersion, marker.Engine);
    }

    /// <summary>
    ///     ⚠ The reason the marker records a version: opening a project built against a newer engine
    ///     fails later and stranger, and being told at the door is the whole value of the field.
    /// </summary>
    [Fact]
    public void A_project_from_a_newer_engine_is_recognised_as_one() {
        File.WriteAllText(Path.Combine(root, "Asteroids.vxproj"), ProjectMarker.Write("99.0.0"));

        Assert.True(ProjectMarker.TryRead(root, out var marker));
        Assert.True(ProjectMarker.IsFromTheFuture(marker, "0.1.0"));

        // Older is the ordinary case and must never warn, and an unparseable version on either side
        // is not evidence of anything.
        Assert.False(ProjectMarker.IsFromTheFuture(marker, "99.1.0"));
        Assert.False(ProjectMarker.IsFromTheFuture(marker with { Engine = "nightly" }, "0.1.0"));
    }

    /// <summary>
    ///     ⚠ A format this build does not understand is found and not read. Binding the half of it
    ///     that is recognised would be worse than saying nothing, because a later format may change
    ///     what a field means rather than which fields there are.
    /// </summary>
    [Fact]
    public void A_marker_from_a_future_format_is_not_bound() {
        File.WriteAllText(Path.Combine(root, "Asteroids.vxproj"), "format: 99\nengine: 1.0.0\n");

        Assert.True(ProjectMarker.TryFind(root, out _));
        Assert.False(ProjectMarker.TryRead(root, out _));
    }

    /// <summary>
    ///     ⚠ A file that will not parse is not an editor that will not open the project — the same
    ///     bargain the keymap and the preferences file make.
    /// </summary>
    [Fact]
    public void A_broken_marker_is_ignored_rather_than_thrown_on() {
        File.WriteAllText(Path.Combine(root, "Asteroids.vxproj"), "format: : :\n\t- broken");

        Assert.False(ProjectMarker.TryRead(root, out _));
    }

    /// <summary>Two markers in one directory is two projects sharing an Assets/, so neither wins.</summary>
    [Fact]
    public void Two_markers_are_refused_rather_than_picked_between() {
        File.WriteAllText(Path.Combine(root, "One.vxproj"), ProjectMarker.Write("0.1.0"));
        File.WriteAllText(Path.Combine(root, "Two.vxproj"), ProjectMarker.Write("0.1.0"));

        Assert.False(ProjectMarker.TryFind(root, out _));
    }
}
