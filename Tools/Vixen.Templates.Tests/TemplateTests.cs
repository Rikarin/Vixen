// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Cli;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
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
    ///     The five that are written. `vixen-tool` — doc 17 § Q5d's headless batch head — is named
    ///     in doc 17 too and is not here; it is owed rather than blocked, because
    ///     `Vixen.Platform.Headless` exists and nobody has written the template. `vixen-plugin` was
    ///     in that sentence until `Vixen.Editor.Plugin` landed in wave W0-12, which is what a
    ///     template pinning a package nobody publishes was waiting on. This list is what has to be
    ///     edited when the last one arrives.
    /// </summary>
    [Fact]
    public void TheTemplatesAreTheOnesThatCanBeWrittenToday() {
        string[] expected = ["vixen-app", "vixen-game", "vixen-lib", "vixen-mmo", "vixen-plugin"];

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
                || file.Path.EndsWith(ProjectMarker.Extension, StringComparison.Ordinal)
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
    ///     Every project a template writes is named after the project, because that is the name the
    ///     assembly, the namespace and the output binary all take.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Single-project templates put the <c>.csproj</c> at the root; a multi-project one puts
    ///     each in a directory of its own, both named after the project with a suffix.</b> Anything
    ///     else is a directory whose name says nothing about what is in it, which for
    ///     <c>vixen-mmo</c>'s four projects is the difference between a scaffold somebody can read
    ///     and four folders of C#.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryProjectFileIsNamedAfterTheProject(string id) {
        var files = Template(id).Instantiate("Kestrel", "1.2.3").Select(file => file.Path).ToList();
        var projects = files.Where(path => path.EndsWith(".csproj", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(projects);

        if (projects.Count == 1) {
            Assert.Equal("Kestrel.csproj", projects[0]);

            return;
        }

        foreach (var project in projects) {
            var directory = Path.GetDirectoryName(project)!;

            Assert.StartsWith("Kestrel.", directory, StringComparison.Ordinal);
            Assert.Equal($"{directory}/{directory}.csproj", project);
        }
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

    // ── vixen-mmo ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The reference graph is the template, and this is the gate on it.</b> docs/plan/27 §
    ///     The three assemblies a game writes: "getting this graph wrong on day one is the kind of
    ///     mistake that is discovered in month six". <see cref="TemplateCompiler" /> deliberately
    ///     cannot check it — it compiles every project of a multi-project template together — so the
    ///     project files are read instead, which is where the graph is written down anyway.
    /// </summary>
    [Fact]
    public void TheMmoTemplateWiresTheFourProjectsTheWayTheDocumentSays() {
        var mmo = Template("vixen-mmo");

        var contracts = TextOf(mmo, "Kestrel", "Kestrel.Contracts/Kestrel.Contracts.csproj");
        var shared = TextOf(mmo, "Kestrel", "Kestrel.Shared/Kestrel.Shared.csproj");
        var realm = TextOf(mmo, "Kestrel", "Kestrel.Realm/Kestrel.Realm.csproj");
        var client = TextOf(mmo, "Kestrel", "Kestrel.Client/Kestrel.Client.csproj");

        // Contracts is seen by everybody, so it names the wire and the shard vocabulary and nothing
        // else — no engine, and no project references at all.
        Assert.Contains("Include=\"Vixen.Live.Abstractions\"", contracts, StringComparison.Ordinal);
        Assert.Contains("Include=\"Vixen.Net\"", contracts, StringComparison.Ordinal);
        Assert.DoesNotContain("Include=\"Vixen.Engine\"", contracts, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", contracts, StringComparison.Ordinal);

        // Shared is the rules both ends run, so it sits on Contracts and on nothing that only one
        // end has.
        Assert.Contains("Kestrel.Contracts.csproj", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("Vixen.Live.Realm", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("Vixen.App", shared, StringComparison.Ordinal);

        foreach (var end in new[] { realm, client }) {
            Assert.Contains("Kestrel.Contracts.csproj", end, StringComparison.Ordinal);
            Assert.Contains("Kestrel.Shared.csproj", end, StringComparison.Ordinal);
        }

        Assert.Contains("Include=\"Vixen.Live.Realm\"", realm, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ADR-017, made mechanical rather than remembered: the client physically cannot reach a
    ///     grain, because no assembly it references has one in it. A cluster client is a peer of the
    ///     cluster, and this one runs on somebody else's machine.
    /// </summary>
    [Fact]
    public void TheMmoClientLinksNothingFromTheControlPlane() {
        var mmo = Template("vixen-mmo");

        foreach (var file in mmo.Instantiate("Kestrel", "1.2.3")) {
            if (!file.Path.StartsWith("Kestrel.Client/", StringComparison.Ordinal)
                && !file.Path.StartsWith("Kestrel.Contracts/", StringComparison.Ordinal)
                && !file.Path.StartsWith("Kestrel.Shared/", StringComparison.Ordinal)) {
                continue;
            }

            var text = Encoding.UTF8.GetString(file.Content);

            // What is referenced, rather than what is mentioned: the Contracts project's comment
            // says the word "Orleans" and saying it is the point of the comment.
            Assert.DoesNotContain("Include=\"Microsoft.Orleans", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Include=\"Vixen.Live.Realm\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Include=\"Vixen.Live.Cluster\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("using Orleans", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     A realm's container has to keep its standard input, because that is where its lifecycle
    ///     lives: it writes `vixen-realm ready` and reads `vixen-realm drain`. A container with no
    ///     stdin is a shard that can be killed and not drained.
    /// </summary>
    [Fact]
    public void TheMmoRealmShipsADockerfileThatKeepsItsLifecycleChannel() {
        var docker = TextOf(Template("vixen-mmo"), "Kestrel", "Kestrel.Realm/Dockerfile");

        Assert.Contains("VixenVariant=Server", docker, StringComparison.Ordinal);
        Assert.Contains("docker run --rm -i", docker, StringComparison.Ordinal);
        Assert.Contains("chiseled", docker, StringComparison.Ordinal);
        Assert.Contains("USER $APP_UID", docker, StringComparison.Ordinal);
        Assert.Contains($"Vixen.Cli --version {ScaffoldRunner.SdkVersion}", docker, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The three projects doc 27 also lists — `.Cluster`, `.Orchestrator` and `.Gate` — are not
    ///     here, and this is what says so out loud. Each needs a package that does not exist yet
    ///     (milestones L1 and L3), and a template pinning a package nobody publishes is worse than no
    ///     template at all — which is the same judgement `vixen-plugin` waited on.
    /// </summary>
    [Fact]
    public void TheMmoTemplateScaffoldsOnlyWhatItCanReference() {
        var directories = Template("vixen-mmo")
            .Instantiate("Kestrel", "1.2.3")
            .Select(file => file.Path.Contains('/', StringComparison.Ordinal)
                ? file.Path[..file.Path.IndexOf('/', StringComparison.Ordinal)]
                : ""
            )
            .Where(directory => directory.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            ["Kestrel.Client", "Kestrel.Content", "Kestrel.Contracts", "Kestrel.Realm", "Kestrel.Shared"],
            directories
        );
    }

    // ── vixen-plugin ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The manifest is read by the loader before any of this project's code runs, so a
    ///     scaffold that writes an invalid one fails at the only moment its author has no context to
    ///     debug it.</b> Parsed here by the same <c>YamlSerializer</c> the editor uses, and put
    ///     through <see cref="PluginManifest.Problems" />, which is the check the editor performs —
    ///     rather than a second opinion about YAML written in a test.
    /// </summary>
    [Fact]
    public void TheEditorPluginTemplateWritesAManifestTheLoaderAccepts() {
        var manifest = YamlSerializer.Parse<PluginManifest>(
            TextOf(Template("vixen-plugin"), "Kestrel", PluginManifest.FileName)
        );

        Assert.Empty(manifest.Problems());

        // The name and the assembly are the project's; the id is not, because a reverse-domain name
        // cannot be derived from one and lower-casing the project name would produce a plugin id
        // every scaffold on earth shares.
        Assert.Equal("Kestrel", manifest.Name);
        Assert.Equal("Kestrel.dll", manifest.AssemblyFileName);
        Assert.NotEqual("kestrel", manifest.Id);
    }

    /// <summary>
    ///     ⚠ <b>The one thing about this template that goes stale silently.</b> Before 1.0 the
    ///     editor refuses a plugin whose <c>api</c> minor differs from its own, so the day
    ///     <see cref="EditorApi.Version" /> moves, every project scaffolded from this template
    ///     produces a plugin the editor will not load — with nothing in the build to say so, because
    ///     the manifest is data and compiles fine. This is what says so.
    /// </summary>
    [Fact]
    public void TheEditorPluginTemplateDeclaresTheApiThisEditorImplements() {
        var manifest = YamlSerializer.Parse<PluginManifest>(
            TextOf(Template("vixen-plugin"), "Kestrel", PluginManifest.FileName)
        );

        Assert.Equal(EditorApi.Version, manifest.Api);
        Assert.True(EditorApi.IsCompatible(manifest.Api));
    }

    /// <summary>
    ///     A plugin is a class library that is loaded rather than launched, and both halves of that
    ///     sentence are a line in the project file: no <c>OutputType</c>, and
    ///     <c>EnableDynamicLoading</c> — which is what writes the <c>.deps.json</c> the plugin's
    ///     <c>AssemblyLoadContext</c> resolves everything else through. Without it a plugin runs on
    ///     the machine that built it and on no other, which is the worst of the two failures to have.
    /// </summary>
    /// <remarks>
    ///     No <c>Vixen.Sdk</c>, for the reason <c>vixen-app</c> and <c>vixen-lib</c> do without it:
    ///     a plugin has no assets to import and no content to build.
    /// </remarks>
    [Fact]
    public void TheEditorPluginTemplateIsALibraryTheEditorCanLoad() {
        var project = TextOf(Template("vixen-plugin"), "Kestrel", "Kestrel.csproj");

        Assert.Contains("Sdk=\"Microsoft.NET.Sdk\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<OutputType>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Vixen.Sdk", project, StringComparison.Ordinal);
        Assert.Contains("<EnableDynamicLoading>true</EnableDynamicLoading>", project, StringComparison.Ordinal);

        // One package reference, deliberately: the contract names Vixen.Editor.Ui and reaches
        // everything else through PluginContext.Services, so a plugin that adds a menu item does not
        // pay for an importer it never calls.
        Assert.Contains("Include=\"Vixen.Editor.Plugin\"", project, StringComparison.Ordinal);

        // The manifest travels with the assembly, so the build output is a plugin folder.
        Assert.Contains($"Update=\"{PluginManifest.FileName}\"", project, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The point of shipping this template at all: it demonstrates the door a third party
    ///     uses, not a shortcut past it.</b> Everything registered on the
    ///     <see cref="PluginContext" /> is recorded and undone on unload; the same registration made
    ///     against <c>context.Shell</c> directly works, and leaks the whole assembly for the rest of
    ///     the session. A scaffold that taught the second habit would be teaching it to everybody who
    ///     ever runs <c>dotnet new vixen-plugin</c>.
    /// </summary>
    [Fact]
    public void TheEditorPluginTemplateRegistersThroughTheContext() {
        var source = TextOf(Template("vixen-plugin"), "Kestrel", "KestrelPlugin.cs");

        Assert.Contains("public sealed class KestrelPlugin : IEditorPlugin", source, StringComparison.Ordinal);
        Assert.Contains("public void Activate(PluginContext context)", source, StringComparison.Ordinal);

        // A command, a menu entry and a panel — the three registrations doc 11's extension points
        // start with, and the three a plugin author is most likely to want on day one.
        Assert.Contains("context.AddCommand(", source, StringComparison.Ordinal);
        Assert.Contains("context.AddMenuItem(", source, StringComparison.Ordinal);
        Assert.Contains("context.AddPanel(", source, StringComparison.Ordinal);

        // And nothing that goes round it. `Shell.Commands.Add` is allowed and is occasionally right,
        // but it is not what a first example should show.
        Assert.DoesNotContain("Shell.Commands.Add", source, StringComparison.Ordinal);
    }
}
