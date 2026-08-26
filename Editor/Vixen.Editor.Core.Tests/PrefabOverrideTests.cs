// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core.Scenes;
using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>A component a scene may name, standing in for the engine's own.</summary>
/// <remarks>
///     Declared here rather than referenced, because what is being tested is the format's handling of
///     an alias-tagged entry and not any particular component — and a test that reached for
///     <c>Vixen.Rendering</c>'s <c>Light</c> would make this assembly depend on the renderer to check a
///     property of YAML.
/// </remarks>
[DataContract("TestLamp")]
public sealed class TestLamp {
    /// <summary>How bright it is.</summary>
    public float Intensity { get; set; }

    /// <summary>What colour it is.</summary>
    public Vector3 Tint { get; set; }
}

/// <summary>A second one, for the case where the template has a component the instance does not.</summary>
[DataContract("TestTag")]
public sealed class TestTag {
    /// <summary>What it says.</summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>
///     The prefab link and the override list, as doc 47 decides them: presence is the override, a
///     reconcile writes values and removes nothing, and a round trip loses neither.
/// </summary>
public sealed class PrefabOverrideTests {
    static readonly string Asset = new AssetReference(AssetId.New()).ToString();

    static SceneEntityData Lamp(EntityId source, float intensity, Vector3 position, params string[] overrides) =>
        new() {
            Id = EntityId.New(),
            Name = "Lamp",
            Position = position,
            Prefab = Asset,
            Source = source,
            Overrides = [.. overrides],
            Components = [new TestLamp { Intensity = intensity, Tint = new(1f, 0.5f, 0.25f) }]
        };

    static SceneFile Template(EntityId source, float intensity, Vector3 position) =>
        new() {
            Name = "Lamp",
            Roots = [
                new() {
                    Id = source,
                    Name = "Lamp",
                    Position = position,
                    Components = [new TestLamp { Intensity = intensity, Tint = new(1f, 1f, 1f) }]
                }
            ]
        };

    static SceneFile Scene(params SceneEntityData[] roots) => new() { Name = "Level", Roots = [.. roots] };

    // ── The link ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnEntityWithNoLinkIsNotAnInstance() =>
        Assert.False(PrefabOverrides.IsInstance(new() { Id = EntityId.New() }));

    [Fact]
    public void HalfALinkIsNotAnInstance() {
        Assert.False(PrefabOverrides.IsInstance(new() { Prefab = Asset }));
        Assert.False(PrefabOverrides.IsInstance(new() { Source = EntityId.New() }));
    }

    [Fact]
    public void ABothHalvedLinkIsAnInstance() =>
        Assert.True(PrefabOverrides.IsInstance(Lamp(EntityId.New(), 1f, Vector3.Zero)));

    // ── The round trip ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheLinkAndTheOverridesSurviveASave() {
        var source = EntityId.New();
        var file = Scene(Lamp(source, 3f, new(1f, 2f, 3f), "Position", "TestLamp.Intensity"));

        var read = SceneFile.FromYaml(file.ToYaml());
        var entity = read.Roots[0];

        Assert.Equal(Asset, entity.Prefab);
        Assert.Equal(source, entity.Source);
        Assert.Equal(["Position", "TestLamp.Intensity"], entity.Overrides);

        // ⚠ And the vector is not zero. A format whose scalars were not registered reads a Vector3 back
        // as a default, silently and only when it runs before anything else scene-shaped — which is
        // exactly the code an override list would be.
        Assert.Equal(new Vector3(1f, 2f, 3f), entity.Position);
    }

    [Fact]
    public void SaveLoadSaveIsTheSameBytes() {
        var file = Scene(Lamp(EntityId.New(), 3f, new(1f, 2f, 3f), "TestLamp.Intensity"));

        var once = file.ToYaml();
        var twice = SceneFile.FromYaml(once).ToYaml();

        Assert.Equal(once, twice);
    }

