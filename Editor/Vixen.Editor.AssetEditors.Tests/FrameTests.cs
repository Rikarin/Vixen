// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Frame;
using Vixen.Editor.Inspector;
using Vixen.Engine.Transforms;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.PostFx;
using Vixen.Ui;
using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>What the frame document does to a <c>.vxcompositor</c>, and what it refuses to.</summary>
public class FrameDocumentTests : IDisposable {
    const string Knobs = """
        version: 2
        game: !StandardFrame
          quality: High
          shadows: Cascades
          antialiasing: Taa
          look: !Look
            settings:
              ev100: 13
              fogDensity: 0.02
        """;

    const string Authored = """
        version: 2
        game: !SingleStage
          name: Opaque
        """;

    readonly EditorFixture fixture = new();

    public void Dispose() {
        fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    StandardFrameDocument Open(string text, string name = "Frame.vxcompositor") =>
        new(fixture.Project, AssetId.New(), fixture.Write("Assets/" + name, text));

    /// <summary>The knobs arrive in the mirror the inspector edits.</summary>
    [Fact]
    public void TheKnobsAreRead() {
        var document = Open(Knobs);

        Assert.True(document.CanEdit);
        Assert.Equal(FrameQualityChoice.High, document.Settings.Quality);
        Assert.Equal(ShadowMode.Cascades, document.Settings.Shadows);
        Assert.Equal(AntialiasingMode.Taa, document.Settings.Antialiasing);
        Assert.Equal(13f, document.Look.Ev100);
        Assert.Equal(0.02f, document.Look.FogDensity);
    }

    /// <summary>
    ///     ⚠ A knob that says nothing is not a knob that says zero, which is the whole optional model.
    /// </summary>
    [Fact]
    public void WhatTheLookDoesNotSayStaysUnsaid() {
        var document = Open(Knobs);

        Assert.Null(document.Look.Saturation);
        Assert.Null(document.Look.BloomIntensity);
    }

    /// <summary>A write reaches the document and the expansion is rebuilt from it.</summary>
    /// <remarks>
    ///     ⚠ <b>The count is the assertion, not the call.</b> This is the panel's live-apply claim in
    ///     one line: turning the shadows off has to take the caster stage and its atlases out of the
    ///     frame a builder would build, without a save and without a restart.
    /// </remarks>
    [Fact]
    public void TurningShadowsOffTakesStagesOutOfTheExpansion() {
        var document = Open(Knobs);
        var before = document.Expanded.Stages.Length;

        document.Settings.Shadows = ShadowMode.Off;

        Assert.True(document.Apply());
        Assert.True(document.Expanded.Stages.Length < before);
    }

    /// <summary>And the change is announced, because that is what the panel restates on.</summary>
    [Fact]
    public void ApplyingAnnouncesItself() {
        var document = Open(Knobs);
        var announced = 0;

        document.Changed += _ => announced++;
        document.Settings.Gi = GiMode.Ambient;
        document.Apply();

        Assert.Equal(1, announced);
    }

    /// <summary>Everything the mirrors do not model survives a round trip through them.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this pins is an editor deleting the half of a file it cannot draw.</b>
    ///     The node's name is not on the knobs form and must still be there afterwards.
    /// </remarks>
    [Fact]
    public void WhatThePanelDoesNotDrawSurvivesAWrite() {
        var document = Open(
            """
            version: 2
            game: !StandardFrame
              name: TheFrame
              shadows: Cascades
            """
        );

        document.Settings.Antialiasing = AntialiasingMode.Fxaa;
        document.Apply();

        Assert.Equal("TheFrame", document.Node?.Name);
    }

    /// <summary>A hand-authored document opens, shows its stacks, and refuses to be written.</summary>
    /// <remarks>
    ///     ⚠ <b>Read-only rather than unopenable.</b> Saving it from this panel would reserialise
    ///     eleven hundred lines and drop every comment in them, which is a worse outcome than a panel
    ///     that says it will not.
    /// </remarks>
    [Fact]
    public void AHandAuthoredDocumentIsReadOnly() {
        var document = Open(Authored);

        Assert.False(document.CanEdit);
        Assert.False(document.CanExplode);
        Assert.False(document.Apply());
        Assert.Throws<InvalidOperationException>(document.Save);
    }

    /// <summary>Explode replaces the node with the graph, and keeps what was there.</summary>
    [Fact]
    public void ExplodeIsOneWayAndNotDestructive() {
        var document = Open(Knobs);
        var kept = document.Explode();

        Assert.True(File.Exists(kept));
        Assert.Contains("!StandardFrame", File.ReadAllText(kept), StringComparison.Ordinal);

        // ⚠ And the panel stops claiming this is a Standard Frame, which is the half of "one-way"
        // that a form left showing knobs over a file without them would get wrong silently.
        Assert.False(document.CanEdit);
        Assert.Null(document.Node);

        var exploded = File.ReadAllText(document.AssetPath);

        // ⚠ The node, not the word: the header comment says what the file was exploded *from*, and
        // saying so is the point of the header.
        Assert.DoesNotContain("game: !StandardFrame", exploded, StringComparison.Ordinal);
        Assert.Contains("one-way", exploded, StringComparison.Ordinal);
        Assert.NotEmpty(document.Expanded.Stages);
    }

    /// <summary>The project's preset is found beside the frame and reaches the resolved table.</summary>
    [Fact]
    public void TheProjectPresetIsFoundByConvention() {
        fixture.Write(
            "Assets/" + StandardFrameDocument.PresetFile,
            """
            high: !QualityTierOverrides
              shadows: !ShadowQuality { cascadeResolution: 4096 }
            """
        );

        var document = Open(Knobs);

        Assert.NotNull(document.Preset);

        var knob = ResolvedQualityTable
            .Resolve(document.Tier, document.Preset, document.Node?.Preset)
            .Single(entry => entry.Path == "shadows.cascadeResolution");

        Assert.Equal(QualityLayer.Project, knob.Layer);
        Assert.Equal("4096", knob.Value);
    }
}

/// <summary>The resolved quality table, against the fold it is a view of.</summary>
public class FrameQualityTableTests {
    /// <summary>
    ///     Every knob the table shows resolves to what the engine's own waterfall resolved.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is what keeps the panel from lying.</b> The table walks
    ///     <see cref="QualityTierOverrides" />'s schema and <c>RenderQuality.Resolve</c> walks its own
    ///     sixty-two <c>Pick</c> calls; they are the same waterfall read two ways, and the day they
    ///     stop agreeing is the day the panel starts sending people to edit the wrong file. The two
    ///     spellings differ in one place — the document says <c>reflections.screenSteps</c> where the
    ///     resolved struct says <c>ReflectionSteps</c> — and a second divergence should fail here
    ///     rather than be absorbed.
    /// </remarks>
    [Theory]
    [InlineData(QualityTier.Low)]
    [InlineData(QualityTier.Medium)]
    [InlineData(QualityTier.High)]
    [InlineData(QualityTier.Epic)]
    public void TheTableAgreesWithTheEngine(QualityTier tier) {
        var resolved = RenderQuality.Resolve(tier);

        var members = typeof(ResolvedQuality)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

        var table = ResolvedQualityTable.Resolve(tier);

        Assert.NotEmpty(table);

        foreach (var knob in table) {
            var name = knob.Name switch {
                "screenSteps" => "ReflectionSteps",
                _ => knob.Name
            };

            Assert.True(members.TryGetValue(name, out var member), $"'{knob.Path}' has no ResolvedQuality member.");
            Assert.Equal(Format(member.GetValue(resolved)), knob.Value);
        }
    }

