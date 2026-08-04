// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ai.Nodes.Tests;

/// <summary>Why the widest reference list in <c>Core/</c> is allowed to be here and nowhere else.</summary>
/// <remarks>
///     <para>
///         <b>These nodes are where a decision meets the engine</b>, so somebody has to depend on
///         navigation, animation and audio at once. What makes that safe is the other half:
///         <b>nothing depends on this</b>. A game links it if it wants the nodes and does not if it
///         does not, and a change here cannot reach anything.
///     </para>
///     <para>
///         ⚠ The positive list is the test. A reference that arrives with an unrelated feature is the
///         failure mode — this assembly is the one place in the AI stack where adding one is easy and
///         nobody would notice.
///     </para>
/// </remarks>
public class NodeLayeringTests {
    static readonly string[] Forbidden = ["Vixen.Gameplay", "Vixen.Editor", "Vixen.Raven"];

    [Fact]
    public void TheNodesReferenceNothingAboveThem() {
        var violations = References()
            .Where(name => Forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"Vixen.Ai.Nodes references {string.Join(", ", violations)}. See docs/plan/37 § P4."
        );
    }

    [Fact]
    public void TheNodesReferenceOnlyWhatTheirOwnNodesNeed() {
        var allowed = new HashSet<string>(StringComparer.Ordinal) {
            "Vixen.Ai",
            "Vixen.Core",
            "Vixen.Core.Mathematics",
            "Vixen.Ecs",

            // MoveTo, Patrol, RotateToward and the focus.
            "Vixen.Engine",
            "Vixen.Navigation",

            // PlayAnimation and PlaySound.
            "Vixen.Animation",
            "Vixen.Audio",

            // P8's query tests: "can I see the target from here" and "is there anything beside me"
            // are both a physics query, and they are the two most useful tests an environment query
            // has. A game that wants queries with no solver still gets the generators, the distance
            // test and the dot test out of Vixen.Ai.
            "Vixen.Physics",

            // Transitive and unavoidable: AnimationClip is built from Vixen.Rendering's clip data,
            // the components carry [DataContract], and SystemBase returns a JobHandle.
            "Vixen.Rendering",
            "Vixen.Core.Serialization",
            "Vixen.Core.Reflection",
            "Vixen.Core.Threading",
            "Vixen.Core.Collections"
        };

        var unexpected = References()
            .Where(name => name.StartsWith("Vixen.", StringComparison.Ordinal))
            .Where(name => !allowed.Contains(name))
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Vixen.Ai.Nodes has grown a reference to {string.Join(", ", unexpected)}. "
            + "If that is right, say so here and in the .csproj."
        );
    }

    /// <summary>
    ///     ⚠ And the half that matters more: nothing in the engine depends on this. If anything ever
    ///     does, the reference list above stops being contained and becomes everybody's.
    /// </summary>
    [Fact]
    public void NothingLoadedAlongsideTheseNodesDependsOnThem() {
        var dependents = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name?.StartsWith("Vixen.", StringComparison.Ordinal) == true)
            .Where(assembly => assembly.GetName().Name != "Vixen.Ai.Nodes")
            .Where(assembly => assembly.GetName().Name != "Vixen.Ai.Nodes.Tests")
            .Where(
                assembly => assembly.GetReferencedAssemblies()
                    .Any(reference => reference.Name == "Vixen.Ai.Nodes")
            )
            .Select(assembly => assembly.GetName().Name)
            .ToList();

        Assert.True(dependents.Count == 0, $"{string.Join(", ", dependents)} depends on Vixen.Ai.Nodes.");
    }

    static IEnumerable<string> References() =>
        typeof(WorldNodes).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name ?? string.Empty);
}

public class WorldNodeRegistrationTests {
    [Fact]
    public void EverySchemaEntryHasAFactoryWhenTheThingsItNeedsExist() {
        var level = new Level();
        var resolver = new BehaviorTreeResolver { Schema = new BehaviorNodeSchema() };
        var sounds = new Dictionary<string, Audio.AudioClip> { ["step"] = Rig.Clip(0.2f) };

        WorldNodes.Register(resolver, level.Query, sounds);

        foreach (var type in WorldNodes.Describe()) {
            Assert.True(resolver.Schema.TryGet(type.Type, out _), $"{type.Type} is not in the schema.");
            Assert.True(Builds(resolver, type), $"{type.Type} is in the schema with no factory behind it.");
        }
    }

    /// <summary>
    ///     ⚠ Without a navmesh there is no <c>DoesPathExist</c>, and that is a diagnostic on the node
    ///     rather than a crash — a tree authored before the level was baked still opens.
    /// </summary>
    [Fact]
    public void WithoutANavmeshThePathDecoratorIsReportedRatherThanBuilt() {
        var content = new BehaviorTreeContent {
            Name = "unbaked",
            Root = new() {
                Name = "Root",
                Type = "Selector",
                Children = { new() { Name = "Go", Type = "Wait", Fields = { ["Seconds"] = "1" } } }
            },
            Keys = { new() { Name = "target", Type = BlackboardValueType.Vector3 } }
        };

        content.Root!.Children[0].Decorators.Add(
            new() { Type = "DoesPathExist", Fields = { ["Key"] = "target" } }
        );

        var resolver = new BehaviorTreeResolver { Schema = new BehaviorNodeSchema() };

        WorldNodes.Register(resolver);
        BehaviorTreeContentCompiler.TryCompile(content, resolver, out var diagnostics, out _);

        Assert.Contains(diagnostics, problem => problem.Message.Contains("no factory registered", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisteringTwiceIsHarmless() {
        var schema = WorldNodes.Register(new BehaviorNodeSchema());
        var before = schema.Types.Count;

        WorldNodes.Register(schema);

        Assert.Equal(before, schema.Types.Count);
    }

    /// <summary>Builds one node from its own declared defaults, which is what an empty file gives it.</summary>
    static bool Builds(BehaviorTreeResolver resolver, BehaviorNodeType type) {
        var content = new BehaviorTreeContent {
            Name = "probe",
            Root = new() { Name = "Root", Type = "Selector" },
            Keys = { new() { Name = "target", Type = BlackboardValueType.Vector3 } }
        };

        var node = type.Slot == BehaviorSlot.Task
            ? new BehaviorNodeContent { Name = type.Type, Type = type.Type }
            : new BehaviorNodeContent { Name = "Host", Type = "Wait", Fields = { ["Seconds"] = "1" } };

        if (type.Slot == BehaviorSlot.Decorator) {
            node.Decorators.Add(Row(type));
        }

        if (type.Slot == BehaviorSlot.Service) {
            content.Root!.Services.Add(Row(type));
        }

        foreach (var field in type.Fields) {
            if (field.Kind == BehaviorFieldKind.Key) {
                node.Fields[field.Name] = "target";
            }
        }

        content.Root!.Children.Add(node);

        BehaviorTreeContentCompiler.TryCompile(content, resolver, out var diagnostics, out _);

        return diagnostics.All(problem => !problem.Message.Contains("no factory registered", StringComparison.Ordinal));
    }

    static BehaviorAttachmentContent Row(BehaviorNodeType type) {
        var row = new BehaviorAttachmentContent { Type = type.Type };

        foreach (var field in type.Fields) {
            if (field.Kind == BehaviorFieldKind.Key) {
                row.Fields[field.Name] = "target";
            }
        }

        return row;
    }
}
