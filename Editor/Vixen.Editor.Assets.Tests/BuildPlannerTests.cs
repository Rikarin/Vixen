// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     The step between "every asset has been imported" and "there is a build". Imports produce chunks
///     and know nothing about addresses; the content builder takes addresses and knows nothing about
///     imports. These are the tests for what happens in between, and for the mistakes that would
///     otherwise surface as a load failure on a device.
/// </summary>
public sealed class BuildPlannerTests {
    /// <summary>An asset with an address and a group is planned as itself.</summary>
    [Fact]
    public void AnAddressedAssetIsPlanned() {
        var project = new PlannedProject();
        project.Add("Textures/hero.png", address: "ui/hero", group: "UiCore", labels: ["ui", "hd"]);
        project.Group("UiCore");

        var plan = project.Plan();

        Assert.True(plan.Succeeded);
        var asset = Assert.Single(plan.Assets);
        Assert.Equal("ui/hero", asset.Address);
        Assert.Equal("UiCore", asset.Group);
        Assert.Equal(["ui", "hd"], asset.Labels);
        Assert.Equal(project.ArtifactOf("Textures/hero.png"), asset.Id);
    }

    /// <summary>
    ///     Most files in a project are not addressable, and that is not a mistake. A source texture
    ///     only a material refers to is reached through the chunk graph and never asked for by name.
    /// </summary>
    [Fact]
    public void AnAssetWithNoAddressIsSkippedRatherThanRefused() {
        var project = new PlannedProject();
        project.Add("Textures/hero.png", address: "ui/hero", group: "UiCore");
        project.Add("Textures/detail.png", group: "UiCore");
        project.Group("UiCore");

        var plan = project.Plan();

        Assert.True(plan.Succeeded);
        Assert.Equal(["ui/hero"], plan.Assets.Select(asset => asset.Address));
        Assert.Empty(plan.Diagnostics);
    }

    /// <summary>
    ///     A folder names a group and its descendants inherit it, which is what makes "everything
    ///     under Assets/UI ships together" one line rather than one line per file.
    /// </summary>
    [Fact]
    public void AGroupIsInheritedFromTheNearestFolderThatNamesOne() {
        var project = new PlannedProject();
        project.AddFolder("UI", group: "UiCore");
        project.AddFolder("UI/Icons");
        project.Add("UI/Icons/save.png", address: "ui/icons/save");
        project.Add("UI/panel.png", address: "ui/panel");
        project.Group("UiCore");

        var plan = project.Plan();

        Assert.True(plan.Succeeded);
        Assert.All(plan.Assets, asset => Assert.Equal("UiCore", asset.Group));
    }

    /// <summary>The walk stops at the first ancestor that names one, so a subfolder can override.</summary>
    [Fact]
    public void ASubfolderOverridesItsParentsGroup() {
        var project = new PlannedProject();
        project.AddFolder("UI", group: "UiCore");
        project.AddFolder("UI/Hd", group: "UiHd");
        project.Add("UI/Hd/big.png", address: "ui/hd/big");
        project.Add("UI/small.png", address: "ui/small");
        project.Group("UiCore");
        project.Group("UiHd");

        var plan = project.Plan();

        Assert.Equal("UiHd", plan.Assets.Single(asset => asset.Address == "ui/hd/big").Group);
        Assert.Equal("UiCore", plan.Assets.Single(asset => asset.Address == "ui/small").Group);
    }

    /// <summary>An asset's own group beats anything it would have inherited.</summary>
    [Fact]
    public void AnAssetsOwnGroupWins() {
        var project = new PlannedProject();
        project.AddFolder("UI", group: "UiCore");
        project.Add("UI/special.png", address: "ui/special", group: "Special");
        project.Group("UiCore");
        project.Group("Special");

        Assert.Equal("Special", Assert.Single(project.Plan().Assets).Group);
    }

    /// <summary>
    ///     Labels are not inherited. A folder-wide label would be impossible to remove from one of its
    ///     children, and a label is a query — the thing you most want to say "all of these except that
    ///     one" about.
    /// </summary>
    [Fact]
    public void LabelsAreNotInherited() {
        var project = new PlannedProject();
        project.AddFolder("UI", group: "UiCore", labels: ["everything"]);
        project.Add("UI/one.png", address: "ui/one", labels: ["mine"]);
        project.Add("UI/two.png", address: "ui/two");
        project.Group("UiCore");

        var plan = project.Plan();

        Assert.Equal(["mine"], plan.Assets.Single(asset => asset.Address == "ui/one").Labels);
        Assert.Empty(plan.Assets.Single(asset => asset.Address == "ui/two").Labels);
    }

    /// <summary>
    ///     A project that has not configured anything still builds, in a group the planner invents —
    ///     and is told that it did, because the moment it cares about compression or remote delivery
    ///     it needs a real one.
    /// </summary>
    [Fact]
    public void AnAssetWithNoGroupAnywhereGetsAnInventedOneAndIsToldSo() {
        var project = new PlannedProject();
        project.Add("hero.png", address: "ui/hero");

        var plan = project.Plan();

        Assert.True(plan.Succeeded);
        Assert.Equal(BuildPlanner.DefaultGroupName, Assert.Single(plan.Assets).Group);
        Assert.Equal(BuildPlanner.DefaultGroupName, Assert.Single(plan.Groups).Name);

        var said = Assert.Single(plan.Diagnostics);
        Assert.Equal(ImportSeverity.Information, said.Severity);
        Assert.Contains(BuildPlanner.DefaultGroupName, said.Message, StringComparison.Ordinal);
    }