    /// <summary>Every knob the engine has is in the table, so none can be quietly missing.</summary>
    [Fact]
    public void TheTableCoversTheWholeEngineTable() =>
        Assert.Equal(
            typeof(ResolvedQuality).GetProperties(BindingFlags.Public | BindingFlags.Instance).Length,
            ResolvedQualityTable.Resolve(QualityTier.High).Count
        );

    /// <summary>Nothing above the engine table means every row says so.</summary>
    [Fact]
    public void EngineDefaultsAreAttributedToTheEngine() =>
        Assert.All(
            ResolvedQualityTable.Resolve(QualityTier.High),
            knob => {
                Assert.Equal(QualityLayer.Engine, knob.Layer);
                Assert.False(knob.Overridden);
            }
        );

    /// <summary>
    ///     ⚠ A layer that states a value the engine already had has still taken ownership of it.
    /// </summary>
    /// <remarks>
    ///     The distinction the panel exists to draw: "the highest layer that <em>states</em> it" is
    ///     not "the highest layer that <em>changes</em> it", and a table built by diffing resolved
    ///     values would send somebody to the wrong file for exactly this row.
    /// </remarks>
    [Fact]
    public void StatingTheEnginesOwnValueIsStillAnOverride() {
        var engine = RenderQuality.Resolve(QualityTier.High).CascadeCount;

        var project = new RenderQualityAsset {
            High = new QualityTierOverrides { Shadows = new ShadowQuality { CascadeCount = engine } }
        };

        var knob = ResolvedQualityTable
            .Resolve(QualityTier.High, project)
            .Single(entry => entry.Path == "shadows.cascadeCount");

        Assert.Equal(QualityLayer.Project, knob.Layer);
        Assert.True(knob.Overridden);
    }

