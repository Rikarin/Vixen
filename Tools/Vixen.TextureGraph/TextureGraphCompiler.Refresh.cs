// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.NodeGraph;

namespace Vixen.TextureGraph;

public sealed partial class TextureGraphCompiler {
    /// <summary>What the last compilation emitted, node by node.</summary>
    internal TextureEmission Emission { get; } = new();

    /// <summary>The graph the compilation in progress started from.</summary>
    NodeGraphModel? compiling;

    /// <summary>The last compilation, when a value edit can still be answered from it.</summary>
    Compiled? compiled;

    /// <summary>How many graph changes <see cref="Refresh" /> has answered without a compile.</summary>
    public int Refreshes { get; private set; }

    /// <summary>
    ///     The plan for a graph that differs from the one last compiled only in its numbers, without
    ///     compiling it.
    /// </summary>
    /// <param name="graph">The graph, which has to be the object last given to <c>Compile</c>.</param>
    /// <returns>
    ///     The plan, sharing the last one's images and wiring; or <see langword="null" />, meaning
    ///     compile. <see cref="NodeImages" />, <see cref="Outputs" /> and <see cref="Externals" />
    ///     still describe it, because a plan this returns has the same images.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>A value edit never rebuilds the plan, and nothing was annotated to make that true.</b>
    ///         What a port's number becomes on its way to a uniform is written in a node's own
    ///         <c>Compile</c>, as arbitrary C#: <c>Levels</c> passes it through, <c>Blur</c> scales it,
    ///         <c>Distance</c> decides how many ops to emit from it. A table of "this port feeds that
    ///         uniform" would be a second statement of each of those. So the node is simply asked
    ///         again, with every <see cref="TextureEmitter" /> call that touches the plan answered
    ///         from what it did last time, and the ops it dispatches are compared with the ones it
    ///         dispatched last time.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which also says what "truly changes a variant" means, and no list does.</b> If the
    ///         new number makes the node ask for a different kernel, a different image, a different
    ///         op count or a diagnostic, the replay diverges and this returns null. A number that
    ///         only reaches a uniform comes back as the same ops with other parameters, and those are
    ///         patched into a copy of the plan.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null is always a correct answer, and it is deliberately the answer to anything
    ///         that is not a number on a port</b>: a wire, a node added or removed, a setting's text
    ///         (a blend mode is a setting, an expression is a text), a base resolution, an exposed
    ///         parameter — and <b>any graph that inlined a sub-graph</b>. That last one is the
    ///         expensive refusal: a compound's contents are an object this cannot watch, so a compile
    ///         is what reads them. The graph's seed is the one setting this does answer, because
    ///         nothing a node emits can depend on it: it is read in <c>SeedFor</c>, at evaluation.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Compilation state is what makes this short, so it has to be this compiler's own
    ///         last compilation.</b> An image index a node was handed last time is only meaningful in
    ///         that plan, so a compiler that has since compiled another graph, or changed a property,
    ///         answers null. The graph may have been edited any number of times in between.
    ///     </para>
    /// </remarks>
    public TexturePlan? Refresh(NodeGraphModel graph) {
        ArgumentNullException.ThrowIfNull(graph);

        if (compiled is not { } last || !last.Matches(this, graph)) {
            return null;
        }

        if (SubGraphSource is { } source && SubGraphs.ContainsSubGraph(graph, source)) {
            return null;
        }

        var seed = TextureGraphSettings.SeedOf(graph, Seed, out var seedProblem);

        if (seedProblem is not null || !last.Differences(graph, out var changed)) {
            return null;
        }

        Dictionary<int, TextureOp> patched = [];

        foreach (var node in changed) {
            // The live model: with nothing inlined, the graph the walk was over is this one.
            if (!Registry.TryGet(node.Type, out var definition) || !Replay(node, definition, patched)) {
                return null;
            }
        }

        var plan = last.Plan;

        if (patched.Count > 0 || seed != plan.Seed) {
            var next = plan.Ops.ToBuilder();

            foreach (var (index, op) in patched) {
                next[index] = op;
            }

            plan = plan.Reissued(next.ToImmutable(), seed);
        }

        compiled = last with { Plan = plan, Nodes = NodeSnapshot.Of(graph) };
        Refreshes++;

        return plan;
    }

