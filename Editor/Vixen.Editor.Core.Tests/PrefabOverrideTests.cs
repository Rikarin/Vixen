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

    /// <summary>⚠ A child the template gained reaches the instance — doc 47 row 4.</summary>
    /// <remarks>
    ///     <para>
    ///         The grafted entity is a fully-formed instance node and not a transplanted template one:
    ///         a fresh <see cref="SceneEntityData.Id" /> because a scene mints its own identities, the
    ///         template's id as its <see cref="SceneEntityData.Source" />, the prefab's reference, and
    ///         an empty override list because an instance that has never been edited claims nothing.
    ///     </para>
    ///     <para>
    ///         Still reported. Propagation over values is silent because a value is what the level asked
    ///         for; entities appearing is a diff of lines nobody typed.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AnEntityTheTemplateGainedIsAddedAndReported() {
        var source = EntityId.New();
        var instance = Lamp(source, 0f, Vector3.Zero);
        var template = Template(source, 9f, Vector3.Zero);
        var added = EntityId.New();
        template.Roots[0].Children.Add(new() { Id = added, Name = "Bulb" });

        List<PrefabReport> reports = [];
        var scene = Scene(instance);
        PrefabOverrides.Reconcile(scene, Asset, template, reports);

        var child = Assert.Single(scene.Roots[0].Children);

        Assert.Equal("Bulb", child.Name);
        Assert.Equal(added, child.Source);
        Assert.Equal(Asset, child.Prefab);
        Assert.NotEqual(added, child.Id);
        Assert.Empty(child.Overrides);

        Assert.Contains(
            reports,
            report => report.Kind == PrefabReportKind.AddedByTemplate && report.Detail == added.ToString()
        );
    }

    /// <summary>⚠ Reconciling twice adds once — the second pass finds it already there.</summary>
    /// <remarks>
    ///     The check is by the template's id and not by the scene's, which is what makes this true: the
    ///     grafted entity carries a fresh identity of its own and answers to the template's through
    ///     <see cref="SceneEntityData.Source" />. A rule that matched on the scene's id would add the
    ///     child again on every open, for ever.
    /// </remarks>
    [Fact]
    public void AddingBackIsIdempotent() {
        var source = EntityId.New();
        var instance = Lamp(source, 0f, Vector3.Zero);
        var template = Template(source, 9f, Vector3.Zero);
        template.Roots[0].Children.Add(new() { Id = EntityId.New(), Name = "Bulb" });

        var scene = Scene(instance);
        PrefabOverrides.Reconcile(scene, Asset, template);
        PrefabOverrides.Reconcile(scene, Asset, template);

        Assert.Single(scene.Roots[0].Children);
    }

    /// <summary>A grafted child brings its own subtree, each node linked in its own right.</summary>
    /// <remarks>
    ///     ⚠ The positions are asserted, not decoration. The copy goes through the format, so a
    ///     <c>Vector3</c> in it reads back as <c>(0, 0, 0)</c> unless <c>SceneScalars.Register</c> has
    ///     run — the trap that makes a hand-written mapping look like it worked. It runs from
    ///     <see cref="SceneFile" />'s static constructor, so anything holding a scene has already paid
    ///     for it; a graft that silently zeroed every transform would otherwise pass every other
    ///     assertion here.
    /// </remarks>
    [Fact]
    public void AnAddedChildBringsItsSubtree() {
        var source = EntityId.New();
        var instance = Lamp(source, 0f, Vector3.Zero);
        var template = Template(source, 9f, Vector3.Zero);
        var housing = EntityId.New();
        var bolt = EntityId.New();

        template.Roots[0]
            .Children.Add(
                new() {
                    Id = housing,
                    Name = "Housing",
                    Position = new(1f, 2f, 3f),
                    Components = [new TestLamp { Intensity = 5f, Tint = new(0.25f, 0.5f, 0.75f) }],
                    Children = [new() { Id = bolt, Name = "Bolt", Position = new(4f, 5f, 6f) }]
                }
            );

        var scene = Scene(instance);
        PrefabOverrides.Reconcile(scene, Asset, template);

        var added = Assert.Single(scene.Roots[0].Children);
        var deeper = Assert.Single(added.Children);

        Assert.Equal(housing, added.Source);
        Assert.Equal(bolt, deeper.Source);
        Assert.Equal(Asset, deeper.Prefab);
        Assert.NotEqual(deeper.Id, bolt);

        // ⚠ The values came across, at both depths and inside a component.
        Assert.Equal(new Vector3(1f, 2f, 3f), added.Position);
        Assert.Equal(new Vector3(4f, 5f, 6f), deeper.Position);
        Assert.Equal(5f, ((TestLamp) added.Components[0]).Intensity);
        Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), ((TestLamp) added.Components[0]).Tint);
    }

    /// <summary>⚠ The graft is a copy, so editing the level never reaches back into the prefab.</summary>
    /// <remarks>
    ///     The failure this pins is aliasing, and it is invisible until the second thing happens: a
    ///     member-wise copy shares the component objects, so nudging the level's new lamp would edit the
    ///     <i>template's</i> lamp — which the next reconcile would then propagate to every other
    ///     instance in the project as though the prefab's author had made the change.
    /// </remarks>
    [Fact]
    public void AnAddedChildIsACopyAndNotTheTemplatesOwnEntity() {
        var source = EntityId.New();
        var instance = Lamp(source, 0f, Vector3.Zero);
        var template = Template(source, 9f, Vector3.Zero);

        var lamp = new TestLamp { Intensity = 3f, Tint = new(1f, 1f, 1f) };
        template.Roots[0].Children.Add(new() { Id = EntityId.New(), Name = "Bulb", Components = [lamp] });

        var scene = Scene(instance);
        PrefabOverrides.Reconcile(scene, Asset, template);

        var added = Assert.Single(scene.Roots[0].Children);

        Assert.NotSame(template.Roots[0].Children[0], added);

        ((TestLamp) added.Components[0]).Intensity = 99f;

        Assert.Equal(3f, lamp.Intensity);
    }

    /// <summary>⚠ Add-back is per instance, because a removal is.</summary>
    /// <remarks>
    ///     Two instances of one prefab, one of which deleted the child. The one that deleted it keeps it
    ///     deleted; the one that did not gains it. The old scene-wide rule silenced both, which was the
    ///     honest reading of a report — it is not an honest reading of a graft.
    /// </remarks>
    [Fact]
    public void OneInstancesRemovalDoesNotStopAnothersAddBack() {
        var source = EntityId.New();
        var gone = EntityId.New();

        var deleted = Lamp(source, 0f, Vector3.Zero);
        deleted.Removed.Add(gone);

        var kept = Lamp(source, 0f, Vector3.Zero);

        var template = Template(source, 9f, Vector3.Zero);
        template.Roots[0].Children.Add(new() { Id = gone, Name = "Bulb" });

        PrefabOverrides.Reconcile(Scene(deleted, kept), Asset, template);

        Assert.Empty(deleted.Children);
        Assert.Equal("Bulb", Assert.Single(kept.Children).Name);
    }

    /// <summary>⚠⚠ A designer's deletion outlives a template change that re-adds the child.</summary>
    /// <remarks>
    ///     <para>
    ///         The interaction doc 47 § 6 says must be proved before add-back is allowed to exist: a
    ///         template gaining a child and an author having deleted one are the same shape in a file
    ///         that carries resolved values, and the only thing that tells them apart is
    ///         <see cref="SceneEntityData.Removed" />. Both cases are present here at once so that a
    ///         rule which simply refused to add anything would not pass.
    ///     </para>
    ///     <para>
    ///         ⚠ And it has to hold on <i>every</i> open rather than once: the deletion is in the file,
    ///         so a second pass over the same scene is the same question asked again.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ADeletedChildStaysDeletedWhileANewOneArrives() {
        var source = EntityId.New();
        var deleted = EntityId.New();
        var gained = EntityId.New();

        var instance = Lamp(source, 0f, Vector3.Zero);
        instance.Removed.Add(deleted);

        var template = Template(source, 9f, Vector3.Zero);
        template.Roots[0].Children.Add(new() { Id = deleted, Name = "Bulb" });
        template.Roots[0].Children.Add(new() { Id = gained, Name = "Housing" });

        var scene = Scene(instance);

        PrefabOverrides.Reconcile(scene, Asset, template);
        PrefabOverrides.Reconcile(scene, Asset, template);

        Assert.Equal(["Housing"], scene.Roots[0].Children.Select(child => child.Name));
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
    ///         ⚠ <b>And now the list decides what is <i>done</i> about the absence rather than only what
    ///         is said about it.</b> Add-back landed, so the same two lines that used to suppress a
    ///         warning are what stop the level regrowing the entity — which is why doc 47 § 6 insisted
    ///         on the order.
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

    // ── Nesting ─────────────────────────────────────────────────────────────────────────────────

    static readonly string Inner = new AssetReference(AssetId.New()).ToString();

    /// <summary>The inner prefab: one lamp, dim.</summary>
    static SceneFile Bulb(EntityId root) =>
        new() {
            Name = "Bulb",
            Roots = [
                new() {
                    Id = root,
                    Name = "Bulb",
                    Components = [new TestLamp { Intensity = 1f, Tint = new(1f, 1f, 1f) }]
                }
            ]
        };

    /// <summary>
    ///     The outer prefab: its own root with an instance of the inner one under it, turned up.
    /// </summary>
    /// <remarks>
    ///     ⚠ The nested node carries the <i>inner</i> link and not the outer's, which is what
    ///     <c>Prefab.Instantiate</c> writes and what makes a nested prefab nested at all. The outer
    ///     file's own id for that node is nowhere a scene can see it.
    /// </remarks>
    static SceneFile Turret(EntityId root, EntityId inner, float intensity) =>
        new() {
            Name = "Turret",
            Roots = [
                new() {
                    Id = root,
                    Name = "Turret",
                    Children = [
                        new() {
                            Id = EntityId.New(),
                            Name = "Bulb",
                            Prefab = Inner,
                            Source = inner,
                            Overrides = ["TestLamp.Intensity"],
                            Components = [new TestLamp { Intensity = intensity, Tint = new(1f, 1f, 1f) }]
                        }
                    ]
                }
            ]
        };

    /// <summary>A scene holding one instance of the outer prefab, as a placement would write it.</summary>
    static SceneFile Nested(EntityId root, EntityId inner, float intensity) =>
        Scene(
            new SceneEntityData {
                Id = EntityId.New(),
                Name = "Turret",
                Prefab = Asset,
                Source = root,
                Children = [
                    new() {
                        Id = EntityId.New(),
                        Name = "Bulb",
                        Prefab = Inner,
                        Source = inner,
                        Components = [new TestLamp { Intensity = intensity, Tint = new(1f, 1f, 1f) }]
                    }
                ]
            }
        );

    /// <summary>⚠⚠ A nested instance takes the outer prefab's copy, overrides and all.</summary>
    /// <remarks>
    ///     Doc 47 § 6's single level. The scene node carries the <i>inner</i> prefab's link, so the
    ///     obvious reading — reconcile it against the inner prefab — is available, wrong, and silent: it
    ///     throws away every override the outer prefab's author made over the inner one. The outer
    ///     template is what an instance of the outer shows.
    /// </remarks>
    [Fact]
    public void ANestedInstanceTakesTheOuterPrefabsCopy() {
        var root = EntityId.New();
        var inner = EntityId.New();

        var scene = Nested(root, inner, 0f);

        PrefabOverrides.Reconcile(
            scene,
            new Dictionary<string, SceneFile>(StringComparer.OrdinalIgnoreCase) {
                [Asset] = Turret(root, inner, 42f),
                [Inner] = Bulb(inner)
            }
        );

        Assert.Equal(42f, ((TestLamp) scene.Roots[0].Children[0].Components[0]).Intensity);
    }

    /// <summary>⚠ The outer template is composed against the inner one first, and then the scene.</summary>
    /// <remarks>
    ///     The other half of "outer over inner": the outer file's nested node holds resolved values, so
    ///     a member the outer's author did <i>not</i> claim has to come from the inner prefab before the
    ///     scene reads it. Composing the template is an ordinary reconcile of one file — which is what
    ///     <c>PrefabReconcile.Run</c> does to every template before it touches the scene.
    /// </remarks>
    [Fact]
    public void ComposingTheOuterTemplateBringsTheInnerPrefabsUnclaimedMembers() {
        var root = EntityId.New();
        var inner = EntityId.New();

        var outer = Turret(root, inner, 42f);
        var bulb = Bulb(inner);

        bulb.Roots[0].Name = "Bulb Mk II";
        ((TestLamp) bulb.Roots[0].Components[0]).Tint = new(0f, 1f, 0f);

        PrefabOverrides.Reconcile(outer, Inner, bulb);

        var nested = outer.Roots[0].Children[0];

        // The name and the tint follow the inner prefab; the intensity is the outer author's.
        Assert.Equal("Bulb Mk II", nested.Name);
        Assert.Equal(new Vector3(0f, 1f, 0f), ((TestLamp) nested.Components[0]).Tint);
        Assert.Equal(42f, ((TestLamp) nested.Components[0]).Intensity);
    }

    /// <summary>⚠ A run inside an instance whose template is missing is left exactly as the file has it.</summary>
    /// <remarks>
    ///     Without the outer template there is no telling a nested node of that prefab from a separate
    ///     instance dragged in under it, and the two want opposite treatments. Reconciling against the
    ///     inner prefab on that guess is the destructive one — an unbuilt or renamed outer prefab would
    ///     silently strip its overrides off every instance in the level.
    /// </remarks>
    [Fact]
    public void ANestedRunWhoseOuterTemplateIsMissingIsLeftAlone() {
        var root = EntityId.New();
        var inner = EntityId.New();

        var scene = Nested(root, inner, 42f);

        var written = PrefabOverrides.Reconcile(scene, Inner, Bulb(inner));

        Assert.Equal(0, written);
        Assert.Equal(42f, ((TestLamp) scene.Roots[0].Children[0].Components[0]).Intensity);
    }

    /// <summary>An instance of another prefab dropped under this one is an addition, and is left alone.</summary>
    /// <remarks>
    ///     The same shape as a nested node from the outside — an instance of B under an instance of A —
    ///     and the opposite case: A's template says nothing about it, so it belongs to B and B alone.
    ///     Doc 47 § 5's "an addition needs no syntax".
    /// </remarks>
    [Fact]
    public void AnInstanceDraggedUnderAnotherIsReconciledAgainstItsOwnPrefab() {
        var root = EntityId.New();
        var inner = EntityId.New();

        var scene = Nested(root, inner, 0f);

        // The outer prefab has no nested instance at all: what is in the scene was dropped there.
        var bare = new SceneFile { Name = "Turret", Roots = [new() { Id = root, Name = "Turret" }] };

        PrefabOverrides.Reconcile(
            scene,
            new Dictionary<string, SceneFile>(StringComparer.OrdinalIgnoreCase) {
                [Asset] = bare,
                [Inner] = Bulb(inner)
            }
        );

        Assert.Equal(1f, ((TestLamp) scene.Roots[0].Children[0].Components[0]).Intensity);
    }

    /// <summary>⚠ A nested instance the outer prefab gained is grafted whole, inner link intact.</summary>
    /// <remarks>
    ///     Add-back over a nesting. The grafted node keeps the inner prefab's link rather than being
    ///     stamped with the outer's, for the reason the writer declines to record over one: stamping it
    ///     would flatten a level of nesting silently, and the subtree would answer to the wrong template
    ///     for ever after.
    /// </remarks>
    [Fact]
    public void ANestedInstanceTheOuterPrefabGainedIsAddedBack() {
        var root = EntityId.New();
        var inner = EntityId.New();

        var instance = new SceneEntityData {
            Id = EntityId.New(),
            Name = "Turret",
            Prefab = Asset,
            Source = root
        };

        var scene = Scene(instance);

        PrefabOverrides.Reconcile(
            scene,
            new Dictionary<string, SceneFile>(StringComparer.OrdinalIgnoreCase) {
                [Asset] = Turret(root, inner, 42f),
                [Inner] = Bulb(inner)
            }
        );

        var added = Assert.Single(instance.Children);

        Assert.Equal(Inner, added.Prefab);
        Assert.Equal(inner, added.Source);
        Assert.Equal(["TestLamp.Intensity"], added.Overrides);
        Assert.Equal(42f, ((TestLamp) added.Components[0]).Intensity);
    }

    /// <summary>⚠⚠ A nested instance the designer deleted is not regrown by the outer's add-back.</summary>
    /// <remarks>
    ///     The removed list names the id the template gave the entity, and for a nested node that id is
    ///     the <i>inner</i> prefab's — because that is the half of the link both files write down. A
    ///     rule that compared the whole link rather than the id would let this one through.
    /// </remarks>
    [Fact]
    public void ADeletedNestedInstanceIsNotRegrown() {
        var root = EntityId.New();
        var inner = EntityId.New();

        var instance = new SceneEntityData {
            Id = EntityId.New(),
            Name = "Turret",
            Prefab = Asset,
            Source = root,
            Removed = [inner]
        };

        PrefabOverrides.Reconcile(
            Scene(instance),
            new Dictionary<string, SceneFile>(StringComparer.OrdinalIgnoreCase) {
                [Asset] = Turret(root, inner, 42f),
                [Inner] = Bulb(inner)
            }
        );

        Assert.Empty(instance.Children);
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
