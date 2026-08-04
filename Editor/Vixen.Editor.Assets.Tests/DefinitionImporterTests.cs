// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Gameplay;
using Vixen.Gameplay;
using Xunit;

namespace Tests;

/// <summary>One importer, several extensions, and the type tag doing the deciding.</summary>
public sealed class DefinitionImporterTests {
    [Fact]
    public void OneImporterClaimsEveryDefinitionExtension() {
        // docs/plan/28 G-Q1: one importer with type tags, extensions cosmetic. Asserted, because the
        // alternative — an importer per extension — is the thing somebody adds the first time a new
        // definition kind needs a file suffix.
        var importer = new DefinitionImporter();

        Assert.Contains(".vxdef", importer.Extensions);
        Assert.Contains(".vxitem", importer.Extensions);
        Assert.Contains(".vxquest", importer.Extensions);
        Assert.Contains(".vxeffect", importer.Extensions);
    }

    [Fact]
    public async Task AnEffectDefinitionRoundTripsThroughTheArtefact() {
        const string Yaml = """
            !EffectDefinition
            displayName: Burning
            duration: 6
            period: 2
            stacking: StackTo
            maximumStacks: 3
            tags:
              - Effect.Damage.Burning
            grantedTags:
              - State.Burning
            cancelOn:
              - Event.Cleansed
            modifiers:
              - attribute: Power
                op: AddPercent
                value: -0.1
            """;

        var result = await Import("/Assets/effects/burning.vxeffect", Yaml);

        Assert.DoesNotContain(result.Diagnostics, entry => entry.Severity == ImportSeverity.Error);

        var artefact = Assert.Single(result.Artifacts);

        Assert.Equal(DefinitionImporter.ArtefactType, artefact.Type);

        var definition = Assert.IsType<EffectDefinition>(
            DefinitionSerialization.FromBytes(artefact.Content.ToArray())
        );

        Assert.Equal("Burning", definition.DisplayName);
        Assert.Equal(6f, definition.Duration);
        Assert.Equal(2f, definition.Period);
        Assert.Equal(EffectStacking.StackTo, definition.Stacking);
        Assert.Equal(3, definition.MaximumStacks);
        Assert.Equal("Effect.Damage.Burning", Assert.Single(definition.Tags));
        Assert.Equal("State.Burning", Assert.Single(definition.GrantedTags));
        Assert.Equal("Event.Cleansed", Assert.Single(definition.CancelOn));

        var modifier = Assert.Single(definition.Modifiers);

        Assert.Equal("Power", modifier.Attribute);
        Assert.Equal(ModifierOp.AddPercent, modifier.Op);
        Assert.Equal(-0.1f, modifier.Value, 5);
    }

    [Fact]
    public async Task AFileWithNoTypeTagIsRefused() {
        var result = await Import("/Assets/effects/nameless.vxdef", "duration: 6\n");

        Assert.Contains(result.Diagnostics, entry => entry.Severity == ImportSeverity.Error);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public async Task AFileWhoseTagNamesNothingIsRefused() {
        var result = await Import("/Assets/effects/odd.vxdef", "!NoSuchDefinition\nduration: 6\n");

        Assert.Contains(result.Diagnostics, entry => entry.Severity == ImportSeverity.Error);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public async Task ADefinitionArrivesInACatalogAtTheAddressItWasFoundAt() {
        var result = await Import("/Assets/effects/burning.vxeffect", "!EffectDefinition\nduration: 6\n");

        var catalog = new DefinitionCatalogBuilder()
            .Add("effects/burning", Assert.Single(result.Artifacts).Content.Span)
            .Build();

        var burning = catalog.Find(DefId.From("effects/burning"));

        Assert.NotNull(burning);
        Assert.Equal("effects/burning", burning.Address);
        Assert.Equal(DefId.From("effects/burning"), burning.Id);
    }

    static async Task<ImportResult> Import(string at, string text) {
        var importer = new DefinitionImporter();
        var path = new VirtualPath(at);
        var files = new MemoryFileProvider();

        files.Seed(path, text);

        var context = new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows");

        return await importer.ImportAsync(context, TestContext.Current.CancellationToken);
    }
}
