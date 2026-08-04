// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Ecs;
using Vixen.Ai.Perception.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Ai.Diagnostics.Tests;

/// <summary>
///     P7's second exit criterion: the overlay is asserted by a test with no window.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>There is no device anywhere in this file, and that is the criterion rather than a
///         convenience.</b> Everything the debugger produces lands in a <see cref="DebugDraw" />'s
///         three lists — world lines, world labels, screen lines — so a test reads the geometry
///         directly. <c>ConstraintGizmos</c> established the arrangement and doc 37 § D20 adopts it,
///         because an overlay that could only be checked by looking at it is one that quietly stops
///         being checked.
///     </para>
/// </remarks>
public class AiOverlayExitCriteriaTests {
    [Fact]
    public void TheOverlayDrawsAnAgentWithNoDeviceAnywhereInSight() {
        using var fixture = new OverlayFixture();

        fixture.Step();

        var draw = new DebugDraw();
        var debugger = new AiGameplayDebugger { Style = AiOverlayStyle.Everything };

        debugger.Draw(draw, fixture.System, fixture.World);

        Assert.Equal(1, debugger.DrawnAgents);
        Assert.True(draw.Count > 0, "the overlay drew no geometry.");
        Assert.True(draw.TextCount > 0, "the overlay drew no labels.");

        // The readout says which planner, which asset and what it is doing, because those are the
        // three things somebody needs before they know whether to keep looking.
        var readout = string.Join('\n', Labels(draw));

        Assert.Contains("Utility", readout, StringComparison.Ordinal);
        Assert.Contains("villager", readout, StringComparison.Ordinal);
        Assert.Contains("run", readout, StringComparison.Ordinal);
    }

    /// <summary>⚠ Off must be free: not one line, not one label, and no capture at all.</summary>
    [Fact]
    public void EveryCategoryOffDrawsNothingWhatever() {
        using var fixture = new OverlayFixture();

        fixture.Step();

        var draw = new DebugDraw();
        var debugger = new AiGameplayDebugger { Style = new() { Categories = AiDebugCategory.None } };

        debugger.Draw(draw, fixture.System, fixture.World);

        Assert.Equal(0, debugger.DrawnAgents);
        Assert.Equal(0, draw.Count);
        Assert.Equal(0, draw.TextCount);
    }

    [Fact]
    public void OneCategoryAtATimeDrawsOneCategoryAtATime() {
        using var fixture = new OverlayFixture();

        fixture.Step();

        var agentOnly = Rows(fixture, AiDebugCategory.Agent);
        var withData = Rows(fixture, AiDebugCategory.Agent | AiDebugCategory.Data);

        Assert.True(withData > agentOnly, $"turning the blackboard on added nothing ({agentOnly} then {withData}).");
    }

    /// <summary>
    ///     ⚠ Range and count bite before anything is formatted, or an overlay in a crowd is a screen
    ///     of overlapping text and a capture per agent per frame.
    /// </summary>
    [Fact]
    public void AgentsOutOfRangeAreNotEvenPhotographed() {
        using var fixture = new OverlayFixture();

        fixture.Add(new(0f, 0f, 500f));
        fixture.Step();

        var draw = new DebugDraw();
        var debugger = new AiGameplayDebugger { Style = new() { Categories = AiDebugCategory.All, Range = 40f } };

        debugger.Draw(draw, fixture.System, fixture.World);

        Assert.Equal(1, debugger.DrawnAgents);

        debugger.Style = AiOverlayStyle.Everything;
        debugger.Draw(draw, fixture.System, fixture.World);

        Assert.Equal(2, debugger.DrawnAgents);
    }

    [Fact]
    public void TheCapKeepsTheNearestAgentsRatherThanWhicheverTheQueryWalkedFirst() {
        using var fixture = new OverlayFixture();

        fixture.Add(new(0f, 0f, 30f));
        fixture.Add(new(0f, 0f, 10f));
        fixture.Step();

        var draw = new DebugDraw();
        var debugger = new AiGameplayDebugger {
            Style = new() { Categories = AiDebugCategory.Agent, Range = 0f, MaximumAgents = 2 }
        };

        debugger.Draw(draw, fixture.System, fixture.World);

        Assert.Equal(2, debugger.DrawnAgents);

        // The two nearest are at the origin and at ten metres; the one at thirty is dropped, so no
        // line reaches out that far.
        foreach (var line in draw.Lines) {
            Assert.True(line.From.Z < 25f, $"a line was drawn at z = {line.From.Z}.");
        }
    }

    /// <summary>
    ///     ⚠ The selected agent is drawn whatever the range says. "Why is that one doing that" must
    ///     not be answered by its label being culled.
    /// </summary>
    [Fact]
    public void TheSelectedAgentIsDrawnHoweverFarAwayItIs() {
        using var fixture = new OverlayFixture();

        var distant = fixture.Add(new(0f, 0f, 900f));

        fixture.Step();

        var draw = new DebugDraw();
        var debugger = new AiGameplayDebugger {
            Style = new() { Categories = AiDebugCategory.Agent, Range = 5f, MaximumAgents = 1 },
            Selected = distant
        };

        debugger.Draw(draw, fixture.System, fixture.World);

        Assert.Equal(1, debugger.DrawnAgents);
        Assert.Contains(draw.Texts.ToArray(), text => text.Position.Z > 800f);
    }