    /// <summary>Asks one node again, against the record of what it did.</summary>
    /// <returns>False when it did something else, in which case nothing has changed.</returns>
    bool Replay(GraphNode node, NodeTypeDefinition definition, Dictionary<int, TextureOp> patched) {
        if (!Emission.Begin(node.Id, ops, patched)) {
            return false;
        }

        try {
            Walk(graph!, node, definition);

            return true;
        } catch (TextureReplayDiverged) {
            return false;
        } finally {
            Emission.End();
        }
    }

    /// <summary>Records a finished compilation, if a value edit could be answered from it.</summary>
    void Remember(TexturePlan plan) {
        if (compiling is { } author && Inlining.IsEmpty) {
            compiled = Compiled.Of(this, author, plan);
        }
    }

    void Forget() {
        compiled = null;
        Emission.Clear();
    }

    /// <summary>One node's part of a graph, as it stood.</summary>
    readonly record struct NodeSnapshot(
        string Type,
        IReadOnlyDictionary<string, string> Texts,
        IReadOnlyDictionary<string, float[]> Values
    ) {
        public static Dictionary<NodeId, NodeSnapshot> Of(NodeGraphModel graph) {
            Dictionary<NodeId, NodeSnapshot> nodes = [];

            foreach (var node in graph.Nodes) {
                nodes[node.Id] = new(
                    node.Type,
                    new Dictionary<string, string>(node.Texts, StringComparer.Ordinal),
                    node.Values.ToDictionary(
                        value => value.Key,
                        value => (float[])value.Value.Clone(),
                        StringComparer.Ordinal
                    )
                );
            }

            return nodes;
        }

        public bool SameShape(GraphNode node) =>
            string.Equals(Type, node.Type, StringComparison.Ordinal)
            && Texts.Count == node.Texts.Count
            && Texts.All(
                text => node.Texts.TryGetValue(text.Key, out var other)
                    && string.Equals(text.Value, other, StringComparison.Ordinal)
            );

        public bool SameValues(GraphNode node) =>
            Values.Count == node.Values.Count
            && Values.All(
                value => node.Values.TryGetValue(value.Key, out var other) && value.Value.SequenceEqual(other)
            );
    }

