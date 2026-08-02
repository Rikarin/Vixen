// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Reflection;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Animation;
using Xunit;

namespace Tests;

/// <summary>The two files an author's markup is declared against, and the markup itself.</summary>
public sealed class ConstraintAuthoringImporterTests {
    [Fact]
    public void TheImportersClaimTheirOwnExtensions() {
        Assert.Equal([".vxpriorities"], new PriorityLadderImporter().Extensions);
        Assert.Equal([".vxconstraints"], new ConstraintTemplateImporter().Extensions);
    }

    [Fact]
    public void TheArtifactTypesAreTheContractsOfWhatIsWritten() {
        Assert.True(TypeRegistry.TryGetByAlias(PriorityLadderImporter.LadderType, out var ladder));
        Assert.Equal(typeof(PriorityLadderContent), ladder.Type);

        Assert.True(TypeRegistry.TryGetByAlias(ConstraintTemplateImporter.TemplateType, out var template));
        Assert.Equal(typeof(ConstraintTemplateContent), template.Type);
    }

    // ---------------------------------------------------------------- the ladder

    [Fact]
    public async Task ALadderCompilesAndItsNamesResolve() {
        const string Yaml = """
            name: swimming
            step: 100
            rungs:
              - name: flourish
                value: 0
                meaning: A secondary motion.
              - name: contact
                value: 400
                meaning: A hand on the ladder rung.
            """;

        var result = await ImportLadder(Yaml);

        Assert.DoesNotContain(result.Diagnostics, entry => entry.Severity == ImportSeverity.Error);

        var content = Serializer.Read<PriorityLadderContent>(Assert.Single(result.Artifacts).Content.ToArray());
        var ladder = content.Bake();

        Assert.Equal(400, ladder.Value("contact"));
        Assert.Equal(402, ladder.Value("contact+2"));
    }