    /// <summary>And a document's inline preset out-votes the project's.</summary>
    [Fact]
    public void TheDocumentOverlayIsTheTopVote() {
        var project = new RenderQualityAsset {
            High = new QualityTierOverrides { Shadows = new ShadowQuality { CascadeCount = 3 } }
        };

        var overlay = new RenderQualityAsset {
            High = new QualityTierOverrides { Shadows = new ShadowQuality { CascadeCount = 2 } }
        };

        var knob = ResolvedQualityTable
            .Resolve(QualityTier.High, project, overlay)
            .Single(entry => entry.Path == "shadows.cascadeCount");

        Assert.Equal(QualityLayer.Document, knob.Layer);
        Assert.Equal("2", knob.Value);
    }

    static string Format(object? value) => value switch {
        bool flag => flag ? "true" : "false",
        float number => number.ToString("0.####", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? "—"
    };
}

/// <summary>The per-camera volume stack, against the fold it reads.</summary>
public class FrameVolumeStackTests : IDisposable {
    readonly World world = new();

    public void Dispose() {
        world.Dispose();
        GC.SuppressFinalize(this);
    }

    Entity Volume(Vector3 at, in PostProcessVolume volume) {
        var entity = world.Create();

        world.Add(entity, volume);
        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(at) });

        return entity;
    }

    /// <summary>A scene with no volumes and no look folds to nothing, and says so.</summary>
    [Fact]
    public void NothingFoldsToNothing() {
        var report = new ResolvedVolumes().Fold(world);

        Assert.Equal(0, report.Volumes);
        Assert.Equal(0, report.Contributing);
        Assert.Empty(report.Parameters);
        Assert.Contains("says nothing", report.Summary, StringComparison.Ordinal);
    }

    /// <summary>The look alone is a layer, at full weight, and it is named as one.</summary>
    [Fact]
    public void TheLookIsTheBaseLayer() {
        var stack = new ResolvedVolumes { Look = new PostProcessSettings { Ev100 = 13f } };
        var report = stack.Fold(world);

        Assert.True(report.HasLook);

        var parameter = Assert.Single(report.Parameters);

        Assert.Equal("ev100", parameter.Parameter);
        Assert.Equal("look", parameter.Winner);
        Assert.False(parameter.IsContested);
        Assert.Equal(1f, parameter.Weight);
    }

    /// <summary>
    ///     ⚠ A volume placed and not reaching is counted, because it looks exactly like one that is
    ///     not wired up.
    /// </summary>
    [Fact]
    public void WhatDoesNotReachIsStillCounted() {
        Volume(new(0f, 0f, 0f), new() { Unbound = true, Weight = 1f, Settings = new() { Saturation = 1.1f } });
        Volume(new(500f, 0f, 0f), new() { Extents = new(1f, 1f, 1f), Weight = 1f, Settings = new() { Saturation = 0f } });

        var report = new ResolvedVolumes().Fold(world);

        Assert.Equal(2, report.Volumes);
        Assert.Equal(1, report.Contributing);
        Assert.Contains("1 of 2", report.Summary, StringComparison.Ordinal);
    }

