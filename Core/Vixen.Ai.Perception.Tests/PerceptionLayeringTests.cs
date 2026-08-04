// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Perception.Ecs;
using Xunit;

namespace Vixen.Ai.Perception.Tests;

/// <summary>Why this is a second assembly, asserted rather than only written down.</summary>
/// <remarks>
///     <para>
///         <b>Perception needs the world and a behaviour tree does not.</b> Where things are is
///         <c>Vixen.Engine</c>'s and what is between them is <c>Vixen.Physics</c>'; a turn-based game,
///         a simulation or a test harness wants trees with neither. Folding this into
///         <c>Vixen.Ai</c> would put a physics solver behind every agent that has ever run a selector.
///     </para>
///     <para>
///         ⚠ The other half of the rule is in <c>Vixen.Ai.Tests</c>: this assembly may reference those
///         two, and <c>Vixen.Ai</c> may not.
///     </para>
/// </remarks>
public class PerceptionLayeringTests {
    static readonly string[] Forbidden = ["Vixen.Gameplay", "Vixen.Editor", "Vixen.Raven", "Vixen.Rendering"];

    [Fact]
    public void PerceptionReferencesNothingAboveIt() {
        var violations = References()
            .Where(name => Forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"Vixen.Ai.Perception references {string.Join(", ", violations)}. See docs/plan/37 § P3."
        );
    }

    [Fact]
    public void PerceptionReferencesOnlyTheNineItIsAllowed() {
        var allowed = new HashSet<string>(StringComparer.Ordinal) {
            "Vixen.Ai",
            "Vixen.Core",
            "Vixen.Core.Mathematics",
            "Vixen.Ecs",

            // The two Vixen.Ai refuses, and the reason this assembly exists.
            "Vixen.Engine",
            "Vixen.Physics",

            // AiStimuliSource is a scene component — an entity is made perceivable in the level
            // editor — so it needs the generated serializer and the descriptor that makes its type
            // alias resolvable when a scene loads it. Vixen.Ai took the same two on for `.vxbt`.
            "Vixen.Core.Serialization",
            "Vixen.Core.Reflection",

            // SystemBase.Update returns a JobHandle.
            "Vixen.Core.Threading"
        };

        var unexpected = References()
            .Where(name => name.StartsWith("Vixen.", StringComparison.Ordinal))
            .Where(name => !allowed.Contains(name))
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Vixen.Ai.Perception has grown a reference to {string.Join(", ", unexpected)}. "
            + "If that is right, say so here and in the .csproj."
        );
    }

    static IEnumerable<string> References() =>
        typeof(PerceptionSystem).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name ?? string.Empty);
}