    /// <summary>
    ///     ⚠ Both sight radii are drawn, because the gap between them is the whole of why a guard
    ///     keeps following somebody it should have lost — and it is invisible in every debugger that
    ///     draws one circle.
    /// </summary>
    [Fact]
    public void ASensingAgentGetsItsConeAndBothOfItsSightRadii()  {
        using var fixture = new OverlayFixture();

        fixture.Sense();
        fixture.Step();

        var draw = new DebugDraw();
        var debugger = new AiGameplayDebugger {
            Style = new() { Categories = AiDebugCategory.Shapes, Range = 0f },
            Perception = fixture.Perception
        };

        debugger.Draw(draw, fixture.System, fixture.World);

        var reach = 0f;

        foreach (var line in draw.Lines) {
            reach = MathF.Max(reach, MathF.Max(line.From.Length(), line.To.Length()));
        }

        // The lose-sight radius is 25 where the acquire radius is 20, so the far circle can only be
        // the one a single-circle debugger would not have drawn.
        Assert.True(reach > 24f, $"nothing reached the lose-sight radius; the furthest line was {reach:0.#} m.");
    }

    [Fact]
    public void ADebuggerThatIsOffCostsOneBranch() {
        using var fixture = new OverlayFixture();

        fixture.Step();

        var draw = new DebugDraw();
        var debugger = new AiGameplayDebugger { Enabled = false, Style = AiOverlayStyle.Everything };

        debugger.Draw(draw, fixture.System, fixture.World);

        Assert.Equal(0, draw.Count);
        Assert.Equal(0, debugger.DrawnAgents);
    }

    /// <summary>
    ///     ⚠ <c>default</c> is the quiet style and <c>new()</c> is the usual one — the trap
    ///     <c>ConstraintGizmoStyle</c> paid for, asserted rather than remembered.
    /// </summary>
    [Fact]
    public void AZeroedStyleIsOffAndTheUsualOneIsNot() {
        Assert.Equal(AiDebugCategory.None, default(AiOverlayStyle).Categories);
        Assert.Equal(AiDebugCategory.Default, AiOverlayStyle.Default.Categories);
        Assert.Equal(AiOverlayStyle.DefaultTextSize, default(AiOverlayStyle).Text);
    }

    static int Rows(OverlayFixture fixture, AiDebugCategory categories) {
        var draw = new DebugDraw();
        var debugger = new AiGameplayDebugger { Style = new() { Categories = categories, Range = 0f } };

        debugger.Draw(draw, fixture.System, fixture.World);

        return debugger.DrawnRows;
    }

    static IEnumerable<string> Labels(DebugDraw draw) {
        foreach (var text in draw.Texts.ToArray()) {
            yield return text.Text;
        }
    }
}

/// <summary>A world with utility agents in it, a system stepping them, and nothing that draws.</summary>
sealed class OverlayFixture : IDisposable {
    readonly AgentActionRegistry registry = new();

    public OverlayFixture() {
        registry.Register("wander", new Idle(), 0);
        registry.Register("run", new Idle(), 0);

        System = new(registry, Layout);
        System.Sets.Add(
            new UtilitySet(Symbol.Intern("villager"), Candidate("wander", 0, 0.2f), Candidate("run", 1, 0.9f))
        );

        World = new("overlay");
        First = Add(Vector3.Zero);
    }

    public static BlackboardLayout Layout { get; } =
        new BlackboardLayoutBuilder().Add("alarm", BlackboardValueType.Float).Build();

    public AiSystem System { get; }

    public World World { get; }

    public PerceptionSystem Perception { get; } = new();

    public Entity First { get; }

    /// <summary>Adds an agent at a position.</summary>
    public Entity Add(Vector3 at) =>
        World.Create(AiAgent.Scoring(0), LocalTransform.At(at));

    /// <summary>Gives the first agent senses, and something to sense.</summary>
    public void Sense() {
        Perception.Configs.Add(new() { Name = Symbol.Intern("guard") });
        World.Add(First, AiPerception.Sensing(0));
        System.Step(World, GameTime.Zero);
        Perception.Step(World, GameTime.Zero);
    }

    /// <summary>Steps the agents, and writes a key so the blackboard has something in it.</summary>
    public void Step() {
        System.Step(World, GameTime.Zero);

        System.BlackboardOf(in World.Read<AiAgent>(First))?.SetFloat(Layout.Key("alarm"), 0.5f);
        System.Step(World, GameTime.Zero);
    }

    public void Dispose() => World.Dispose();

    static UtilityAction Candidate(string name, ushort action, float score) =>
        new(
            Symbol.Intern(name),
            action,
            new UtilityConsideration(
                Symbol.Intern("axis"),
                UtilityInputs.From((in AgentContext context) => score),
                ResponseCurve.Identity
            )
        );

    sealed class Idle : IAgentAction {
        public void Start(in AgentContext context, Span<byte> state) { }

        public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) => ActionStatus.Running;

        public void Abort(in AgentContext context, Span<byte> state) { }
    }
}