    /// <summary>What a compilation read, so that "the same but for the numbers" can be asked of it.</summary>
    sealed record Compiled(
        NodeGraphModel Graph,
        TexturePlan Plan,
        Dictionary<NodeId, NodeSnapshot> Nodes,
        ImmutableArray<GraphEdge> Edges,
        Dictionary<string, string> Settings,
        ImmutableArray<SettingDefinition> GraphParameters,
        ImmutableArray<TextureGraphParameter> HostParameters,
        (int Width, int Height, int Bake, bool Previews) Host,
        Dictionary<string, string>? Arguments
    ) {
        public static Compiled Of(TextureGraphCompiler compiler, NodeGraphModel graph, TexturePlan plan) =>
            new(
                graph,
                plan,
                NodeSnapshot.Of(graph),
                [.. graph.Edges],
                new(graph.Settings, StringComparer.Ordinal),
                [.. graph.Parameters],
                [.. compiler.Parameters],
                (compiler.BaseWidth, compiler.BaseHeight, compiler.BakeLevelOffset, compiler.PreviewEveryNode),
                compiler.Arguments is null
                    ? null
                    : new Dictionary<string, string>(compiler.Arguments, StringComparer.Ordinal)
            );

        /// <summary>Whether this is that compiler's own compilation of this graph, as it was configured.</summary>
        public bool Matches(TextureGraphCompiler compiler, NodeGraphModel graph) =>
            ReferenceEquals(graph, Graph)
            && Host == (compiler.BaseWidth, compiler.BaseHeight, compiler.BakeLevelOffset, compiler.PreviewEveryNode)
            && compiler.Parameters.SequenceEqual(HostParameters)
            && SameArguments(compiler.Arguments);

        /// <summary>The nodes whose numbers moved.</summary>
        /// <returns>False when anything other than a number moved.</returns>
        public bool Differences(NodeGraphModel graph, out List<GraphNode> changed) {
            changed = [];

            if (graph.Nodes.Count != Nodes.Count
                || !graph.Edges.SequenceEqual(Edges)
                || !graph.Parameters.SequenceEqual(GraphParameters)
                || !SameSettings(graph)) {
                return false;
            }

            foreach (var node in graph.Nodes) {
                if (!Nodes.TryGetValue(node.Id, out var before) || !before.SameShape(node)) {
                    return false;
                }

                if (!before.SameValues(node)) {
                    changed.Add(node);
                }
            }

            return true;
        }

        bool SameArguments(IReadOnlyDictionary<string, string>? arguments) =>
            arguments is null
                ? Arguments is null
                : Arguments is not null
                  && Arguments.Count == arguments.Count
                  && Arguments.All(
                      argument => arguments.TryGetValue(argument.Key, out var other)
                          && string.Equals(argument.Value, other, StringComparison.Ordinal)
                  );

        /// <summary>Every setting but the seed, which <see cref="Refresh" /> answers itself.</summary>
        bool SameSettings(NodeGraphModel graph) {
            bool Others(IReadOnlyDictionary<string, string> from, IReadOnlyDictionary<string, string> to) =>
                from.All(
                    setting => string.Equals(setting.Key, TextureGraphSettings.Seed, StringComparison.Ordinal)
                        || (to.TryGetValue(setting.Key, out var other)
                            && string.Equals(setting.Value, other, StringComparison.Ordinal))
                );

            return Others(Settings, graph.Settings) && Others(graph.Settings, Settings);
        }
    }
}

/// <summary>A replay reached something the record does not say, so the node did something else.</summary>
sealed class TextureReplayDiverged : InvalidOperationException {
    public TextureReplayDiverged() : base("The node emitted something other than it did last time.") { }
}

/// <summary>
///     What each node did to the plan while it compiled, in order, so that it can be asked again.
/// </summary>
/// <remarks>
///     <para>
///         <b>A log of calls and their answers.</b> Recording, a call goes through to the compiler and
///         its answer is written down. Replaying, the compiler is not touched: the next entry has to be
///         the same call with the same arguments, and its recorded answer is returned. So a node
///         re-running sees exactly the image indices it saw, allocates nothing, and can only differ in
///         the numbers it computes — or diverge, which aborts the replay.
///     </para>
///     <para>
///         ⚠ <b>A dispatch is compared with the op in the plan rather than logged by value</b>, because
///         that is the thing that matters: the same kernel over the same images with the same parameter
///         names and units. A difference in anything but the numbers is a different op.
///     </para>
/// </remarks>
sealed class TextureEmission {
    /// <summary>The emitter calls that touch the plan.</summary>
    public enum Kind {
        Enter,
        Read,
        ReadGrey,
        Write,
        Scratch,
        External,
        Dispatch,
        Declare,
        Keep,
        Report
    }

    readonly Dictionary<NodeId, List<Entry>> logs = [];

    List<Entry>? recording;
    List<Entry>? replaying;
    int cursor;
    ImmutableArray<TextureOp>.Builder? plan;
    Dictionary<int, TextureOp>? patched;

    /// <summary>Whether a missing image means this op is not going to exist.</summary>
    public static bool Dropped(TextureOp op) => op.Output < 0 || op.Inputs.Any(input => input < 0);

