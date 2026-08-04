// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Ecs;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>One running agent, and the file that describes what it is running.</summary>
/// <remarks>
///     ⚠ <b>The document and the agent are built from the same text</b>, because a live panel is only
///     honest if the picture on the canvas and the tree in the world are the same tree. A fixture that
///     built one in code and one in YAML would pass while the two drifted.
/// </remarks>
sealed class LiveAgent : IDisposable {
    readonly AgentActionRegistry registry = new();

    public LiveAgent(bool utility = false) {
        World = new("live-editor");
        Layout = new BlackboardLayoutBuilder()
            .Add("alarmed", BlackboardValueType.Bool)
            .Add("danger", BlackboardValueType.Float)
            .Build();

        if (utility) {
            System = new(registry, Layout) { Governor = new UnboundedGovernor() };
            System.Debug.Enabled = true;

            var wander = registry.Register("wander", new WaitTask(2f), WaitTask.StateSize);
            var flee = registry.Register("flee", new WaitTask(2f), WaitTask.StateSize);

            System.Sets.Add(
                new UtilitySet(
                    Symbol.Intern("Mood"),
                    Candidate("Wander", wander, "danger", falling: true),
                    Candidate("Flee", flee, "danger", falling: false)
                )
            );

            Agent = World.Create(AiAgent.Scoring(0));
            System.BlackboardOf(in World.Read<AiAgent>(Agent));

            return;
        }

        var resolver = new BehaviorTreeResolver { Schema = new BehaviorNodeSchema() };

        if (!BehaviorTreeContentCompiler.TryCompile(Content(), resolver, out _, out var template)) {
            throw new InvalidOperationException("the fixture's own tree does not compile.");
        }

        System = new(resolver.Actions, Layout) { Governor = new UnboundedGovernor() };
        System.Debug.Enabled = true;
        System.Trees.Add(template!);

        Agent = World.Create(AiAgent.Thinking(0));
    }

    public World World { get; }

    public BlackboardLayout Layout { get; }

    public AiSystem System { get; }

    public Entity Agent { get; }

    /// <summary>The tree as a file, so the editor opens the same thing the agent is running.</summary>
    public static string Yaml => YamlSerializer.ToYaml(Content());

    /// <summary>The set as a file, ditto.</summary>
    public static string SetYaml => YamlSerializer.ToYaml(SetContent());

    public void Steps(int frames) {
        for (var frame = 0; frame < frames; frame++) {
            var board = System.BlackboardOf(in World.Read<AiAgent>(Agent));

            // The alarm goes off half-way, so the first child fails for a while and then passes —
            // which is what gives the canvas both a live path and a last result to draw.
            board?.SetBool(Layout.Key("alarmed"), frame > 8);
            board?.SetFloat(Layout.Key("danger"), 0.9f);

            System.Step(
                World,
                new(TimeSpan.FromSeconds(frame * 0.1), TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.1), frame, 1f)
            );
        }
    }

    public void Dispose() => World.Dispose();

    /// <summary>What a pantry holds, for the orchard the GOAP viewer's tests draw.</summary>
    internal sealed class Pantry {
        public int OnGround;
        public int Carried;
        public int Hunger;
    }

    /// <summary>The orchard: pick a pear up, then eat it.</summary>
    internal static GoapDomain Domain(Pantry pantry) {
        ArgumentNullException.ThrowIfNull(pantry);

        var keys = new GoapWorldKeys(
            new(Symbol.Intern("pears-on-ground"), GoapWorldSources.From((in AgentContext _) => pantry.OnGround)),
            new(Symbol.Intern("pears-carried"), GoapWorldSources.From((in AgentContext _) => pantry.Carried)),
            new(Symbol.Intern("hunger"), GoapWorldSources.From((in AgentContext _) => pantry.Hunger))
        );

        var ground = new GoapWorldKey(0);
        var carried = new GoapWorldKey(1);
        var hunger = new GoapWorldKey(2);

        return new(
            Symbol.Intern("orchard"),
            keys,
            [
                new GoapAction(
                    Symbol.Intern("pick-up-pear"),
                    0,
                    [new(ground, GoapComparison.Greater, 0)],
                    new GoapEffect(carried, true)
                ),
                new GoapAction(
                    Symbol.Intern("eat-pear"),
                    1,
                    [new(carried, GoapComparison.Greater, 0)],
                    new GoapEffect(hunger, false)
                )
            ],
            [new GoapGoal(Symbol.Intern("not-hungry"), [new(hunger, GoapComparison.Less, 20)])]
        );
    }

    /// <summary>A selector whose first child is gated and whose second always runs.</summary>
    static BehaviorTreeContent Content() {
        var root = new BehaviorNodeContent { Name = "brain", Type = "Selector" };
        var alarm = new BehaviorNodeContent { Name = "alarm", Type = "Wait", Fields = { ["Seconds"] = "5" } };

        alarm.Decorators.Add(
            new() {
                Type = "Blackboard",
                Fields = {
                    ["Key"] = "alarmed",
                    ["Test"] = nameof(BlackboardTest.Equal),
                    ["Value"] = "1",
                    ["Aborts"] = nameof(ObserverAborts.Both)
                }
            }
        );

        root.Children.Add(alarm);
        root.Children.Add(new() { Name = "idle", Type = "Wait", Fields = { ["Seconds"] = "5" } });

        return new() {
            Name = "guard",
            Root = root,
            Keys = {
                new() { Name = "alarmed", Type = BlackboardValueType.Bool },
                new() { Name = "danger", Type = BlackboardValueType.Float }
            }
        };
    }

    static UtilitySetContent SetContent() => new() {
        Name = "Mood",
        Keys = {
            new() { Name = "alarmed", Type = BlackboardValueType.Bool },
            new() { Name = "danger", Type = BlackboardValueType.Float }
        },
        Actions = {
            Authored("Wander", falling: true),
            Authored("Flee", falling: false)
        }
    };

    static UtilityActionContent Authored(string name, bool falling) => new() {
        Name = name,
        Task = "Wait",
        Fields = { ["Seconds"] = "2" },
        Considerations = {
            new() {
                Name = "danger",
                Input = UtilityInputKind.Blackboard,
                Key = "danger",
                Maximum = 1f,
                Curve = ResponseCurveKind.Linear,
                Slope = falling ? -1f : 1f,
                Shift = falling ? 1f : 0f
            }
        }
    };

    UtilityAction Candidate(string name, ushort action, string key, bool falling) =>
        new(
            Symbol.Intern(name),
            action,
            new UtilityConsideration(
                Symbol.Intern("danger"),
                new BlackboardUtilityInput(Layout.Key(key)),
                new ResponseCurve { Slope = falling ? -1f : 1f, Shift = falling ? 1f : 0f }
            )
        );
}