    /// <summary>Two layers claiming one parameter is contested, and the last one wins.</summary>
    /// <remarks>
    ///     The panel's most useful state: a parameter one layer claims is doing what its author
    ///     asked, and a parameter two claim is where somebody's edit is being out-voted.
    /// </remarks>
    [Fact]
    public void ThePriorityOrderIsTheAnswer() {
        Volume(new(0f, 0f, 0f), new() { Unbound = true, Priority = -100, Weight = 1f, Settings = new() { Saturation = 1.1f } });

        Volume(
            new(0f, 0f, 0f),
            new() { Extents = new(10f, 10f, 10f), Priority = 5, Weight = 1f, Settings = new() { Saturation = 0.5f } }
        );

        var report = new ResolvedVolumes().Fold(world);
        var parameter = Assert.Single(report.Parameters);

        Assert.True(parameter.IsContested);
        Assert.Equal(2, parameter.Layers.Count);
        Assert.Equal("scene", parameter.Layers[0]);
        Assert.Equal("volume(priority 5)", parameter.Winner);
        Assert.Equal("0.5", parameter.Value);
    }

    /// <summary>Moving the camera out of a volume takes it out of the stack.</summary>
    /// <remarks>
    ///     ⚠ <b>Which is the gesture the panel is for.</b> "My volume is not working" is answered by
    ///     flying into it and watching the count move; a stack that did not follow the viewport would
    ///     answer for a camera nobody is looking through.
    /// </remarks>
    [Fact]
    public void TheCameraDecidesWhatReaches() {
        Volume(
            new(0f, 0f, 0f),
            new() { Extents = new(5f, 5f, 5f), Weight = 1f, Settings = new() { Saturation = 0.5f } }
        );

        var stack = new ResolvedVolumes();

        Assert.Equal(1, stack.Fold(world).Contributing);

        stack.Camera = new(1000f, 0f, 0f);

        Assert.Equal(0, stack.Fold(world).Contributing);
    }
}

/// <summary>The two shipped markup forms, built and bound.</summary>
/// <remarks>
///     Doc 36 § P4's shape, on <c>BrushInspectorMarkupTests</c>' terms — against the real
///     <c>.vxml</c> rather than a fixture, because a test over markup written for the test is the
///     mistake F7 warns about with a green tick on it.
/// </remarks>
public class FrameInspectorMarkupTests : IDisposable {
    readonly UiDocument document = new(420f, 900f);

    public FrameInspectorMarkupTests() => InspectorTheme.Install(document);

    public void Dispose() {
        document.Dispose();
        GC.SuppressFinalize(this);
    }

    IReadOnlyList<PropertyField> Fields<T>(object edited) where T : Component, new() {
        var view = BuildContext.Build<T>(document, document.Root);

        MarkupBinding.Bind(view.Root, new InspectorTarget([edited]));
        document.Update();

        return [.. Descendants(view.Root).OfType<PropertyField>()];
    }

    /// <summary>Every knob the node has is on the form, and every row found its member.</summary>
    [Fact]
    public void TheFrameFormDrawsEveryKnob() {
        var fields = Fields<StandardFrameInspector>(new StandardFrameSettings());

        Assert.All(fields, field => Assert.NotNull(field.Row));

        Assert.Equal(
            ["Quality", "Shadows", "Gi", "Reflections", "Antialiasing", "Exposure", "Particles", "Output"],
            fields.Select(field => field.Path)
        );
    }

    /// <summary>And the look form draws every opinion a <c>.vxlook</c> can carry.</summary>
    [Fact]
    public void TheLookFormDrawsEveryOpinion() {
        var fields = Fields<LookInspector>(new LookSettings());

        Assert.All(fields, field => Assert.NotNull(field.Row));
        Assert.Equal(29, fields.Count);
    }

    /// <summary>A row writes through to the mirror, which is what makes the form an editor.</summary>
    [Fact]
    public void ARowWritesToTheMirror() {
        var settings = new StandardFrameSettings();
        var fields = Fields<StandardFrameInspector>(settings);

        var row = fields.Single(field => field.Path == "Shadows").Row;

        Assert.NotNull(row);
        Assert.True(row.Field.Write(ShadowMode.Virtual));
        Assert.Equal(ShadowMode.Virtual, settings.Shadows);
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var descendant in Descendants(child)) {
                yield return descendant;
            }
        }
    }
}
