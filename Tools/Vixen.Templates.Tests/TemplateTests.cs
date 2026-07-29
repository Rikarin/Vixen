// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Cli;
using Xunit;

namespace Vixen.Templates.Tests;

/// <summary>What has to be true of every template, and of each one in particular.</summary>
public class TemplateTests {
    public static TheoryData<string> Templates =>
        new(TemplateCatalog.All.Select(template => template.Id));

    static ProjectTemplate Template(string id) =>
        TemplateCatalog.All.Single(template => template.Id == id);

    static string TextOf(ProjectTemplate template, string projectName, string path) =>
        Encoding.UTF8.GetString(
            template.Instantiate(projectName, ScaffoldRunner.SdkVersion)
                .Single(file => file.Path == path)
                .Content
        );

    // ── Every template ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The three doc 17 says are writable today. `vixen-plugin` and `vixen-tool` are named there
    ///     too and are not here — the first needs `Vixen.Editor.Plugin`, which does not exist, and a
    ///     template pinning a package nobody publishes is worse than no template at all.
    /// </summary>
    [Fact]
    public void TheTemplatesAreTheOnesThatCanBeWrittenToday() {
        string[] expected = ["vixen-app", "vixen-game", "vixen-lib"];

        Assert.Equal(expected, TemplateCatalog.All.Select(template => template.Id).ToArray());
    }

