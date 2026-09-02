// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Ecs;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>The six assemblies <c>Vixen.Ai</c> is allowed to reference, and nothing else.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28 put the three planners in <c>Vixen.Gameplay.Ai</c>, on top of
///         <c>Vixen.Gameplay.Combat</c>, and doc 37 § Why this is Core is the argument against
///         it.</b> A behaviour tree that depends on a combat package cannot run a stealth patrol, a
///         shopkeeper, a companion following a player through a puzzle, or a squad in a game with no
///         abilities at all. A composite that runs children until one fails contains no game concept
///         and is identical in every game ever shipped; a task that casts <c>Fireball</c> names a
///         definition, a cooldown and a resource. Those are two things and they belong in two layers.
///     </para>
///     <para>
///         ⚠ <b>That rule is the build's now, and the half of this file that asserted it is gone.</b>
///         <c>Gameplay/</c> is a real top level, docs 02 and 28 both draw it, and
///         <c>Build.ArchitectureRules.cs</c> refuses a <c>Core/</c> project that references
///         <c>Gameplay</c>, <c>Platform</c>, <c>Editor</c>, <c>Tools</c>, <c>Live</c> or
///         <c>Raven</c> — so a violating reference stops compiling instead of failing a test
///         somebody can delete. <c>VixenAiReferencesNothingAboveIt</c> was the fallback doc 37
///         § Testing described and it has been deleted, as that document said it should be.
///     </para>
///     <para>
///         ⚠ <b>What is left is the half no layer rule can express, and it is the stricter one.</b>
///         A layer rule is about directories; this is about six named assemblies. It says that a new
///         reference — even a downward one, even to another <c>Core/</c> project — is a decision
///         somebody makes rather than something that arrives with an unrelated feature. It has
///         earned its keep once already: P2 added <c>Vixen.Core.Serialization</c> and this failed
///         until somebody wrote down why, which is the comment in the set below.
///     </para>
///     <para>
///         ⚠ <b>And it is the only thing still checking that <c>Vixen.Ai</c> does not reference
///         <c>Vixen.Engine</c></b>, which is a <c>Core/</c> project and therefore invisible to the
///         layer rule. Doc 37 § P4 is the reason: the world-facing tasks that want a scene live in
///         <c>Vixen.Ai.Nodes</c>, so a game that wants trees without one links <c>Vixen.Ai</c> and
///         stops.
///     </para>
/// </remarks>
public class AiReferenceAllowlistTests {
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