    /// <summary>Only the groups actually used come out, so an unused .vxgroup does not make a bundle.</summary>
    [Fact]
    public void OnlyTheGroupsThatAreUsedAreReturned() {
        var project = new PlannedProject();
        project.Add("hero.png", address: "ui/hero", group: "UiCore");
        project.Group("UiCore");
        project.Group("NeverUsed");

        Assert.Equal(["UiCore"], project.Plan().Groups.Select(group => group.Name));
    }

    /// <summary>An address names one thing, and a build cannot decide between two claimants.</summary>
    [Fact]
    public void TwoAssetsClaimingOneAddressIsAnError() {
        var project = new PlannedProject();
        project.Add("a.png", address: "ui/hero", group: "UiCore");
        project.Add("b.png", address: "ui/hero", group: "UiCore");
        project.Group("UiCore");

        var plan = project.Plan();

        Assert.False(plan.Succeeded);
        Assert.Contains(
            plan.Diagnostics,
            said => said.Severity == ImportSeverity.Error && said.Message.Contains("all claim", StringComparison.Ordinal)
        );

        // And neither is planned, rather than one of them winning by enumeration order.
        Assert.Empty(plan.Assets);
    }

    /// <summary>A group nothing defines is named, along with the asset that asked for it.</summary>
    [Fact]
    public void AGroupNothingDefinesIsAnError() {
        var project = new PlannedProject();
        project.Add("hero.png", address: "ui/hero", group: "Missing");

        var plan = project.Plan();

        Assert.False(plan.Succeeded);
        Assert.Contains(
            plan.Diagnostics,
            said => said.Message.Contains("Missing", StringComparison.Ordinal)
                && said.Message.Contains("no .vxgroup", StringComparison.Ordinal)
        );

        // Not planned either. Every error case leaves its asset out, so a tool reading the plan
        // never sees an entry that a diagnostic elsewhere says is unbuildable.
        Assert.Empty(plan.Assets);
    }

    /// <summary>Shipping an address whose chunk was never produced is refused before it ships.</summary>
    [Fact]
    public void AnAddressedAssetThatWasNeverImportedIsAnError() {
        var project = new PlannedProject();
        project.Add("hero.png", address: "ui/hero", group: "UiCore", imported: false);
        project.Group("UiCore");

        var plan = project.Plan();

        Assert.False(plan.Succeeded);
        Assert.Contains(
            plan.Diagnostics,
            said => said.Message.Contains("has not been imported", StringComparison.Ordinal)
        );

        Assert.Empty(plan.Assets);
    }

    /// <summary>
    ///     <b>The check worth having.</b> The catalog records dependencies by address, so a dependency
    ///     with no address is in no bundle — the build succeeds, ships, and fails at load on a chunk
    ///     that was never packed.
    /// </summary>
    [Fact]
    public void DependingOnSomethingWithNoAddressIsAnError() {
        var project = new PlannedProject();
        var texture = project.Add("Textures/detail.png", group: "UiCore");
        project.Add("Materials/wall.mat", address: "materials/wall", group: "UiCore", dependsOn: [texture]);
        project.Group("UiCore");

        var plan = project.Plan();

        Assert.False(plan.Succeeded);
        Assert.Contains(
            plan.Diagnostics,
            said => said.Message.Contains("would not be packed", StringComparison.Ordinal)
        );

        // The material is left out rather than planned with a dependency list that quietly omits the
        // one it could not name.
        Assert.Empty(plan.Assets);
    }

    /// <summary>And a dependency that is addressed comes through as its address.</summary>
    [Fact]
    public void DependenciesComeThroughAsAddresses() {
        var project = new PlannedProject();
        var texture = project.Add("Textures/detail.png", address: "textures/detail", group: "UiCore");
        var normal = project.Add("Textures/normal.png", address: "textures/normal", group: "UiCore");
        project.Add("Materials/wall.mat", address: "materials/wall", group: "UiCore", dependsOn: [normal, texture]);
        project.Group("UiCore");

        var plan = project.Plan();

        Assert.True(plan.Succeeded);

        var material = plan.Assets.Single(asset => asset.Address == "materials/wall");

        // Sorted, because a catalog that reorders a dependency list turns a no-op rebuild into a diff.
        Assert.Equal(["textures/detail", "textures/normal"], material.Dependencies);
    }