    /// <summary>
    ///     A short name is what somebody types, so two templates answering to one of them is a
    ///     coin toss dressed as a command.
    /// </summary>
    [Fact]
    public void NoTwoTemplatesShareAShortName() {
        var names = TemplateCatalog.All.SelectMany(template => template.ShortNames).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    ///     Every template says what it is, because `vixen new nonsense` lists them and a list of
    ///     blank lines helps nobody.
    /// </summary>
    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryTemplateIsDescribed(string id) {
        var template = Template(id);

        Assert.NotEmpty(template.Name);
        Assert.NotEmpty(template.Description);
        Assert.NotEmpty(template.ShortNames);
        Assert.NotEmpty(template.SourceName);
    }

    /// <summary>
    ///     ⚠ <b>The one rule that keeps `vixen new` and `dotnet new` from drifting.</b> The template
    ///     engine substitutes <i>derived forms</i> of <c>sourceName</c> as well as the name itself —
    ///     a lower-cased <c>vixengame1</c> in a comment becomes <c>asteroids</c> — and
    ///     <see cref="TemplateCatalog" /> deliberately implements identity substitution and nothing
    ///     else, because a second partial implementation of a templating language is a thing that
    ///     silently disagrees with the real one.
    ///     <para>
    ///         So the templates are written to need only identity: every mention of the source name
    ///         is spelled exactly. A file that reads `vixengame1-server` would come out of the two
    ///         paths differently, and this is what says so before anybody ships it.
    ///     </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Templates))]
    public void NoTemplateMentionsItsSourceNameInAnyOtherCasing(string id) {
        var template = Template(id);

        foreach (var file in template.Instantiate(template.SourceName, "0.0.0")) {
            if (!TemplateCatalog.IsTextFile(file.Content)) {
                continue;
            }

            var text = Encoding.UTF8.GetString(file.Content);

            for (var at = 0; at >= 0;) {
                at = text.IndexOf(template.SourceName, at, StringComparison.OrdinalIgnoreCase);

                if (at < 0) {
                    break;
                }

                Assert.Equal(
                    template.SourceName,
                    text.Substring(at, template.SourceName.Length)
                );

                at += template.SourceName.Length;
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>The version token is replaced twice, by two different things, and they have to agree
    ///     on which files carry it.</b> `Vixen.Templates.csproj` rewrites project files and the
    ///     Dockerfile at pack time; <see cref="TemplateCatalog" /> rewrites every text file at
    ///     scaffold time. A token in a `.cs` file would therefore survive into the package and not
    ///     into the CLI's output — which is a difference nobody would look for.
    /// </summary>
    [Theory]
    [MemberData(nameof(Templates))]
    public void TheVersionTokenAppearsOnlyWherePackingReplacesIt(string id) {
        foreach (var file in Template(id).Instantiate("Probe", TemplateCatalog.VersionToken)) {
            if (!Encoding.UTF8.GetString(file.Content).Contains(TemplateCatalog.VersionToken, StringComparison.Ordinal)) {
                continue;
            }

            Assert.True(
                file.Path.EndsWith(".csproj", StringComparison.Ordinal)
                || Path.GetFileName(file.Path) == "Dockerfile",
                $"{id}/{file.Path} carries the version token, which pack time does not replace there."
            );
        }
    }

    /// <summary>Nothing scaffolded is left asking for a version nobody filled in.</summary>
    [Theory]
    [MemberData(nameof(Templates))]
    public void NothingScaffoldedStillHoldsTheToken(string id) {
        foreach (var file in Template(id).Instantiate("Probe", "1.2.3")) {
            Assert.DoesNotContain(
                TemplateCatalog.VersionToken,
                Encoding.UTF8.GetString(file.Content),
                StringComparison.Ordinal
            );
        }
    }

    /// <summary>
    ///     Every template writes a project file named after the project, because that is the name the
    ///     assembly, the namespace and the output binary all take.
    /// </summary>
    [Theory]
    [MemberData(nameof(Templates))]
    public void TheProjectFileIsNamedAfterTheProject(string id) {
        var files = Template(id).Instantiate("Kestrel", "1.2.3").Select(file => file.Path).ToList();

        Assert.Contains("Kestrel.csproj", files);
        Assert.Single(files, path => path.EndsWith(".csproj", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ <b>A binary file is copied, never substituted into.</b> Compiled shader modules are
    ///     bytecode, and a project name rewritten into the middle of a SPIR-V word is a device lost
    ///     rather than a compile error. <see cref="TemplateCatalog" /> decides by looking for a NUL
    ///     byte, which is how `git` answers the same question.
    /// </summary>
    [Fact]
    public void CompiledShadersComeThroughUntouched() {
        var app = Template("vixen-app");

        var before = app.Instantiate(app.SourceName, "0.0.0");
        var after = app.Instantiate("SomethingRatherLonger", "9.9.9-preview.1");

        foreach (var file in after.Where(entry => entry.Path.EndsWith(".spv", StringComparison.Ordinal))) {
            Assert.Equal(
                before.Single(entry => entry.Path == file.Path).Content,
                file.Content
            );
        }

        Assert.Contains(after, entry => entry.Path.EndsWith(".spv", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The whole point of the gate: the C# a template writes compiles against the assemblies its
    ///     package references resolve to.
    /// </summary>
    [Theory]
    [MemberData(nameof(Templates))]
    public void WhatEachTemplateWritesCompiles(string id) {
        var errors = TemplateCompiler.Errors(Template(id), "Kestrel");

        // Joined rather than asserted as a collection: the compiler's message is the whole value of
        // this test, and Assert.Empty prints five truncated ones.
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    // ── vixen-game ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A scaffolded game is a project the SDK drives, which is why `new` waited for `Vixen.Sdk`
    ///     to exist: the alternative is a template listing package references that are wrong one
    ///     release later.
    /// </summary>
    [Fact]
    public void TheGameProjectIsDrivenByTheSdk() {
        var project = TextOf(Template("vixen-game"), "Asteroids", "Asteroids.csproj");

        Assert.Contains($"Sdk=\"Vixen.Sdk/{ScaffoldRunner.SdkVersion}\"", project, StringComparison.Ordinal);
        Assert.Contains("Vixen.App", project, StringComparison.Ordinal);
    }

    /// <summary>The name reaches both places it has to: the namespace and the type the host starts.</summary>
    [Fact]
    public void TheGameNameIsBothANamespaceAndATypeName() {
        var game = Template("vixen-game");

        Assert.Contains(
            "VixenApp.Run<AsteroidsGame>(args)",
            TextOf(game, "Asteroids", "Program.cs"),
            StringComparison.Ordinal
        );

        Assert.Contains(
            "public sealed class AsteroidsGame : Game",
            TextOf(game, "Asteroids", "AsteroidsGame.cs"),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     docs/plan/17 § Q5c: a `Dockerfile` ships in `vixen-game` rather than the engine growing
    ///     container tooling. It builds the server variant, because that is the head a container is
    ///     for — a client in a container has no display.
    /// </summary>
    [Fact]
    public void TheGameShipsADockerfileForTheServerVariant() {
        var docker = TextOf(Template("vixen-game"), "Asteroids", "Dockerfile");

        Assert.Contains("VixenVariant=Server", docker, StringComparison.Ordinal);
        Assert.Contains($"Vixen.Cli --version {ScaffoldRunner.SdkVersion}", docker, StringComparison.Ordinal);

        // Doc 17 asks for a distroless-ish base and a non-root user, and both are one line each.
        Assert.Contains("chiseled", docker, StringComparison.Ordinal);
        Assert.Contains("USER $APP_UID", docker, StringComparison.Ordinal);
    }

    /// <summary>
    ///     `Library/` is the import cache and the artefact database: reproducible from the assets,
    ///     large, and binary. A project that commits it has merge conflicts on every branch.
    /// </summary>
    [Fact]
    public void TheGameKeepsItsImportCacheOutOfHistory() =>
        Assert.Contains("Library/", TextOf(Template("vixen-game"), "Asteroids", ".gitignore"), StringComparison.Ordinal);

    // ── vixen-app ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The template's whole reason for existing, asserted.</b> docs/plan/17 § Project
    ///     templates makes `vixen-app` "the practical test that the `Vixen.Ui` ⇸ `Vixen.Engine`
    ///     boundary holds" — so it must not reference `Vixen.Engine`, and it must not reach it the
    ///     easy way either, through `Vixen.App`, which does.
    /// </summary>
    [Fact]
    public void TheApplicationTemplateReferencesNoEngine() {
        var project = TextOf(Template("vixen-app"), "Painter", "Painter.csproj");

        Assert.DoesNotContain("Include=\"Vixen.Engine\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Include=\"Vixen.App\"", project, StringComparison.Ordinal);
        Assert.Contains("Include=\"Vixen.Ui.Controls\"", project, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An application with no assets has nothing to import and no content to build, so the SDK
    ///     would add two no-op build steps and a tool dependency for nothing.
    /// </summary>
    [Fact]
    public void TheApplicationTemplateDoesNotUseTheSdk() =>
        Assert.Contains(
            "Sdk=\"Microsoft.NET.Sdk\"",
            TextOf(Template("vixen-app"), "Painter", "Painter.csproj"),
            StringComparison.Ordinal
        );

    // ── vixen-lib ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A library gets no SDK either, and for the same reason — and it answers to both spellings,
    ///     because `library` is what the CLI took before the pack existed and breaking it would buy
    ///     nothing.
    /// </summary>
    [Fact]
    public void TheLibraryTemplateDoesNotUseTheSdk() {
        var library = Template("vixen-lib");

        Assert.Contains("lib", library.ShortNames);
        Assert.Contains("library", library.ShortNames);

        var project = TextOf(library, "Physics", "Physics.csproj");

        Assert.Contains("Microsoft.NET.Sdk", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Vixen.Sdk", project, StringComparison.Ordinal);
    }
}
