// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.Content;
using Vixen.Editor.Testing;
using Vixen.Testing.Importers;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The editor's importer registry has the map every host must have (#1300).</summary>
/// <remarks>
///     The map and the reason it is a literal are <see cref="HostImporterMap" />'s. ⚠ Built over a set
///     this host's own link method filled, not over <c>ImporterContributions.Linked</c>, which every host
///     in this test process links into.
/// </remarks>
public sealed class HostImporterMapTests {
    /// <summary>The gate.</summary>
    [Fact]
    public void The_editors_registry_has_every_hosts_map() => HostImporterMap.AssertHost("editor", EditorModules.LinkImporters);

    /// <summary>⚠ An opened editor's registries have the linked importers, including the earliest one built.</summary>
    /// <remarks>
    ///     The half the map cannot see: that the editor calls its link method at all, and early enough.
    ///     <c>AssetPicker</c> builds its registry in a field initializer, inside
    ///     <c>EditorApplication</c>'s constructor, so a link made when a module activates would be too
    ///     late for it. ⚠ Red with <c>EditorModules</c>' static constructor removed — nothing else in
    ///     this test process links the VFX importer.
    /// </remarks>
    [Fact]
    public void An_opened_editor_imports_an_effect_with_the_importer_it_linked() {
        using var session = EditorSession.Start();

        Assert.True(ProjectWorkspace.Importers().TryGetForFile("Assets/Embers.vxvfx", out var importer));
        Assert.Equal("VfxImporter", importer.GetType().Name);
    }
}
