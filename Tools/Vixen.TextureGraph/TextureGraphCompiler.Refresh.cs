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

    /// <summary>Whether a refresh is in progress, so that a diagnostic has nowhere to go.</summary>
    bool refreshing;

    /// <summary>Whether the refresh in progress had something to say, which only a compile can.</summary>
    bool spoke;

    /// <summary>How many graph changes <see cref="Refresh(NodeGraphModel)" /> has answered without a compile.</summary>
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
    ///         that is not a number</b>: a wire, a node added or removed, a setting's text (a blend
    ///         mode is a setting, an expression is a text), a base resolution, a compound's contents
    ///         changing shape. The graph's seed is the one setting this does answer, because nothing
    ///         a node emits can depend on it: it is read in <c>SeedFor</c>, at evaluation.
    ///     </para>
    ///     <para>
    ///         <b>A compound is inlined again, and that is the whole of what it costs.</b> A number
    ///         on a sub-graph node reaches the nodes inside it through
    ///         <see cref="SubGraphs.Flatten(NodeGraphModel,ISubGraphSource,out IReadOnlyList{NodeDiagnostic})" />,
    ///         and a table of which inner port a number lands on would be a second statement of that
    ///         walk. So the flattener runs, its answer is compared with the graph the compile walked,
    ///         and the nodes whose numbers moved are asked again. A compound's contents are read the
    ///         same way, so an edit inside one is answered like any other. Its ops are not touched:
    ///         flattening one graph twice numbers its nodes alike, which is what a replay's log is
    ///         keyed on, and comparing the two is what says so.
    ///     </para>
    ///     <para>
    ///         <b>An exposed parameter is a number too</b>, whether it is a text on a sub-graph node
    ///         or an entry in <see cref="Arguments" />. A knob reaches a node by an expression or by a
    ///         <c>$</c> reference and in no other way, so moving one folds that scope's expressions
    ///         again and asks the nodes holding either. A name knob is answered the same way: a
    ///         name that makes a node emit something else diverges like any other number.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Anything said during a refresh is a compile.</b> A refused override or an
    ///         expression that no longer folds is a diagnostic, and only a compilation has anywhere
    ///         to put one. This is also why a graph that compiled with a warning from an expression
    ///         compiles whenever that expression is folded again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Compilation state is what makes this short, so it has to be this compiler's own
    ///         last compilation.</b> An image index a node was handed last time is only meaningful in
    ///         that plan, so a compiler that has since compiled another graph, or changed a property,
    ///         answers null. The graph may have been edited any number of times in between.
    ///     </para>
    /// </remarks>
    public TexturePlan? Refresh(NodeGraphModel graph) => Refresh(graph, false);

    /// <summary>The same, for a graph a document builds afresh on every edit.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="rebuilt">
    ///     Whether it may be another object than the one compiled: one the same builder made from the
    ///     same document, so that its node ids mean what they meant.
    /// </param>
    /// <returns>As for <see cref="Refresh(NodeGraphModel)" />.</returns>
    /// <remarks>
    ///     ⚠ <b>A layer stack's graph is a new object on every edit, so the identity check made this
    ///     unreachable from one.</b> <c>LayerStackEvaluator.Prepare</c> said it compiled only for more
    ///     than a value and every value compiled. Nothing is lost by waiving the check: what is
    ///     compared afterwards is every node, edge, setting and parameter of the graph, and a builder
    ///     that numbered its nodes otherwise would differ in them and answer null.
    /// </remarks>
    public TexturePlan? Refresh(NodeGraphModel graph, bool rebuilt) {
        ArgumentNullException.ThrowIfNull(graph);

        if (compiled is not { } last || !last.Matches(this, graph, rebuilt)) {
            return null;
        }

        var seed = TextureGraphSettings.SeedOf(graph, Seed, out var seedProblem);

        if (seedProblem is not null) {
            return null;
        }

        var values = ParameterValues;
        var inlining = Inlining;
        var walked = this.graph;
        Dictionary<(NodeId Node, string Port), float> numbers = new(folded);

        refreshing = true;
        spoke = false;

        try {
            if (Reissue(last, graph, seed) is { } plan) {
                Refreshes++;

                return plan;
            }
        } finally {
            refreshing = false;
        }

        // ⚠ Put back rather than forgotten, because null says only "not this graph": the same
        // compilation still answers the moment the number that diverged is put back.
        ParameterValues = values;
        Inlining = inlining;
        this.graph = walked;
        folded.Clear();

        foreach (var (port, number) in numbers) {
            folded[port] = number;
        }

        return null;
    }

    /// <summary>What <see cref="Refresh(NodeGraphModel)" /> does, with nothing yet decided about keeping it.</summary>
    TexturePlan? Reissue(Compiled last, NodeGraphModel author, uint seed) {
        var everyScope = !last.SameArguments(Arguments);

        if (everyScope && !Reread(last)) {
            return null;
        }

        // The same graph a compile walks, bypassed nodes and reroutes taken out — see `Walkable`.
        var walked = Walkable(author, out var refused, out var inlining);

        if (refused.Count > 0 || spoke) {
            return null;
        }

        if (!last.Differences(walked, out var changed) || !Retuned(last.Inlining, inlining, everyScope, out var retuned)) {
            return null;
        }

        Inlining = inlining;
        graph = walked;

        if (retuned.Count > 0) {
            foreach (var (expansion, scope, expressions) in Collect(walked)) {
                if (retuned.Contains(expansion)) {
                    Fold(expansion, scope, expressions);
                }
            }

            if (spoke) {
                return null;
            }

            changed.AddRange(walked.Nodes.Where(node => !changed.Contains(node) && Reads(node, retuned)));
        }

        Dictionary<int, TextureOp> patched = [];

        foreach (var node in changed) {
            // The graph the walk was over, which is this one with nothing inlined.
            if (!Registry.TryGet(node.Type, out var definition) || !Replay(node, definition, patched)) {
                return null;
            }
        }

        // A node's setting is read during its replay, and a knob that no longer resolves says so there.
        if (spoke) {
            return null;
        }

        var plan = last.Plan;

        if (patched.Count > 0 || seed != plan.Seed) {
            var next = plan.Ops.ToBuilder();

            foreach (var (index, op) in patched) {
                next[index] = op;
            }

            plan = plan.Reissued(next.ToImmutable(), seed);
        }

        compiled = last with {
            Plan = plan,
            Nodes = NodeSnapshot.Of(walked),
            Inlining = inlining,
            Arguments = Copied(Arguments)
        };

        return plan;
    }

    /// <summary>Takes new arguments, which have to be numbers for knobs this graph declares.</summary>
    bool Reread(Compiled last) {
        foreach (var key in Moved(last.Arguments, Arguments)) {
            if (!declared.Exists(parameter => string.Equals(parameter.Name, key, StringComparison.Ordinal))) {
                return false;
            }
        }

        ParameterValues = TextureGraphParameters.Read(declared, Arguments, out var refused);

        return refused.IsEmpty;
    }

    /// <summary>Which scopes had a knob move, when nothing else about the inlining did.</summary>
    /// <param name="before">The inlining the compile walked.</param>
    /// <param name="after">The inlining now.</param>
    /// <param name="everyScope">Whether the author's own knobs moved, which every scope can read.</param>
    /// <param name="retuned">The expansions, with <c>0</c> for the author's own graph.</param>
    /// <returns>False when the two are not one inlining.</returns>
    /// <remarks>
    ///     ⚠ <b>A nested expansion moves with the one around it</b>, because a <c>$</c> reference is
    ///     followed outwards and what an inner setting names can be a knob of the outer scope.
    /// </remarks>
    bool Retuned(NodeGraphInlining before, NodeGraphInlining after, bool everyScope, out HashSet<int> retuned) {
        HashSet<int> moved = [];

        retuned = moved;

        if (before.Expansions.Count != after.Expansions.Count
            || before.Origins.Count != after.Origins.Count
            || before.Origins.Any(origin => !after.TryGet(origin.Key, out var other) || origin.Value != other)) {
            return false;
        }

        foreach (var (number, was) in before.Expansions) {
            if (!after.Expansions.TryGetValue(number, out var now)
                || !string.Equals(was.Type, now.Type, StringComparison.Ordinal)
                || was.Source != now.Source
                || !was.Path.SequenceEqual(now.Path)) {
                return false;
            }

            var keys = Moved(was.Settings, now.Settings).ToArray();

            if (keys.Length == 0) {
                continue;
            }

            var parameters = (SubGraphSource as ITextureGraphLibrary)?.ParametersOf(now.Type) ?? [];

            if (keys.Any(key => !parameters.Any(parameter => string.Equals(parameter.Name, key, StringComparison.Ordinal)))) {
                return false;
            }

            moved.Add(number);
        }

        if (everyScope) {
            moved.Add(0);
            moved.UnionWith(after.Expansions.Keys);

            return true;
        }

        var outer = after.Expansions
            .Where(expansion => moved.Contains(expansion.Key))
            .Select(expansion => expansion.Value.Path)
            .ToArray();

        foreach (var (number, expansion) in after.Expansions) {
            if (outer.Any(path => expansion.Path.Length > path.Length && expansion.Path.Take(path.Length).SequenceEqual(path))) {
                moved.Add(number);
            }
        }

        return true;
    }

    /// <summary>Whether a node reads a knob of a scope that moved: by expression, or by reference.</summary>
    bool Reads(GraphNode node, HashSet<int> retuned) =>
        retuned.Contains(Inlining.TryGet(node.Id, out var origin) ? origin.Expansion : 0)
        && node.Texts.Any(
            text => TextureGraphExpressions.IsExpression(text.Key, out _)
                || TextureGraphParameters.IsReference(text.Value, out _)
        );

    /// <summary>The keys two tables disagree on.</summary>
    static IEnumerable<string> Moved(
        IReadOnlyDictionary<string, string>? from,
        IReadOnlyDictionary<string, string>? to
    ) {
        from ??= ImmutableDictionary<string, string>.Empty;
        to ??= ImmutableDictionary<string, string>.Empty;

        return from.Keys.Union(to.Keys, StringComparer.Ordinal).Where(
            key => !string.Equals(from.GetValueOrDefault(key), to.GetValueOrDefault(key), StringComparison.Ordinal)
        );
    }

    static Dictionary<string, string>? Copied(IReadOnlyDictionary<string, string>? table) =>
        table is null ? null : new Dictionary<string, string>(table, StringComparer.Ordinal);

    /// <summary>Says something about a node, which during a refresh makes it a compile, where it can be said.</summary>
    internal void Say(NodeDiagnostic diagnostic) {
        if (refreshing) {
            spoke = true;
        } else {
            Report(diagnostic);
        }
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

    /// <summary>Records a finished compilation, so that a value edit can be answered from it.</summary>
    void Remember(TexturePlan plan, NodeGraphModel walked) {
        if (compiling is { } author) {
            compiled = Compiled.Of(this, author, walked, plan);
        }
    }

    void Forget() {
        compiled = null;
        resolutions.Clear();
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
        Dictionary<string, string>? Arguments,
        ISubGraphSource? Source,
        NodeGraphInlining Inlining
    ) {
        /// <summary>Records a compilation.</summary>
        /// <param name="compiler">Who compiled it.</param>
        /// <param name="author">The graph the caller holds.</param>
        /// <param name="walked">The graph the walk was over, which is <paramref name="author" /> unless it inlined.</param>
        /// <param name="plan">What came out.</param>
        public static Compiled Of(
            TextureGraphCompiler compiler,
            NodeGraphModel author,
            NodeGraphModel walked,
            TexturePlan plan
        ) =>
            new(
                author,
                plan,
                NodeSnapshot.Of(walked),
                [.. walked.Edges],
                new(author.Settings, StringComparer.Ordinal),
                [.. author.Parameters],
                [.. compiler.Parameters],
                (compiler.BaseWidth, compiler.BaseHeight, compiler.BakeLevelOffset, compiler.PreviewEveryNode),
                Copied(compiler.Arguments),
                compiler.SubGraphSource,
                compiler.Inlining
            );

        /// <summary>Whether this is that compiler's own compilation of this graph, as it was configured.</summary>
        public bool Matches(TextureGraphCompiler compiler, NodeGraphModel graph, bool rebuilt) =>
            (rebuilt || ReferenceEquals(graph, Graph))
            && Host == (compiler.BaseWidth, compiler.BaseHeight, compiler.BakeLevelOffset, compiler.PreviewEveryNode)
            && compiler.Parameters.SequenceEqual(HostParameters)
            && ReferenceEquals(compiler.SubGraphSource, Source);

        /// <summary>The nodes whose numbers moved.</summary>
        /// <param name="graph">The graph a walk would be over now.</param>
        /// <param name="changed">Those nodes.</param>
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

        public bool SameArguments(IReadOnlyDictionary<string, string>? arguments) =>
            arguments is null
                ? Arguments is null
                : Arguments is not null && !Moved(Arguments, arguments).Any();

        /// <summary>Every setting but the seed, which <see cref="Refresh(NodeGraphModel)" /> answers itself.</summary>
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
        Check,
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
