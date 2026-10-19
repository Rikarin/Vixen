// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets;

namespace Vixen.Editor.Ai;

/// <summary>The importers this assembly brings to a host that links it.</summary>
/// <remarks>
///     <para>
///         <b>#1300's option A, the AI authoring half.</b> The behaviour-tree, utility-set, query and GOAP
///         importers were four rows in <c>Vixen.Editor.Assets</c>' hand list — the four whose absence from
///         it once left the whole AI authoring pipeline unreachable. They live beside the tree's authoring
///         model now, and reach a registry because every host that links this assembly — the editor
///         (<c>EditorModules</c>), <c>vixen</c> (<c>VixenCommand</c>) and the compiler worker
///         (<c>WorkerHost</c>) — calls <see cref="Link" /> at start-up.
///     </para>
///     <para>
///         ⚠ <b>Into <see cref="ImporterContributions.Linked" />, never <see cref="ImporterContributions.Default" /></b>,
///         for <c>VfxGraphImporters</c>' reason: a worker handed this assembly as a plugin and linking it
///         too would have two importers claiming <c>.vxbt</c>.
///     </para>
/// </remarks>
public static class AiImporters {
    /// <summary>Links this assembly's importers into a set.</summary>
    /// <param name="linked">The set — <see cref="ImporterContributions.Linked" />, or a test's own.</param>
    public static void Link(ImporterContributions linked) {
        ArgumentNullException.ThrowIfNull(linked);

        linked.Link(new BehaviorTreeImporter());
        linked.Link(new UtilitySetImporter());
        linked.Link(new QueryImporter());
        linked.Link(new GoapDomainImporter());
    }
}
