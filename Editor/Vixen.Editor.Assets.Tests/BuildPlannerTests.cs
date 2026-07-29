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
    ///     An import that produced several artefacts is planned as several entries: the asset at its
    ///     own address, and each sub-asset under it.
    /// </summary>
    [Fact]
    public void EachSubAssetIsPlannedUnderTheAssetsAddress() {
        var project = new PlannedProject();

        project.Add(
            "Models/hero.fbx",
            address: "characters/hero",
            group: "UiCore",
            subAssets: ["Hero_Mesh", "Cloak_Mesh"]
        );

        project.Group("UiCore");

        var plan = project.Plan();

        Assert.True(plan.Succeeded);

        Assert.Equal(
            ["characters/hero", "characters/hero#Cloak_Mesh", "characters/hero#Hero_Mesh"],
            plan.Assets.Select(asset => asset.Address)
        );

        Assert.Equal(
            project.ArtifactOf("Models/hero.fbx", "Hero_Mesh"),
            plan.Assets.Single(asset => asset.Address == "characters/hero#Hero_Mesh").Id
        );

        Assert.Equal(
            project.ArtifactOf("Models/hero.fbx"),
            plan.Assets.Single(asset => asset.Address == "characters/hero").Id
        );
    }

    /// <summary>
    ///     Every planned entry carries the <c>vx:</c> identity that names it, which is what lets a
    ///     runtime resolve what a component holds into something loadable.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the only place in the build that holds both halves.</b> A sub-asset's address
    ///     carries its <i>name</i> and its reference carries its <i>id</i> — see
    ///     <c>BuildPlanner.SubAssetAddress</c> — so neither can be recovered from the other later. If
    ///     the reference is not written down here it cannot be written down at all.
    /// </remarks>
    [Fact]
    public void EveryPlannedEntryCarriesTheReferenceThatNamesIt() {
        var project = new PlannedProject();

        var asset = project.Add(
            "Models/hero.fbx",
            address: "characters/hero",
            group: "UiCore",
            subAssets: ["Hero_Mesh"]
        );

        project.Group("UiCore");

        var plan = project.Plan();

        Assert.True(plan.Succeeded);

        Assert.Equal(
            new AssetReference(asset),
            plan.Assets.Single(planned => planned.Address == "characters/hero").Reference
        );

        Assert.Equal(
            new AssetReference(asset, SubAssets.Derive("TextureImporter", "Mesh", "Hero_Mesh")),
            plan.Assets.Single(planned => planned.Address == "characters/hero#Hero_Mesh").Reference
        );
    }

    /// <summary>
    ///     <b>The reason sub-assets are in the catalog at all.</b> A chunk is only reachable once the
    ///     bundle holding it is mounted, and what mounts a bundle is an address in the load closure —
    ///     so the asset depends on its own parts, and a group that packs every address separately
    ///     still loads a model with its meshes.
    /// </summary>
    [Fact]
    public void TheAssetDependsOnItsOwnPartsSoLoadingItReachesThem() {
        var project = new PlannedProject();
        project.Add("Models/hero.fbx", address: "characters/hero", group: "Models", subAssets: ["Hero_Mesh"]);
        project.Group("Models", BundlePacking.PackSeparately);

        var plan = project.Plan();
        var result = new ContentBuilder("Windows").Build(plan.Groups, plan.Assets, project.Artifacts);

        Assert.Equal(
            ["characters/hero#Hero_Mesh"],
            plan.Assets.Single(asset => asset.Address == "characters/hero").Dependencies
        );

        // Dependency-first, which is the order the runtime mounts and deserialises in: the mesh is
        // an object before the model that points at it is read.
        Assert.Equal(
            ["characters/hero#Hero_Mesh", "characters/hero"],
            result.Catalog.Closure("characters/hero")
        );

        // And the two are genuinely in different bundles, so the closure is doing the work rather
        // than the packing quietly making it unnecessary.
        Assert.Equal(2, result.Bundles.Length);
    }

    /// <summary>
    ///     A sub-asset is in its owner's group and carries its owner's labels — a label is a query
    ///     over shipped content, so "everything labelled level1" has to reach a labelled model's
    ///     meshes, and a group that packs by label has to put an asset's parts where the asset is.
    /// </summary>
    [Fact]
    public void SubAssetsTakeTheirOwnersGroupAndLabels() {
        var project = new PlannedProject();
        project.AddFolder("Models", group: "Characters");

        project.Add(
            "Models/hero.fbx",
            address: "characters/hero",
            labels: ["level1"],
            subAssets: ["Hero_Mesh"]
        );

        project.Group("Characters");

        var mesh = project.Plan().Assets.Single(asset => asset.Address == "characters/hero#Hero_Mesh");

        Assert.Equal("Characters", mesh.Group);
        Assert.Equal(["level1"], mesh.Labels);
    }

    /// <summary>
    ///     Every part carries the asset's dependencies, so a mesh loaded on its own still mounts what
    ///     it needs. Which part uses which dependency is not recorded, and over-claiming costs a
    ///     bundle that was going to be there anyway; under-claiming fails at load.
    /// </summary>
    [Fact]
    public void EveryPartCarriesTheAssetsDependencies() {
        var project = new PlannedProject();
        var texture = project.Add("Textures/skin.png", address: "textures/skin", group: "UiCore");

        project.Add(
            "Models/hero.fbx",
            address: "characters/hero",
            group: "UiCore",
            dependsOn: [texture],
            subAssets: ["Hero_Mesh"]
        );

        project.Group("UiCore");

        var plan = project.Plan();

        Assert.Equal(
            ["textures/skin"],
            plan.Assets.Single(asset => asset.Address == "characters/hero#Hero_Mesh").Dependencies
        );

        // And the owner's list is its parts and its dependencies together, sorted.
        Assert.Equal(
            ["characters/hero#Hero_Mesh", "textures/skin"],
            plan.Assets.Single(asset => asset.Address == "characters/hero").Dependencies
        );
    }

    /// <summary>
    ///     A chunk whose sub-asset the sidecar does not name cannot be addressed, and the whole asset
    ///     is refused rather than shipped with a part missing.
    /// </summary>
    [Fact]
    public void AChunkForASubAssetTheSidecarDoesNotNameRefusesTheAsset() {
        var project = new PlannedProject();

        project.Add(
            "Models/hero.fbx",
            address: "characters/hero",
            group: "UiCore",
            subAssets: ["Hero_Mesh"],
            undeclared: ["Cloak_Mesh"]
        );

        project.Group("UiCore");

        var plan = project.Plan();

        Assert.False(plan.Succeeded);
        Assert.Empty(plan.Assets);

        Assert.Contains(
            plan.Diagnostics,
            said => said.Message.Contains("does not name", StringComparison.Ordinal)
        );
    }

    /// <summary>An address has to name something, so an import with no main object is refused.</summary>
    [Fact]
    public void AnImportWithNoMainObjectIsAnError() {
        var project = new PlannedProject();

        project.Add(
            "Models/hero.fbx",
            address: "characters/hero",
            group: "UiCore",
            subAssets: ["Hero_Mesh"],
            main: false
        );

        project.Group("UiCore");

        var plan = project.Plan();

        Assert.False(plan.Succeeded);
        Assert.Empty(plan.Assets);
        Assert.Contains(plan.Diagnostics, said => said.Message.Contains("names nothing", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A name is unique per kind and not per asset, so an FBX with a mesh and a material both
    ///     called Body has two sub-assets with different ids and one address between them. The id
    ///     collision is caught at import; this is the other half, and it is the ordinary case rather
    ///     than a contrived one.
    /// </summary>
    [Fact]
    public void TwoSubAssetsOfDifferentKindsSharingANameIsAnError() {
        var project = new PlannedProject();

        project.Add(
            "Models/hero.fbx",
            address: "characters/hero",
            group: "UiCore",
            subAssets: ["Body"],
            materials: ["Body"]
        );

        project.Group("UiCore");

        var plan = project.Plan();

        Assert.False(plan.Succeeded);
        Assert.Empty(plan.Assets);

        Assert.Contains(
            plan.Diagnostics,
            said => said.Message.Contains("a Mesh and a Material both called 'Body'", StringComparison.Ordinal)
        );
    }

    /// <summary>Two chunks for one sub-asset is an importer bug, and an address that names both.</summary>
    [Fact]
    public void TwoChunksForOneSubAssetIsAnError() {
        var project = new PlannedProject();
        project.Add("Models/hero.fbx", address: "characters/hero", group: "UiCore", writeMainTwice: true);
        project.Group("UiCore");

        var plan = project.Plan();

        Assert.False(plan.Succeeded);
        Assert.Empty(plan.Assets);
        Assert.Contains(plan.Diagnostics, said => said.Message.Contains("two chunks", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A sub-asset's address is in the same space as everything else's, so an asset that claims
    ///     one by hand collides with it — and both are refused, the model along with its parts.
    /// </summary>
    [Fact]
    public void AnAssetClaimingASubAssetsAddressCollidesWithIt() {
        var project = new PlannedProject();
        project.Add("Models/hero.fbx", address: "characters/hero", group: "UiCore", subAssets: ["Hero_Mesh"]);
        project.Add("Textures/impostor.png", address: "characters/hero#Hero_Mesh", group: "UiCore");
        project.Group("UiCore");

        var plan = project.Plan();

        Assert.False(plan.Succeeded);
        Assert.Empty(plan.Assets);

        Assert.Contains(
            plan.Diagnostics,
            said => said.Message.Contains("all claim", StringComparison.Ordinal)
                && said.Message.Contains("characters/hero#Hero_Mesh", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     A dependency on an asset is a dependency on the asset, not on a part of it: the dependent
    ///     names the address, and gets the parts through its closure.
    /// </summary>
    [Fact]
    public void ADependencyOnAnAssetWithPartsResolvesToTheAssetsOwnAddress() {
        var project = new PlannedProject();
        var model = project.Add("Models/hero.fbx", address: "characters/hero", group: "UiCore", subAssets: ["Hero_Mesh"]);
        project.Add("Prefabs/hero.vxprefab", address: "prefabs/hero", group: "UiCore", dependsOn: [model]);
        project.Group("UiCore");

        var plan = project.Plan();

        Assert.True(plan.Succeeded);

        Assert.Equal(
            ["characters/hero"],
            plan.Assets.Single(asset => asset.Address == "prefabs/hero").Dependencies
        );

        var result = new ContentBuilder("Windows").Build(plan.Groups, plan.Assets, project.Artifacts);

        Assert.Equal(
            ["characters/hero#Hero_Mesh", "characters/hero", "prefabs/hero"],
            result.Catalog.Closure("prefabs/hero")
        );
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
            string[]? subAssets = null,
            string[]? materials = null,
            bool main = true,
            string[]? undeclared = null,
            bool writeMainTwice = false
        ) =>
            Put(
                path,
                address,
                group,
                labels,
                dependsOn,
                imported,
                subAssets,
                materials,
                main,
                undeclared,
                writeMainTwice,
                false
            );

        public AssetId AddFolder(string path, string? group = null, string[]? labels = null) =>
            Put(path, null, group, labels, null, false, null, null, false, null, false, isFolder: true);

        /// <summary>An asset the index knows about whose sidecar will not read.</summary>
        public void AddUnreadable(string path) {
            var guid = AssetId.New();
            entries.Add(new(guid, path, "TextureImporter", 1, false));
            metas[path] = null;
        }

        public void Group(string name, BundlePacking packing = BundlePacking.PackTogether) =>
            groups.Add(new() { Name = name, Packing = packing });

        public ObjectId ArtifactOf(string path) => artifactOf[path];

        public ObjectId ArtifactOf(string path, string subAsset) => artifactOf[$"{path}::{subAsset}"];

        public BuildPlan Plan() =>
            BuildPlanner.Plan(entries, cache, entry => metas.GetValueOrDefault(entry.Path), groups);

        AssetId Put(
            string path,
            string? address,
            string? group,
            string[]? labels,
            AssetId[]? dependsOn,
            bool imported,
            string[]? subAssets,
            string[]? materials,
            bool main,
            string[]? undeclared,
            bool writeMainTwice,
            bool isFolder
        ) {
            var guid = AssetId.New();
            entries.Add(new(guid, path, isFolder ? null : "TextureImporter", 1, isFolder));

            // Two kinds, because a name is unique per kind and not per asset: an FBX with a mesh and
            // a material both called Body is ordinary, and their ids differ where their names do not.
            var declared = (subAssets ?? []).Select(name => Declare(name, "Mesh"))
                .Concat((materials ?? []).Select(name => Declare(name, "Material")))
                .ToArray();

            metas[path] = new() {
                Guid = guid,
                Addressable = address is null && group is null && labels is null
                    ? null
                    : new() { Address = address, Group = group, Labels = labels ?? [] },
                SubAssets = declared
            };

            if (imported) {
                var stored = new List<StoredArtifact>();

                if (main) {
                    stored.Add(new(SubAssetId.Main, Chunk(path, "main")));
                    artifactOf[path] = stored[0].Id;
                }

                if (writeMainTwice) {
                    stored.Add(new(SubAssetId.Main, Chunk(path, "again")));
                }

                foreach (var entry in declared) {
                    var chunk = Chunk(path, $"{entry.Type}:{entry.Name}");
                    stored.Add(new(entry.Id, chunk));
                    artifactOf[$"{path}::{entry.Name}"] = chunk;
                }

                // Chunks for sub-assets the sidecar does not declare, which is what an asset
                // imported and then rewritten by hand looks like.
                foreach (var name in undeclared ?? []) {
                    stored.Add(new(SubAssets.Derive("TextureImporter", "Mesh", name), Chunk(path, name)));
                }

                cache.Set(new(guid, "TextureImporter", 1, default, stored, [], dependsOn ?? []));
            }

            return guid;
        }

        static SubAssetEntry Declare(string name, string type) =>
            new() { Id = SubAssets.Derive("TextureImporter", type, name), Name = name, Type = type };

        /// <summary>Writes a chunk for real, so the content builder downstream has one to pack.</summary>
        ObjectId Chunk(string path, string part) =>
            new ObjectDatabase(Artifacts).WriteRaw(1, [], System.Text.Encoding.UTF8.GetBytes($"{path}::{part}"));
    }
}
