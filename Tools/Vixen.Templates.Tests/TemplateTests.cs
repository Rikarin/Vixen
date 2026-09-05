// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Vixen.App;
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
    ///     All six of the ones doc 17 § Project templates names, which is now every one of them.
    /// </summary>
    /// <remarks>
    ///     Two of these were written down as owed rather than blocked and then landed: `vixen-plugin`
    ///     when `Vixen.Editor.Plugin` did, and `vixen-tool` — doc 17 § Q5d's headless batch head —
    ///     which was waiting on nothing at all, because `Vixen.Platform.Headless` has existed since
    ///     Phase 1. This list is what has to be edited when a seventh arrives, and the assertion is
    ///     on the whole list rather than on membership so that adding one is a deliberate act.
    /// </remarks>
    [Fact]
    public void TheTemplatesAreTheOnesThatCanBeWrittenToday() {
        string[] expected = ["vixen-app", "vixen-game", "vixen-lib", "vixen-mmo", "vixen-plugin", "vixen-tool"];

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
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This asserted the rule through a template's own files until 2026-08-23, and now
    ///         it cannot: no template ships a binary file any more.</b> <c>vixen-app</c> carried
    ///         eight <c>.spv</c> modules because it carried its own frame loop; it takes
    ///         <c>Vixen.Ui.Desktop</c> now, which embeds them, so the whole <c>Shaders/</c> folder
    ///         went with the four hundred lines of C# around it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the rule is asserted directly rather than through an example, and saying that
    ///         out loud is the point.</b> The alternative was to keep the loop over <c>.spv</c> files
    ///         and let it iterate zero times — a test that passes, reports nothing, and reads in a
    ///         diff exactly like one that is checking something. The <c>Assert.Contains</c> at the
    ///         end of the old version existed to stop precisely that, and it is what failed and
    ///         brought this comment about.
    ///     </para>
    ///     <para>
    ///         A template will ship a binary again — an icon, a font, a compiled asset — and the day
    ///         it does, the pass-through half is worth restoring on top of this.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ABinaryFileIsCopiedRatherThanSubstitutedInto() {
        // ⚠ A NUL in the middle, with the source name on both sides of it. `IsTextFile` is the one
        // decision the substitution turns on — see `TemplateCatalog.Instantiate`, which calls
        // `Substitute` only for a file this answers true for — so testing it is testing the rule.
        // The substitution itself is `internal` to Vixen.Editor.Core and is not reachable from here.
        Assert.False(TemplateCatalog.IsTextFile("VixenApp1\0VixenApp1"u8));
        Assert.True(TemplateCatalog.IsTextFile("VixenApp1 is the name"u8));
    }

    /// <summary>No template ships a binary file, which is why the theory above cannot use one.</summary>
    /// <remarks>
    ///     ⚠ Asserted rather than assumed, so that adding one is a deliberate act with a failing test
    ///     in front of it — and so that <see cref="ABinaryFileIsCopiedRatherThanSubstitutedInto" />'s
    ///     remarks cannot quietly become false.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Templates))]
    public void NoTemplateShipsABinaryFile(string id) {
        foreach (var file in Template(id).Instantiate("Kestrel", "1.2.3")) {
            Assert.True(
                TemplateCatalog.IsTextFile(file.Content),
                $"{id}/{file.Path} is binary. Restore the pass-through half of "
                + $"{nameof(ABinaryFileIsCopiedRatherThanSubstitutedInto)} over it, and delete this test."
            );
        }
    }

    /// <summary>
    ///     A property only <c>Vixen.Sdk</c>'s targets read may only be set by a project that is on
    ///     <c>Vixen.Sdk</c>. Anywhere else it is inert, and inert in the one way nothing reports.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is a real defect made mechanical, not a hypothetical.</b> The mmo
    ///         template's <c>Shared</c> project set <c>VixenAddressConstants</c>,
    ///         <c>VixenAddressNamespace</c>, <c>VixenAddressIds</c> and
    ///         <c>VixenProjectDirectory</c> on a <c>Microsoft.NET.Sdk</c> project — so
    ///         <c>VixenImport</c> never ran, <c>Addresses.g.cs</c> was never written, and the
    ///         comment above them stated the generated file as a fact. MSBuild says nothing about a
    ///         property nobody reads; the whole failure is silence.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No exemption list, deliberately.</b> An architecture rule whose one entry is the
    ///         defect it was written for is satisfied by that defect, which is how this repository
    ///         has built rules that could not fail. The properties came out of the template instead,
    ///         and what it costs to put them back is written where they used to be.
    ///     </para>
    /// </remarks>
    [Fact]
    public void NoTemplateSetsAnSdkPropertyOnAProjectThatIsNotOnTheSdk() {
        var projects = TemplateCatalog.All
            .SelectMany(template => template.Instantiate("Kestrel", ScaffoldRunner.SdkVersion)
                .Where(file => file.Path.EndsWith(".csproj", StringComparison.Ordinal))
                .Select(file => (template.Id, file.Path, Text: Encoding.UTF8.GetString(file.Content)))
            )
            .ToList();

        // The instrument, before the sweep. A reader that found no projects, or that could not tell
        // the one SDK-driven project from the other eight, would walk an empty list in green.
        Assert.Equal(9, projects.Count);
        Assert.Single(projects, project => DrivenByTheSdk(project.Text));

        // And it has to be able to say no, over the shape this test exists to keep out.
        Assert.Equal(
            ["VixenAddressConstants"],
            SdkProperties(
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                + "<VixenAddressConstants>true</VixenAddressConstants></PropertyGroup></Project>"
            )
        );

        foreach (var (id, path, text) in projects.Where(project => !DrivenByTheSdk(project.Text))) {
            Assert.True(
                SdkProperties(text).Count == 0,
                $"{id}/{path} is not on Vixen.Sdk and sets {string.Join(", ", SdkProperties(text))}, "
                + "which nothing reads. Put the project on Vixen.Sdk or take the property out — "
                + "leaving it is an opt-in a reader cannot tell from a working one."
            );
        }
    }

    /// <summary>Whether a project file names <c>Vixen.Sdk</c> as its SDK.</summary>
    /// <param name="project">The project file's text.</param>
    /// <returns>Whether the SDK's targets are imported at all.</returns>
    /// <remarks>
    ///     ⚠ The root element's attribute, read as XML rather than looked for as a substring —
    ///     which is how the first draft of this test called three of the nine projects SDK-driven.
    ///     Two of them only <i>mention</i> <c>Sdk="Vixen.Sdk/…"</c> in a comment saying why they are
    ///     not on it, and a comment is exactly where that string is most likely to appear.
    /// </remarks>
    static bool DrivenByTheSdk(string project) =>
        XDocument.Parse(project).Root?.Attribute("Sdk")?.Value
            .StartsWith("Vixen.Sdk/", StringComparison.Ordinal)
        ?? false;

    /// <summary>The <c>Vixen*</c> property elements a project file sets.</summary>
    /// <param name="project">The project file's text.</param>
    /// <returns>Their names, in document order.</returns>
    /// <remarks>
    ///     Every property <c>Vixen.Sdk.targets</c> reads is named <c>Vixen…</c>, and an element is
    ///     not a comment — so the recipe written where the mmo template's properties used to be
    ///     names them without setting them, which is the distinction this has to make.
    /// </remarks>
    static IReadOnlyList<string> SdkProperties(string project) =>
        [
            .. XDocument.Parse(project)
                .Descendants()
                .Select(element => element.Name.LocalName)
                .Where(name => name.StartsWith("Vixen", StringComparison.Ordinal))
        ];

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
        // ⚠ One reference where there used to be five, and the substance of the change rather than
        // an incidental tidy: `Vixen.Ui.Desktop` is the window, the device and the frame loop, and
        // it brings the control set — and therefore the whole markup toolchain — behind it.
        Assert.Contains("Include=\"Vixen.Ui.Desktop\"", project, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The template teaches the path the engine is built around: markup, a stylesheet and
    ///     utility classes.</b> It shipped three hand-written C# files and no <c>.vxml</c> at all, so
    ///     every project started from it started on the path a week of work argued against.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The absence of plumbing is half the assertion, and the more important half.</b>
    ///         A <c>PackageReference</c> to <c>Vixen.Ui.Controls</c> brings the VXML compiler, the
    ///         two item types and the utility build step, because <c>Vixen.Ui</c> ships its MSBuild
    ///         logic in <c>buildTransitive/</c>. If that ever regresses to <c>build/</c> the markup
    ///         is silently not compiled — no item, no generator input, no error — so a project file
    ///         that had grown a glob or an <c>Import</c> to compensate is the visible symptom, and
    ///         this is what fails first.
    ///     </para>
    ///     <para>
    ///         That the markup <em>compiles</em> is <see cref="WhatEachTemplateWritesCompiles" />:
    ///         <c>AppDocument.cs</c> mounts the component the <c>.vxml</c> produces, and
    ///         <see cref="TemplateCompiler" /> runs the generator over it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheApplicationTemplateIsWrittenInMarkup() {
        var app = Template("vixen-app");
        var files = app.Instantiate("Painter", "1.2.3").Select(file => file.Path).ToList();

        Assert.Contains("AppShell.vxml", files);
        Assert.Contains("Theme/vixen.ui.vcss", files);

        var shell = TextOf(app, "Painter", "AppShell.vxml");

        // The tree, the utility vocabulary and a signal read: the three things the declarative path
        // is, one assertion each.
        Assert.Contains("@component AppShell", shell, StringComparison.Ordinal);
        Assert.Contains("class=\"flex flex-col", shell, StringComparison.Ordinal);
        Assert.Contains("@clicks.Value", shell, StringComparison.Ordinal);

        // And nothing in the project file to make any of it work.
        var project = TextOf(app, "Painter", "Painter.csproj");

        Assert.DoesNotContain("*.vxml", project, StringComparison.Ordinal);
        Assert.DoesNotContain("*.vcss", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<AdditionalFiles", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<Import", project, StringComparison.Ordinal);
        Assert.DoesNotContain("OutputItemType", project, StringComparison.Ordinal);
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

    // ── vixen-tool ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A batch head is a console application on the engine's host, and it has no assets of its
    ///     own — it operates on somebody else's content, so the SDK's import and content-build steps
    ///     would be two no-ops and a tool dependency for nothing.
    /// </summary>
    /// <remarks>
    ///     One package reference, and it is the host. <c>Vixen.App</c> is what chooses a platform,
    ///     and what it chooses under <c>AppConfig.Headless</c> is <c>Vixen.Platform.Headless</c> —
    ///     which is the whole of doc 17 § Q5d's "nearly free once <c>Vixen.Platform.Headless</c>
    ///     exists".
    /// </remarks>
    [Fact]
    public void TheToolTemplateIsAConsoleHeadWithoutTheSdk() {
        var project = TextOf(Template("vixen-tool"), "Kestrel", "Kestrel.csproj");

        Assert.Contains("Sdk=\"Microsoft.NET.Sdk\"", project, StringComparison.Ordinal);
        Assert.Contains("<OutputType>Exe</OutputType>", project, StringComparison.Ordinal);
        // The SDK attribute rather than the word: the project file explains in a comment why it is
        // not this SDK, and a template that says why it does something is worth more than one that
        // is merely searchable.
        Assert.DoesNotContain("Sdk=\"Vixen.Sdk", project, StringComparison.Ordinal);
        Assert.Contains("Include=\"Vixen.App\"", project, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>What the head is, asserted by running it rather than by reading it.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The scaffolded project is compiled and loaded, its <c>Game</c> is constructed, and the
    ///         host's own call sequence is performed on it: <c>AppConfig.Apply(arguments)</c> and
    ///         then <c>OnConfigure</c>, in that order, because that is the order
    ///         <c>AppBuilder.Build</c> uses. What comes back is the configuration a real run would
    ///         have, which is the only form in which the claims below can be checked at all — "no
    ///         window", "no world", "ends by itself" are behaviours and not strings.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>MaxFrames</c> is the one that would have been wrong.</b> A batch head has to
    ///         end, and <c>ExitWhenAllWindowsClose</c> cannot end it — that check is skipped when
    ///         there is no window — so the template names a frame budget. Assigning it outright
    ///         reads perfectly and silently discards a <c>--vixen-frames</c> the operator typed,
    ///         because <c>Apply</c> has already run by then. The three cases below are the whole
    ///         difference between a default and an assignment.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheToolTemplateIsAHeadlessHeadThatEndsAndStillObeysItsCommandLine() {
        var plain = Configured();

        Assert.True(plain.Headless);
        Assert.Null(plain.Window);
        Assert.False(plain.UseEngine);
        Assert.Equal(1, plain.MaxFrames);

        // No device unless somebody asked for a picture, so a validation run on a build agent with
        // no Vulkan is unaffected — and a CI screenshot job gets the device it came for.
        Assert.False(plain.Graphics.Enabled);
        Assert.True(Configured("--vixen-capture", "shot.png").Graphics.Enabled);

        // And the frame budget is a default rather than an assignment.
        Assert.Equal(120, Configured("--vixen-frames", "120").MaxFrames);
    }

    /// <summary>The configuration the scaffolded tool leaves behind for a given command line.</summary>
    /// <param name="arguments">What the operator typed.</param>
    /// <returns>The config, after the host's own two calls.</returns>
    static AppConfig Configured(params string[] arguments) {
        var assembly = TemplateCompiler.Load(Template("vixen-tool"), "Kestrel");
        var tool = Activator.CreateInstance(assembly.GetType("Kestrel.KestrelTool")!)!;

        var config = new AppConfig();
        config.Apply(AppArguments.Parse(arguments));

        // ⚠ Reflected for, because `OnConfigure` is `protected internal` — the host calls it and
        // nothing else may. Invoking the base declaration on a derived instance dispatches
        // virtually, so what runs is the template's override.
        typeof(Game)
            .GetMethod("OnConfigure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(tool, [config]);

        return config;
    }
}