    /// <summary>Two numbers as one, for a log entry's arguments.</summary>
    public static long Detail(int first, int second) => ((long)first << 32) | (uint)second;

    public void Clear() => logs.Clear();

    /// <summary>Starts replaying a node, if it was recorded at all.</summary>
    public bool Begin(NodeId node, ImmutableArray<TextureOp>.Builder ops, Dictionary<int, TextureOp> into) {
        if (!logs.TryGetValue(node, out var log)) {
            return false;
        }

        replaying = log;
        cursor = 0;
        plan = ops;
        patched = into;

        return true;
    }

    public void End() {
        replaying = null;
        plan = null;
        patched = null;
    }

    /// <summary>The start of one node, which either opens a log or has to match one.</summary>
    public void Enter(NodeId node, TextureChannels channels, int level) {
        if (replaying is null) {
            recording = [];
            logs[node] = recording;
        }

        Call(Kind.Enter, "", Detail((int)channels, level), static () => 0);
    }

    /// <summary>The end of one node: a replay that stopped short did less than the record says.</summary>
    public void Leave() {
        if (replaying is not null && cursor != replaying.Count) {
            throw new TextureReplayDiverged();
        }

        recording = null;
    }

    /// <summary>One call: through to the compiler, or answered from the log.</summary>
    public int Call(Kind kind, string text, long detail, Func<int> real) {
        if (replaying is null) {
            var answer = real();

            recording?.Add(new(kind, text, detail, answer));

            return answer;
        }

        var entry = Next();

        if (entry.What != kind || entry.Detail != detail || !string.Equals(entry.Text, text, StringComparison.Ordinal)) {
            throw new TextureReplayDiverged();
        }

        return entry.Answer;
    }

    /// <summary>One dispatch: added to the plan, or compared with the op already there.</summary>
    public void Dispatch(TextureOp op, Func<int> real) {
        if (replaying is null) {
            recording?.Add(new(Kind.Dispatch, "", 0, real()));

            return;
        }

        var entry = Next();

        if (entry.What != Kind.Dispatch) {
            throw new TextureReplayDiverged();
        }

        if (entry.Answer < 0) {
            if (!Dropped(op)) {
                throw new TextureReplayDiverged();
            }

            return;
        }

        var before = plan![entry.Answer];

        if (Dropped(op) || !SameOp(before, op)) {
            throw new TextureReplayDiverged();
        }

        patched![entry.Answer] = before with { Parameters = op.Parameters, Cpu = op.Cpu };
    }

    Entry Next() =>
        cursor < replaying!.Count ? replaying[cursor++] : throw new TextureReplayDiverged();

    /// <summary>Everything about an op but its numbers.</summary>
    /// <remarks>
    ///     ⚠ Spelled out rather than <c>with</c>-and-compare, because a record's generated equality
    ///     compares an <see cref="ImmutableArray{T}" /> by the array it wraps and two equal lists
    ///     built apart would read as different. <see cref="TextureOp.Identity" /> is left out: the
    ///     compiler stamps it after the node has finished, so the op a node hands over never has one.
    /// </remarks>
    static bool SameOp(TextureOp a, TextureOp b) =>
        string.Equals(a.Kernel, b.Kernel, StringComparison.Ordinal)
        && a.Cpu?.GetType() == b.Cpu?.GetType()
        && a.Output == b.Output
        && a.EmittedForExtent == b.EmittedForExtent
        && a.ReadsOtherExtents == b.ReadsOtherExtents
        && a.DependsOnEveryTexel == b.DependsOnEveryTexel
        && a.Inputs.SequenceEqual(b.Inputs)
        && a.OtherExtentInputs.SequenceEqual(b.OtherExtentInputs)
        && a.Parameters.Length == b.Parameters.Length
        && a.Parameters.Zip(b.Parameters).All(
            pair => string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
                && pair.First.Unit == pair.Second.Unit
        );

    readonly record struct Entry(Kind What, string Text, long Detail, int Answer);
}