    /// <summary>⚠ Two rungs at one value have no order between them, which is all a ladder is.</summary>
    [Fact]
    public async Task TwoRungsAtOneValueAreRefused() {
        const string Yaml = """
            name: broken
            rungs:
              - name: aim
                value: 200
              - name: look
                value: 200
            """;

        var result = await ImportLadder(Yaml);

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error && entry.Message.Contains("no order", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     ⚠ Rungs closer together than a sub-step can reach make <c>look+50</c> outrank <c>aim</c>
    ///     without saying so.
    /// </summary>
    [Fact]
    public async Task RungsCloserThanASubStepCanReachAreWarnedAbout() {
        const string Yaml = """
            name: tight
            step: 10
            rungs:
              - name: look
                value: 100
              - name: aim
                value: 110
            """;

        var result = await ImportLadder(Yaml);

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Warning
                && entry.Message.Contains("without saying so", StringComparison.Ordinal)
        );
    }

    // ---------------------------------------------------------------- templates

    [Fact]
    public async Task ATemplateCompilesAndItsTimingsRemap() {
        const string Yaml = """
            name: seated
            revision: 2
            meaning: A character sitting.
            tags:
              - name: right hand
                kind: Position
                effector: hand_r
                begin: 0.0
                end: 0.5
              - name: hips
                kind: Position
                effector: pelvis
                begin: 0.0
                end: 1.0
            """;

        var result = await ImportTemplate(Yaml);

        Assert.DoesNotContain(result.Diagnostics, entry => entry.Severity == ImportSeverity.Error);

        var template = Serializer.Read<ConstraintTemplateContent>(Assert.Single(result.Artifacts).Content.ToArray());
        var placed = template.Instantiate(0.5f, 1f);

        Assert.Equal(2, placed.Count);
        Assert.Equal(0.75f, placed[0].End, 3);
        Assert.Equal("seated", placed[0].Template);
        Assert.Equal(2, placed[0].TemplateVersion);
    }

    /// <summary>⚠ A template captured from a clip and never re-based fits no other clip.</summary>
    [Fact]
    public async Task ATemplateWhoseTimingsAreNotFractionsIsRefused() {
        const string Yaml = """
            name: captured
            tags:
              - name: right hand
                effector: hand_r
                begin: 2.4
                end: 6.1
            """;

        var result = await ImportTemplate(Yaml);

        Assert.Empty(result.Artifacts);
        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error && entry.Message.Contains("re-based", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task ANamelessTemplateIsRefusedBecauseAReapplyCouldNotFindItsTags() {
        var result = await ImportTemplate("revision: 1\ntags: []\n");

        Assert.Empty(result.Artifacts);
        Assert.Contains(
            result.Diagnostics,
            entry => entry.Message.Contains("never maintained", StringComparison.Ordinal)
        );
    }

    // ---------------------------------------------------------------- the clip's own track

    [Fact]
    public async Task AClipsConstraintTrackSurvivesTheImportAndIsChecked() {
        const string Yaml = """
            name: Reach
            duration: 1.0
            constraints:
              - name: right hand
                kind: Position
                effector: hand_r
                chain: upperarm_r
                begin: 0.2
                end: 0.8
                priority: contact
                goal:
                  kind: Surface
                  shape: rail
                  u: 0.25
                  v: 0.6
            """;

        var result = await ImportClip(Yaml);

        Assert.DoesNotContain(result.Diagnostics, entry => entry.Severity == ImportSeverity.Error);

        var content = Serializer.Read<AnimationClipContent>(Assert.Single(result.Artifacts).Content.ToArray());
        var tag = Assert.Single(content.Constraints);

        Assert.Equal("right hand", tag.Name);
        Assert.Equal("upperarm_r", tag.Chain);
        Assert.Equal(ConstraintFrameKind.Surface, tag.Goal.Kind);
        Assert.Equal(0.6f, tag.Goal.V, 3);
    }

    [Fact]
    public async Task AConstraintWithNoEffectorOrASpanOffTheClipIsRefused() {
        var noEffector = await ImportClip("name: A\nduration: 1.0\nconstraints:\n  - name: nowhere\n");

        Assert.Contains(
            noEffector.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error && entry.Message.Contains("about a joint", StringComparison.Ordinal)
        );

        var offTheEnd = await ImportClip(
            "name: A\nduration: 1.0\nconstraints:\n  - name: late\n    effector: hand_r\n    begin: 1.4\n    end: 2.0\n"
        );

        Assert.Contains(
            offTheEnd.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error && entry.Message.Contains("never reached", StringComparison.Ordinal)
        );
    }

    /// <summary>A span that ends before it begins straddles the loop, and that is legitimate.</summary>
    [Fact]
    public async Task ASpanThatStraddlesTheLoopIsAllowedAndOverlongEasingIsWarnedAbout() {
        var wrapping = await ImportClip(
            "name: A\nduration: 1.0\nconstraints:\n  - name: plant\n    effector: foot_l\n    begin: 0.8\n    end: 0.2\n"
        );

        Assert.DoesNotContain(wrapping.Diagnostics, entry => entry.Severity == ImportSeverity.Error);

        var overEased = await ImportClip(
            "name: A\nduration: 1.0\nconstraints:\n  - name: plant\n    effector: foot_l\n    begin: 0.4\n"
            + "    end: 0.5\n    easeIn: 0.2\n    easeOut: 0.2\n"
        );

        Assert.Contains(
            overEased.Diagnostics,
            entry => entry.Severity == ImportSeverity.Warning
                && entry.Message.Contains("never reaches its full weight", StringComparison.Ordinal)
        );
    }

    static Task<ImportResult> ImportLadder(string text) =>
        Import(new PriorityLadderImporter(), "/Assets/ladder.vxpriorities", text);

    static Task<ImportResult> ImportTemplate(string text) =>
        Import(new ConstraintTemplateImporter(), "/Assets/seated.vxconstraints", text);

    static Task<ImportResult> ImportClip(string text) =>
        Import(new AnimationClipImporter(), "/Assets/reach.vxanim", text);

    static async Task<ImportResult> Import(IAssetImporter importer, string at, string text) {
        var path = new VirtualPath(at);
        var files = new MemoryFileProvider();

        files.Seed(path, text);

        var context = new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows");

        return await importer.ImportAsync(context, TestContext.Current.CancellationToken);
    }
}
