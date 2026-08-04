// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Ecs;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>
///     The single rule doc 37 exists to establish, asserted here until the build can assert it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 28 put the three planners in <c>Vixen.Gameplay.Ai</c>, on top of
///         <c>Vixen.Gameplay.Combat</c>, and that is what this file exists to prevent coming back.</b>
///         A behaviour tree that depends on a combat package cannot run a stealth patrol, a
///         shopkeeper, a companion following a player through a puzzle, or a squad in a game with no
///         abilities at all. A composite that runs children until one fails contains no game concept
///         and is identical in every game ever shipped; a task that casts <c>Fireball</c> names a
///         definition, a cooldown and a resource. Those are two things and they belong in two layers.
///     </para>
///     <para>
///         ⚠ <b>This test is the weaker form and it is meant to be deleted.</b> When the gameplay
///         spine moves out of <c>Core/</c> into a <c>Gameplay/</c> folder of its own — doc 02's tree
///         and doc 28 § Library structure own that move, not doc 37 — the rule becomes one line
///         beside the three already in <c>Build.ArchitectureRules.cs</c>, and a violating reference
///         stops compiling instead of failing a test somebody can delete. Until then, this.
///     </para>
/// </remarks>
public class AiLayeringTests {
    static readonly string[] Forbidden = ["Vixen.Gameplay", "Vixen.Editor", "Vixen.Platform.", "Vixen.Raven"];

    [Fact]
    public void VixenAiReferencesNothingAboveIt() {
        var references = typeof(AiAgent).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToList();

        var violations = references
            .Where(name => Forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"Vixen.Ai references {string.Join(", ", violations)}. See docs/plan/37 § Why this is Core and not Gameplay."
        );
    }

    /// <summary>
    ///     And the positive half: the list is short on purpose, so that a new reference is a decision
    ///     somebody makes rather than something that arrives with an unrelated feature.
    /// </summary>
    /// <remarks>
    ///     It has grown once, and the growth is what the test is for: P2 added
    ///     <c>Vixen.Core.Serialization</c> and this failed until somebody wrote down why.
    /// </remarks>
    [Fact]
    public void VixenAiReferencesOnlyTheSixItIsAllowed() {
        var allowed = new HashSet<string>(StringComparer.Ordinal) {
            "Vixen.Core",
            "Vixen.Core.Mathematics",
            "Vixen.Core.Threading",
            "Vixen.Ecs",

            // Added by P2, deliberately: a `.vxbt` is an artefact a content build writes and a player
            // reads back, so `BehaviorTreeContent` needs the generated serializer and the descriptor
            // over the same types. `Vixen.Animation` carries `MoveSetContent` for the same reason.
            // `Vixen.Core.Reflection` arrives with the serializer and is what makes an artefact's type
            // *alias* resolvable at load.
            "Vixen.Core.Serialization",
            "Vixen.Core.Reflection"
        };

        var unexpected = typeof(AiAgent).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .Where(name => name.StartsWith("Vixen.", StringComparison.Ordinal))
            .Where(name => !allowed.Contains(name))
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Vixen.Ai has grown a reference to {string.Join(", ", unexpected)}. If that is right, say so here and in the .csproj."
        );
    }
}