    /// <summary>
    ///     Addressing sub-assets is not built, so an import that produced several artefacts is refused
    ///     rather than packed as its first one — a model whose meshes are missing fails at load, and
    ///     the failure would name the mesh rather than the thing that dropped it.
    /// </summary>
    [Fact]
    public void AnAssetWithSeveralArtefactsIsRefusedRatherThanPartlyPacked() {
        var project = new PlannedProject();
        project.Add("Models/hero.fbx", address: "characters/hero", group: "UiCore", artifacts: 3);
        project.Group("UiCore");

        var plan = project.Plan();

        Assert.False(plan.Succeeded);
        Assert.Empty(plan.Assets);
        Assert.Contains(plan.Diagnostics, said => said.Message.Contains("sub-assets", StringComparison.Ordinal));
    }

    /// <summary>The plan is in address order, because the build after it has to be reproducible.</summary>
    [Fact]
    public void ThePlanIsInAddressOrder() {
        var project = new PlannedProject();
        project.Add("c.png", address: "z/last", group: "UiCore");
        project.Add("a.png", address: "a/first", group: "UiCore");
        project.Add("b.png", address: "m/middle", group: "UiCore");
        project.Group("UiCore");

        Assert.Equal(["a/first", "m/middle", "z/last"], project.Plan().Assets.Select(asset => asset.Address));
    }

    /// <summary>An asset whose sidecar cannot be read is reported and left out.</summary>
    [Fact]
    public void AnUnreadableSidecarIsReported() {
        var project = new PlannedProject();
        project.Add("hero.png", address: "ui/hero", group: "UiCore");
        project.AddUnreadable("broken.png");
        project.Group("UiCore");

        var plan = project.Plan();

        Assert.True(plan.Succeeded);
        Assert.Contains(
            plan.Diagnostics,
            said => said.Severity == ImportSeverity.Warning
                && said.Message.Contains("no readable sidecar", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     What the planner produces goes straight into the builder — the assertion that the two halves
    ///     actually meet, rather than each being right about a shape the other does not have.
    /// </summary>
    [Fact]
    public void APlanIsWhatTheContentBuilderTakes() {
        var project = new PlannedProject();
        project.Add("Textures/detail.png", address: "textures/detail", group: "UiCore");
        project.Add("hero.png", address: "ui/hero", group: "UiCore");
        project.Group("UiCore");

        var plan = project.Plan();
        var result = new ContentBuilder("Windows").Build(plan.Groups, plan.Assets, project.Artifacts);

        Assert.Equal(2, result.Catalog.Count);
        Assert.True(result.Catalog.Contains("ui/hero"));
        Assert.True(result.Catalog.Contains("textures/detail"));
        Assert.Single(result.Bundles);
    }

    /// <summary>A project of sidecars, imports and groups, with no filesystem in sight.</summary>
    sealed class PlannedProject {
        readonly Dictionary<string, AssetMeta?> metas = new(StringComparer.Ordinal);
        readonly Dictionary<string, ObjectId> artifactOf = new(StringComparer.Ordinal);
        readonly List<AddressableGroup> groups = [];
        readonly List<AssetEntry> entries = [];
        readonly ImportCache cache = new();

        public FileOdbBackend Artifacts { get; }

        public PlannedProject() {
            var files = new VirtualFileSystem();
            files.Mount(new("/store"), new MemoryFileProvider());
            Artifacts = new(files, new("/store/odb"));
        }

        public AssetId Add(
            string path,
            string? address = null,
            string? group = null,
            string[]? labels = null,
            AssetId[]? dependsOn = null,
            bool imported = true,
            int artifacts = 1
        ) => Put(path, address, group, labels, dependsOn, imported, artifacts, isFolder: false);

        public AssetId AddFolder(string path, string? group = null, string[]? labels = null) =>
            Put(path, null, group, labels, null, imported: false, artifacts: 0, isFolder: true);

        /// <summary>An asset the index knows about whose sidecar will not read.</summary>
        public void AddUnreadable(string path) {
            var guid = AssetId.New();
            entries.Add(new(guid, path, "TextureImporter", 1, false));
            metas[path] = null;
        }

        public void Group(string name) => groups.Add(new() { Name = name });

        public ObjectId ArtifactOf(string path) => artifactOf[path];

        public BuildPlan Plan() =>
            BuildPlanner.Plan(entries, cache, entry => metas.GetValueOrDefault(entry.Path), groups);

        AssetId Put(
            string path,
            string? address,
            string? group,
            string[]? labels,
            AssetId[]? dependsOn,
            bool imported,
            int artifacts,
            bool isFolder
        ) {
            var guid = AssetId.New();
            entries.Add(new(guid, path, isFolder ? null : "TextureImporter", 1, isFolder));

            metas[path] = new() {
                Guid = guid,
                Addressable = address is null && group is null && labels is null
                    ? null
                    : new() { Address = address, Group = group, Labels = labels ?? [] }
            };

            if (imported) {
                var stored = new List<ObjectId>();

                for (var index = 0; index < artifacts; index++) {
                    // Written for real, so the content builder downstream has chunks to pack.
                    stored.Add(new ObjectDatabase(Artifacts).WriteRaw(1, [], System.Text.Encoding.UTF8.GetBytes($"{path}#{index}")));
                }

                if (stored.Count > 0) {
                    artifactOf[path] = stored[0];
                }

                cache.Set(new(guid, "TextureImporter", 1, default, stored, [], dependsOn ?? []));
            }

            return guid;
        }
    }
}
