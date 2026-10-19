// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
}
