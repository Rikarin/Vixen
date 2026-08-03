// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Reflection;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Animation;
using Xunit;

namespace Tests;

/// <summary>A movement vocabulary as a file: a table, and the four things a table invites.</summary>
public sealed class MoveSetImporterTests {
    [Fact]
    public void TheImporterClaimsItsExtensionAndWritesItsContract() {
        Assert.Equal([".vxmoveset"], new MoveSetImporter().Extensions);

        Assert.True(TypeRegistry.TryGetByAlias(MoveSetImporter.SetType, out var set));
        Assert.Equal(typeof(MoveSetContent), set.Type);
    }

    [Fact]
    public async Task ASetCompilesAndItsRowsBake() {
        const string Yaml = """
            name: locomotion
            entries:
              - name: walk
                clip: Assets/Anim/Walk.vxanim
                speed: 1.4
                minRate: 0.85
                maxRate: 1.15
                footPhase: 0.12
                facets:
                  - key: role
                    value: loop
                  - key: style
                    value: neutral
              - name: idle
                clip: Assets/Anim/Idle.vxanim
                facets:
                  - key: role
                    value: idle
            rules:
              - from:
                  - key: role
                    value: idle
                to:
                  - key: role
                    value: loop
                duration: 0.18
                sync: ClosestFoot
            """;

        var result = await Import(Yaml);

        Assert.DoesNotContain(result.Diagnostics, entry => entry.Severity == ImportSeverity.Error);

        var content = Serializer.Read<MoveSetContent>(Assert.Single(result.Artifacts).Content.ToArray());
        var moves = content.Preview();

        Assert.Equal(2, moves.Count);
        Assert.Equal(1.4f, moves[0].Traits.Speed, 3);
        Assert.True(moves[0].Facets.Contains(Facet.Of("role", "loop")) || moves[1].Facets.Contains(Facet.Of("role", "loop")));

        var policy = content.Policy();

        Assert.Single(policy.Rules.ToArray());
        Assert.Equal(SyncMode.ClosestFoot, policy.Rules[0].Spec.Sync);
    }

    /// <summary>
    ///     ⚠ <b>A duplicate name silently replaces the first row</b>, in a file where the first is
    ///     still sitting there being read by whoever maintains it.
    /// </summary>
    [Fact]
    public async Task TwoRowsWithOneNameAreRefused() {
        var result = await Import(
            "name: x\nentries:\n  - name: walk\n    clip: a.vxanim\n  - name: walk\n    clip: b.vxanim\n"
        );

        Assert.Empty(result.Artifacts);

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error
                && entry.Message.Contains("silently replaces", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     ⚠ <b>The reserved <c>role</c> vocabulary is the one thing a set may not spell its own
    ///     way.</b> A role nothing recognises reads as a transition rule that never matches.
    /// </summary>
    [Fact]
    public async Task AnInventedRoleIsRefused() {
        var result = await Import(
            "name: x\nentries:\n  - name: walk\n    clip: a.vxanim\n    facets:\n      - key: role\n        value: looping\n"
        );

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error
                && entry.Message.Contains("role nothing recognises", StringComparison.Ordinal)
        );

        // Every other key is the project's own business and passes without comment.
        var free = await Import(
            "name: x\nentries:\n  - name: walk\n    clip: a.vxanim\n    facets:\n      - key: style\n        value: whatever\n"
        );

        Assert.DoesNotContain(free.Diagnostics, entry => entry.Severity == ImportSeverity.Error);
    }

    [Fact]
    public async Task AnInvertedRateRangeIsRefusedAndAStillMoveThatRetimesIsWarnedAbout() {
        var inverted = await Import(
            "name: x\nentries:\n  - name: walk\n    clip: a.vxanim\n    speed: 1.4\n    minRate: 1.2\n    maxRate: 0.8\n"
        );

        Assert.Contains(
            inverted.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error && entry.Message.Contains("no range at all", StringComparison.Ordinal)
        );

        var still = await Import(
            "name: x\nentries:\n  - name: gesture\n    clip: a.vxanim\n    speed: 0\n    minRate: 0.8\n    maxRate: 1.4\n"
        );

        Assert.Contains(
            still.Diagnostics,
            entry => entry.Severity == ImportSeverity.Warning
                && entry.Message.Contains("goes nowhere", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task ARowWithNoClipIsRefused() {
        var result = await Import("name: x\nentries:\n  - name: walk\n");

        Assert.Empty(result.Artifacts);
        Assert.Contains(result.Diagnostics, entry => entry.Message.Contains("play silence", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ <b>The two bake entry points differ in exactly one thing.</b> A game drops a row whose
    ///     clip is missing, because an entry that plays silence reads as a character freezing; a tool
    ///     keeps it, because the row is what is being edited.
    /// </summary>
    [Fact]
    public void AGameDropsAnUnresolvableRowAndAToolKeepsIt() {
        var content = new MoveSetContent {
            Name = "x",
            Entries = [
                new() { Name = "walk", Clip = "walk.vxanim" },
                new() { Name = "run", Clip = "missing.vxanim" }
            ]
        };

        List<string> unresolved = [];

        var played = content.Bake(
            address => address == "walk.vxanim" ? UnresolvedMotion.Shared : null,
            unresolved: unresolved
        );

        Assert.Equal(1, played.Count);
        Assert.Equal("run", Assert.Single(unresolved));

        Assert.Equal(2, content.Preview().Count);
    }

    static async Task<ImportResult> Import(string text) {
        var path = new VirtualPath("/Assets/locomotion.vxmoveset");
        var files = new MemoryFileProvider();
        var importer = new MoveSetImporter();

        files.Seed(path, text);

        var context = new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows");

        return await importer.ImportAsync(context, TestContext.Current.CancellationToken);
    }
}