    [Fact]
    public void AFileWithoutTheKeysReadsAsNotAnInstance() {
        const string yaml = """
            version: 1
            name: Level
            roots:
              - id: 1a2b3c4d5e6f708192a3b4c5d6e7f809
                name: Crate
                position: 1 2 3
            """;

        var entity = SceneFile.FromYaml(yaml).Roots[0];

        Assert.False(PrefabOverrides.IsInstance(entity));
        Assert.Empty(entity.Overrides);
        Assert.Equal(new Vector3(1f, 2f, 3f), entity.Position);
    }

    // ── Presence is the override ────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOverrideToZeroSurvivesAReconcile() {
        // The trap doc 47 is written around: a field whose zero means "not overridden" cannot express
        // "overridden to zero", so an author who turns a lamp off gets it turned back on.
        var source = EntityId.New();
        var instance = Lamp(source, 0f, Vector3.Zero, "TestLamp.Intensity");
        var scene = Scene(instance);

        PrefabOverrides.Reconcile(scene, Asset, Template(source, 12f, Vector3.Zero));

        Assert.Equal(0f, ((TestLamp) instance.Components[0]).Intensity);
    }

    [Fact]
    public void AnOverrideEqualToTheTemplateIsStillAnOverride() {
        // Presence and not difference: the instance's position started identical to the template's, so a
        // model that inferred overridden-ness from a comparison would move it.
        var source = EntityId.New();
        var instance = Lamp(source, 1f, new(5f, 0f, 0f), "Position");
        var scene = Scene(instance);

        PrefabOverrides.Reconcile(scene, Asset, Template(source, 1f, new(9f, 0f, 0f)));

        Assert.Equal(new Vector3(5f, 0f, 0f), instance.Position);
    }

    // ── Propagation ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUnclaimedMemberTakesTheTemplateValue() {
        var source = EntityId.New();
        var instance = Lamp(source, 0f, Vector3.Zero);
        var scene = Scene(instance);

        var written = PrefabOverrides.Reconcile(scene, Asset, Template(source, 12f, new(4f, 5f, 6f)));

        Assert.Equal(12f, ((TestLamp) instance.Components[0]).Intensity);
        Assert.Equal(new Vector3(4f, 5f, 6f), instance.Position);
        Assert.True(written > 0);
    }

    [Fact]
    public void ClearingAnOverrideGivesTheMemberBack() {
        var source = EntityId.New();
        var instance = Lamp(source, 0f, Vector3.Zero, "TestLamp.Intensity");

        Assert.True(PrefabOverrides.Clear(instance, "TestLamp.Intensity"));
        PrefabOverrides.Reconcile(Scene(instance), Asset, Template(source, 12f, Vector3.Zero));

        Assert.Equal(12f, ((TestLamp) instance.Components[0]).Intensity);
    }

    [Fact]
    public void EveryInstanceOfOnePrefabIsReconciled() {
        var source = EntityId.New();
        var first = Lamp(source, 0f, Vector3.Zero);
        var second = Lamp(source, 0f, Vector3.Zero);

        PrefabOverrides.Reconcile(Scene(first, second), Asset, Template(source, 7f, Vector3.Zero));

        Assert.Equal(7f, ((TestLamp) first.Components[0]).Intensity);
        Assert.Equal(7f, ((TestLamp) second.Components[0]).Intensity);
    }

    [Fact]
    public void AnEntityFromAnotherPrefabIsLeftAlone() {
        var source = EntityId.New();
        var mine = Lamp(source, 0f, Vector3.Zero);
        mine.Prefab = new AssetReference(AssetId.New()).ToString();

        var written = PrefabOverrides.Reconcile(Scene(mine), Asset, Template(source, 7f, Vector3.Zero));

        Assert.Equal(0, written);
        Assert.Equal(0f, ((TestLamp) mine.Components[0]).Intensity);
    }

    // ── Nothing is ever removed ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AnEntityTheTemplateNoLongerHasIsKeptAndReported() {
        var instance = Lamp(EntityId.New(), 4f, Vector3.Zero);
        var scene = Scene(instance);
        List<PrefabReport> reports = [];

        PrefabOverrides.Reconcile(scene, Asset, Template(EntityId.New(), 9f, Vector3.Zero), reports);

        Assert.Same(instance, scene.Roots[0]);
        Assert.Equal(4f, ((TestLamp) instance.Components[0]).Intensity);
        Assert.Contains(reports, report => report.Kind == PrefabReportKind.OrphanedEntity);
    }

    [Fact]
    public void AnOverrideNamingNothingIsKeptAndReported() {
        var source = EntityId.New();
        var instance = Lamp(source, 4f, Vector3.Zero, "TestLamp.Intensity", "TestGone.Whatever");
        List<PrefabReport> reports = [];

        PrefabOverrides.Reconcile(Scene(instance), Asset, Template(source, 9f, Vector3.Zero), reports);

        Assert.Contains("TestGone.Whatever", instance.Overrides);

        Assert.Contains(
            reports,
            report => report.Kind == PrefabReportKind.OrphanedOverride && report.Detail == "TestGone.Whatever"
        );
    }

    [Fact]
    public void AComponentOnlyTheTemplateHasIsReportedAndNotAdded() {
        var source = EntityId.New();
        var instance = Lamp(source, 0f, Vector3.Zero);
        var template = Template(source, 9f, Vector3.Zero);
        template.Roots[0].Components.Add(new TestTag { Label = "new" });

        List<PrefabReport> reports = [];
        PrefabOverrides.Reconcile(Scene(instance), Asset, template, reports);

        Assert.Single(instance.Components);

        Assert.Contains(
            reports,
            report => report.Kind == PrefabReportKind.MissingComponent && report.Detail == "TestTag"
        );
    }

    [Fact]
    public void AComponentOnlyTheInstanceHasIsLeftAlone() {
        var source = EntityId.New();
        var instance = Lamp(source, 0f, Vector3.Zero);
        instance.Components.Add(new TestTag { Label = "mine" });

        PrefabOverrides.Reconcile(Scene(instance), Asset, Template(source, 9f, Vector3.Zero));

        Assert.Equal("mine", ((TestTag) instance.Components[1]).Label);
    }

    [Fact]
    public void AnEntityTheTemplateGainedIsReportedAndNotAdded() {
        var source = EntityId.New();
        var instance = Lamp(source, 0f, Vector3.Zero);
        var template = Template(source, 9f, Vector3.Zero);
        var added = EntityId.New();
        template.Roots[0].Children.Add(new() { Id = added, Name = "Bulb" });

        List<PrefabReport> reports = [];
        var scene = Scene(instance);
        PrefabOverrides.Reconcile(scene, Asset, template, reports);

        Assert.Empty(scene.Roots[0].Children);

        Assert.Contains(
            reports,
            report => report.Kind == PrefabReportKind.AddedByTemplate && report.Detail == added.ToString()
        );
    }

    /// <summary>
    ///     ⚠⚠ A child the author deleted is not reported as one the template gained — because the
    ///     instance says which children it removed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two are indistinguishable from the file alone: with resolved values, "the author
    ///         deleted this" and "the template gained this since" are both "the instance does not name
    ///         it". Doc 47 § 6 requires the removed list to land <i>before</i> anything adds a
    ///         template's children back, and this is the rule that makes it load-bearing today: without
    ///         it, one deliberate deletion is a warning on every open of that level, for ever.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The removed entity is still not added.</b> Nothing here grafts a subtree; the list
    ///         changes what is <i>said</i> about the absence and not what is done about it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AChildTheAuthorRemovedIsNotReportedAsAddedByTheTemplate() {
        var source = EntityId.New();
        var deleted = EntityId.New();

        var instance = Lamp(source, 0f, Vector3.Zero);
        instance.Removed.Add(deleted);

        var template = Template(source, 9f, Vector3.Zero);
        template.Roots[0].Children.Add(new() { Id = deleted, Name = "Bulb" });

        List<PrefabReport> reports = [];
        var scene = Scene(instance);
        PrefabOverrides.Reconcile(scene, Asset, template, reports);

        Assert.Empty(scene.Roots[0].Children);
        Assert.DoesNotContain(reports, report => report.Kind == PrefabReportKind.AddedByTemplate);
    }

    /// <summary>A child the template gained is still reported when the instance removed a different one.</summary>
    [Fact]
    public void RemovingOneChildDoesNotSilenceAnother() {
        var source = EntityId.New();
        var deleted = EntityId.New();
        var added = EntityId.New();

        var instance = Lamp(source, 0f, Vector3.Zero);
        instance.Removed.Add(deleted);

        var template = Template(source, 9f, Vector3.Zero);
        template.Roots[0].Children.Add(new() { Id = deleted, Name = "Bulb" });
        template.Roots[0].Children.Add(new() { Id = added, Name = "Housing" });

        List<PrefabReport> reports = [];
        PrefabOverrides.Reconcile(Scene(instance), Asset, template, reports);

        Assert.Equal(
            [added.ToString()],
            reports
                .Where(report => report.Kind == PrefabReportKind.AddedByTemplate)
                .Select(report => report.Detail)
        );
    }

    /// <summary>The removed list survives a save, like every other key of the link.</summary>
    [Fact]
    public void TheRemovedListSurvivesASave() {
        var deleted = EntityId.New();
        var instance = Lamp(EntityId.New(), 1f, Vector3.Zero);

        instance.Removed.Add(deleted);

        var read = SceneFile.FromYaml(Scene(instance).ToYaml());

        Assert.Equal([deleted], read.Roots[0].Removed);
    }

    [Fact]
    public void ASceneWithNoInstancesReportsNothing() {
        var plain = new SceneEntityData { Id = EntityId.New(), Name = "Crate" };
        List<PrefabReport> reports = [];

        PrefabOverrides.Reconcile(Scene(plain), Asset, Template(EntityId.New(), 9f, Vector3.Zero), reports);

        Assert.Empty(reports);
    }

    // ── The list itself ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkingIsIdempotentAndSorted() {
        var entity = Lamp(EntityId.New(), 1f, Vector3.Zero);

        Assert.True(PrefabOverrides.Mark(entity, "TestLamp.Intensity"));
        Assert.True(PrefabOverrides.Mark(entity, "Position"));
        Assert.False(PrefabOverrides.Mark(entity, "Position"));

        Assert.Equal(["Position", "TestLamp.Intensity"], entity.Overrides);
    }

    [Fact]
    public void ClearingWhatIsNotMarkedSaysSo() =>
        Assert.False(PrefabOverrides.Clear(Lamp(EntityId.New(), 1f, Vector3.Zero), "Position"));

    [Fact]
    public void APathIsMatchedWithoutRegardToCase() {
        var entity = Lamp(EntityId.New(), 1f, Vector3.Zero, "testlamp.intensity");

        Assert.True(PrefabOverrides.IsOverridden(entity, "TestLamp.Intensity"));
        Assert.True(PrefabOverrides.TryRead(entity, "TESTLAMP.INTENSITY", out var value));
        Assert.Equal(1f, value);
    }

    [Fact]
    public void StructureIsNotAddressable() {
        // `Children` and `Components` are real members of `SceneEntityData` and are deliberately not
        // reachable: an override naming one would be asking a reconcile to graft a subtree.
        var entity = Lamp(EntityId.New(), 1f, Vector3.Zero);

        Assert.False(PrefabOverrides.TryRead(entity, "Children", out _));
        Assert.False(PrefabOverrides.TryRead(entity, "Components", out _));
        Assert.True(PrefabOverrides.TryRead(entity, "Position", out _));
    }

    [Fact]
    public void AnUnknownPathNamesNothing() {
        var entity = Lamp(EntityId.New(), 1f, Vector3.Zero);

        Assert.False(PrefabOverrides.TryRead(entity, "Nonsense", out _));
        Assert.False(PrefabOverrides.TryRead(entity, "TestLamp.Nonsense", out _));
        Assert.False(PrefabOverrides.TryWrite(entity, "NoSuchComponent.Member", 1f));
    }

    [Fact]
    public void AVectorMemberIsReadAndWrittenByPath() {
        var entity = Lamp(EntityId.New(), 1f, Vector3.Zero);

        Assert.True(PrefabOverrides.TryWrite(entity, "TestLamp.Tint", new Vector3(0.1f, 0.2f, 0.3f)));
        Assert.True(PrefabOverrides.TryRead(entity, "TestLamp.Tint", out var value));
        Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), value);
    }
}
