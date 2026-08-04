// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>
///     Everything a piece of AI content needs resolving against: the actions its tasks name, the
///     sensors its services run, the inputs its considerations read, and the trees its subtrees call.
/// </summary>
/// <remarks>
///     Three lookups rather than one, because they are answered by three different parts of a game —
///     an action registry is code, a sensor is code, and a tree is content. A caller that has none of
///     them still gets a compile and a list of what was missing, which is what makes a half-authored
///     tree openable.
/// </remarks>
public sealed class BehaviorTreeResolver {
    readonly Dictionary<string, IWorldSensor> sensors = new(StringComparer.Ordinal);
    readonly Dictionary<string, IUtilityInput> inputs = new(StringComparer.Ordinal);
    readonly Dictionary<string, BehaviorTreeContent> trees = new(StringComparer.Ordinal);

    // How a node type that lives in another assembly gets built. P3 is what made this necessary: doc
    // 37 § Part 3 files `PerceivedTarget`, `NearestPerceived` and `MakeNoise` under
    // `Vixen.Ai.Perception`, and this assembly cannot construct a type it cannot reference. Without a
    // hook the schema could *describe* a node the compiler could not build — a type the editor offers
    // and the file refuses.
    readonly Dictionary<string, BehaviorDecoratorFactory> decorators = new(StringComparer.Ordinal);
    readonly Dictionary<string, BehaviorServiceFactory> services = new(StringComparer.Ordinal);
    readonly Dictionary<string, BehaviorTaskFactory> tasks = new(StringComparer.Ordinal);

    /// <summary>Creates a resolver with the placeholder action a failed lookup falls back to.</summary>
    public BehaviorTreeResolver() =>
        // ⚠ Registered up front rather than on demand, because the compiler that needs it is
        // reporting a problem at the time and a second failure — "no action called __unresolved" —
        // would bury the first. A branch that could not be built reads as the dead end it is.
        Actions.Register(Unresolved, new FinishWithTask(ActionStatus.Failed));

    /// <summary>What a task whose type could not be resolved runs instead.</summary>
    public const string Unresolved = "__unresolved";

    /// <summary>The actions its tasks are registered in.</summary>
    /// <remarks>
    ///     ⚠ <b>Written to, not only read.</b> A task in a file names a node type and its fields; the
    ///     object those describe is built here and registered, because two <c>Wait</c>s with different
    ///     durations are two actions with two state sizes and the registry is what an index means.
    /// </remarks>
    public AgentActionRegistry Actions { get; } = new();

    /// <summary>The node library the file's type names are looked up in.</summary>
    public BehaviorNodeSchema Schema { get; init; } = BehaviorNodeSchema.Default;

    /// <summary>Trees a dynamic subtree may name at run time.</summary>
    public BehaviorTreeLibrary Library { get; } = new();

    /// <summary>Registers a sensor an <c>UpdateBlackboard</c> service may name.</summary>
    /// <param name="name">What the file calls it.</param>
    /// <param name="sensor">The sensor.</param>
    /// <returns>This resolver.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sensor" /> is null.</exception>
    public BehaviorTreeResolver AddSensor(string name, IWorldSensor sensor) {
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentException.ThrowIfNullOrEmpty(name);

        sensors[name] = sensor;

        return this;
    }

    /// <summary>Registers a utility input a <c>.vxutility</c>'s consideration may name.</summary>
    /// <param name="name">What the file calls it.</param>
    /// <param name="input">The input.</param>
    /// <returns>This resolver.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input" /> is null.</exception>
    /// <remarks>
    ///     The same arrangement <see cref="AddSensor" /> has, and for its reason: "how hungry am I" is
    ///     a game's own question, a file can only name it, and a lambda does not go in a file.
    /// </remarks>
    public BehaviorTreeResolver AddInput(string name, IUtilityInput input) {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrEmpty(name);

        inputs[name] = input;

        return this;
    }

    /// <summary>Looks a utility input up.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="input">Where to put it.</param>
    /// <returns>Whether there is one.</returns>
    public bool TryGetInput(string name, out IUtilityInput? input) => inputs.TryGetValue(name, out input);

    /// <summary>Registers a tree a <c>RunSubtree</c> may name.</summary>
    /// <param name="tree">The tree. Its own name is what a caller names it by.</param>
    /// <returns>This resolver.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tree" /> is null.</exception>
    public BehaviorTreeResolver AddTree(BehaviorTreeContent tree) {
        ArgumentNullException.ThrowIfNull(tree);

        trees[tree.Name] = tree;

        return this;
    }

    /// <summary>Teaches it to build a decorator this assembly does not define.</summary>
    /// <param name="type">The type name, which must also be in <see cref="Schema" />.</param>
    /// <param name="factory">How to build it.</param>
    /// <returns>This resolver.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A shipped type cannot be replaced this way, deliberately.</b> The builtin switch is
    ///     consulted first, so registering a factory called <c>Cooldown</c> does nothing rather than
    ///     silently changing what every existing file means.
    /// </remarks>
    public BehaviorTreeResolver AddDecorator(string type, BehaviorDecoratorFactory factory) {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrEmpty(type);

        decorators[type] = factory;

        return this;
    }

    /// <summary>Teaches it to build a service this assembly does not define.</summary>
    /// <param name="type">The type name.</param>
    /// <param name="factory">How to build it.</param>
    /// <returns>This resolver.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory" /> is null.</exception>
    public BehaviorTreeResolver AddService(string type, BehaviorServiceFactory factory) {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrEmpty(type);

        services[type] = factory;

        return this;
    }

    /// <summary>Teaches it to build a task this assembly does not define.</summary>
    /// <param name="type">The type name.</param>
    /// <param name="factory">How to build it.</param>
    /// <returns>This resolver.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory" /> is null.</exception>
    public BehaviorTreeResolver AddTask(string type, BehaviorTaskFactory factory) {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrEmpty(type);

        tasks[type] = factory;

        return this;
    }

    /// <summary>Looks a sensor up.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="sensor">Where to put it.</param>
    /// <returns>Whether there is one.</returns>
    public bool TryGetSensor(string name, out IWorldSensor? sensor) => sensors.TryGetValue(name, out sensor);

    internal BehaviorDecorator? BuildDecorator(in BehaviorBuildContext context, ObserverAborts aborts) =>
        decorators.TryGetValue(context.Type.Type, out var factory) ? factory(in context, aborts) : null;

    internal BehaviorService? BuildService(in BehaviorBuildContext context) =>
        services.TryGetValue(context.Type.Type, out var factory) ? factory(in context) : null;

    internal BehaviorTaskBuild? BuildTask(in BehaviorBuildContext context) =>
        tasks.TryGetValue(context.Type.Type, out var factory) ? factory(in context) : null;

    /// <summary>Looks a tree up.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="tree">Where to put it.</param>
    /// <returns>Whether there is one.</returns>
    public bool TryGetTree(string name, out BehaviorTreeContent? tree) => trees.TryGetValue(name, out tree);
}

/// <summary>Turns the data in a <c>.vxbt</c> into the objects a tree is compiled from.</summary>
/// <remarks>
///     <para>
///         The one direction between <see cref="BehaviorTreeContent" /> and
///         <see cref="BehaviorTreeAsset" />: data in, live decorators and registered actions out, and
///         then <see cref="BehaviorTreeCompiler" /> flattens the result. Two steps rather than one
///         because the second is the same one a tree built in code goes through, and a file should
///         not be able to produce a template a hand-built tree could not.
///     </para>
///     <para>
///         ⚠ <b>Everything it cannot resolve is a diagnostic and a placeholder, never a refusal.</b>
///         Laying out a tree before the tasks exist is the ordinary order of work, and a compiler that
///         refused would make the file unopenable until every name resolved. A task naming nothing
///         becomes a <c>FinishWith(Failed)</c>, so the topology is still checkable and the branch
///         reads as the dead end it is.
///     </para>
/// </remarks>
public static class BehaviorTreeContentCompiler {
    /// <summary>Resolves one task by name and registers it, for content that is not a tree.</summary>
    /// <param name="resolver">Where names are looked up, and where the action is registered.</param>
    /// <param name="layout">The blackboard its key fields resolve against.</param>
    /// <param name="type">The node type's name.</param>
    /// <param name="fields">What the file said.</param>
    /// <param name="diagnostics">Where to say what could not be resolved.</param>
    /// <param name="action">The registered action's index.</param>
    /// <returns>Whether it resolved to something other than the placeholder.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A utility set names its tasks the same way a tree does, and this is the seam that makes
    ///     that true rather than merely intended.</b> One schema, one factory table, one registry and
    ///     one set of rules about sharing an action between two callers that describe it identically —
    ///     which is doc 37 § D2 stated as code rather than as a paragraph.
    /// </remarks>
    public static bool TryResolveTask(
        BehaviorTreeResolver resolver,
        BlackboardLayout layout,
        string type,
        Dictionary<string, string> fields,
        ICollection<BehaviorTreeDiagnostic> diagnostics,
        out ushort action
    ) {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(diagnostics);

        action = 0;

        if (!resolver.Schema.TryGet(type, out var declared) || declared is not { Slot: BehaviorSlot.Task }) {
            diagnostics.Add(new(Symbol.Intern(type), $"'{type}' is not a task this build knows."));

            return resolver.Actions.TryGetIndex(Symbol.Intern(BehaviorTreeResolver.Unresolved), out action);
        }

        var state = new BuildState(resolver, layout, diagnostics);
        var key = state.Action(new() { Name = type, Type = type, Fields = fields }, declared);

        return resolver.Actions.TryGetIndex(Symbol.Intern(key), out action);
    }

    /// <summary>Builds the authoring tree a compiler takes.</summary>
    /// <param name="content">The file.</param>
    /// <param name="resolver">Where names are looked up, and where actions are registered.</param>
    /// <param name="layout">The blackboard the keys were compiled into.</param>
    /// <param name="diagnostics">Everything that could not be resolved.</param>
    /// <returns>The tree, ready to compile.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static BehaviorTreeAsset Build(
        BehaviorTreeContent content,
        BehaviorTreeResolver resolver,
        BlackboardLayout layout,
        ICollection<BehaviorTreeDiagnostic> diagnostics
    ) {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var state = new BuildState(resolver, layout, diagnostics);
        var root = content.Root is null
            ? Placeholder("root", diagnostics, "The tree has no root.")
            : state.Node(content.Root, new HashSet<string>(StringComparer.Ordinal) { content.Name });

        return BehaviorTree.Asset(content.Name, root);
    }

    /// <summary>Builds and flattens in one call.</summary>
    /// <param name="content">The file.</param>
    /// <param name="resolver">Where names are looked up.</param>
    /// <param name="diagnostics">Everything wrong with it, from both halves.</param>
    /// <param name="template">The compiled tree, or null.</param>
    /// <returns>Whether it compiled.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static bool TryCompile(
        BehaviorTreeContent content,
        BehaviorTreeResolver resolver,
        out IReadOnlyList<BehaviorTreeDiagnostic> diagnostics,
        out BehaviorTreeTemplate? template
    ) {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(resolver);

        var problems = new List<BehaviorTreeDiagnostic>();
        var layout = content.BuildLayout(problems);
        var asset = Build(content, resolver, layout, problems);

        if (!BehaviorTreeCompiler.TryCompile(asset, resolver.Actions, layout, out var compiled, out template)) {
            problems.AddRange(compiled);
        }

        diagnostics = problems;

        return template is not null && problems.Count == 0;
    }

    static BehaviorNodeDefinition Placeholder(string name, ICollection<BehaviorTreeDiagnostic> diagnostics, string why) {
        diagnostics.Add(new(Symbol.Intern(name), why));

        return BehaviorTree.Task(name, BehaviorTreeResolver.Unresolved);
    }

    /// <summary>What one build needs to carry: the lookups, the layout and the growing action list.</summary>
    sealed class BuildState(
        BehaviorTreeResolver resolver,
        BlackboardLayout layout,
        ICollection<BehaviorTreeDiagnostic> diagnostics
    ) {
        readonly Dictionary<string, ushort> actionsByKey = new(StringComparer.Ordinal);

        public BehaviorNodeDefinition Node(BehaviorNodeContent content, HashSet<string> openTrees) {
            if (!resolver.Schema.TryGet(content.Type, out var type) || type is null) {
                return Placeholder(content.Name, diagnostics, $"'{content.Type}' is not a node this build knows.");
            }

            return type.Slot switch {
                BehaviorSlot.Composite => Composite(content, type, openTrees),
                BehaviorSlot.Task => Task(content, type, openTrees),
                _ => Placeholder(content.Name, diagnostics, $"'{content.Type}' is a {type.Slot} and cannot be a node.")
            };
        }

        BehaviorNodeDefinition Composite(BehaviorNodeContent content, BehaviorNodeType type, HashSet<string> openTrees) {
            var kind = Enum.TryParse<BehaviorCompositeKind>(type.Type, out var parsed)
                ? parsed
                : BehaviorCompositeKind.Selector;

            var node = new BehaviorNodeDefinition {
                Name = Symbol.Intern(content.Name),
                Kind = BehaviorNodeKind.Composite,
                Composite = kind,
                FinishMode = BehaviorNodeSchema.Choice<ParallelFinishMode>(type, content.Fields, "FinishMode")
            };

            foreach (var child in content.Children) {
                node.Add(Node(child, openTrees));
            }

            Attach(node, content, composite: true);

            return node;
        }

        BehaviorNodeDefinition Task(BehaviorNodeContent content, BehaviorNodeType type, HashSet<string> openTrees) {
            // A static subtree is a node in the authoring tree rather than an action: the compiler
            // splices it, so it never survives into the template at all.
            if (string.Equals(type.Type, "RunSubtree", StringComparison.Ordinal)) {
                return Subtree(content, type, openTrees);
            }

            var action = Action(content, type);
            var node = BehaviorTree.Task(content.Name, action);

            Attach(node, content, composite: false);

            return node;
        }

        BehaviorNodeDefinition Subtree(BehaviorNodeContent content, BehaviorNodeType type, HashSet<string> openTrees) {
            var name = BehaviorNodeSchema.Read(type, content.Fields, "Tree");

            if (!resolver.TryGetTree(name, out var child) || child?.Root is null) {
                return Placeholder(content.Name, diagnostics, $"No tree called '{name}' to splice in here.");
            }

            if (!openTrees.Add(name)) {
                return Placeholder(content.Name, diagnostics, $"'{name}' contains itself.");
            }

            var spliced = Node(child.Root, openTrees);

            openTrees.Remove(name);

            var node = BehaviorTree.Subtree(content.Name, BehaviorTree.Asset(name, spliced));

            Attach(node, content, composite: false);

            return node;
        }

        void Attach(BehaviorNodeDefinition node, BehaviorNodeContent content, bool composite) {
            foreach (var row in content.Decorators) {
                if (Decorator(row) is { } decorator) {
                    node.With(decorator);
                }
            }

            foreach (var row in content.Services) {
                if (!composite) {
                    diagnostics.Add(new(node.Name, "A service attaches to a composite, not to a task."));

                    continue;
                }

                if (Service(row) is { } service) {
                    node.With(service, row.Interval, row.RandomDeviation);
                }
            }
        }

        BehaviorDecorator? Decorator(BehaviorAttachmentContent row) {
            if (!resolver.Schema.TryGet(row.Type, out var type) || type is not { Slot: BehaviorSlot.Decorator }) {
                diagnostics.Add(new(Symbol.Intern(row.Type), $"'{row.Type}' is not a decorator this build knows."));

                return null;
            }

            var aborts = BehaviorNodeSchema.Choice<ObserverAborts>(type, row.Fields, "Aborts");

            return row.Type switch {
                "Blackboard" => Blackboard(type, row, aborts),
                "CompareEntries" => new CompareEntriesDecorator(
                    Key(type, row.Fields, "Left"),
                    Key(type, row.Fields, "Right"),
                    BehaviorNodeSchema.Choice<BlackboardTest>(type, row.Fields, "Test"),
                    aborts
                ),
                "IsAtLocation" => new IsAtLocationDecorator(
                    Key(type, row.Fields, "Here"),
                    Key(type, row.Fields, "There"),
                    BehaviorNodeSchema.Number(type, row.Fields, "Radius"),
                    BehaviorNodeSchema.Toggle(type, row.Fields, "IgnoreHeight"),
                    aborts
                ),
                "Cone" => new ConeDecorator(
                    Key(type, row.Fields, "Origin"),
                    Key(type, row.Fields, "Direction"),
                    Key(type, row.Fields, "Target"),
                    BehaviorNodeSchema.Number(type, row.Fields, "HalfAngle"),
                    BehaviorNodeSchema.Toggle(type, row.Fields, "KeepTesting"),
                    aborts
                ),
                "Inverter" => new InverterDecorator(),
                "ForceSuccess" => new ForceSuccessDecorator(),
                "ForceFailure" => new ForceFailureDecorator(),
                "RandomChance" => new RandomChanceDecorator(BehaviorNodeSchema.Number(type, row.Fields, "Probability")),
                "Cooldown" => new CooldownDecorator(BehaviorNodeSchema.Number(type, row.Fields, "Seconds")),
                "TimeLimit" => new TimeLimitDecorator(BehaviorNodeSchema.Number(type, row.Fields, "Seconds")),
                "TagCooldown" => new TagCooldownDecorator(
                    Word(type, row.Fields, "Tag"),
                    BehaviorNodeSchema.Number(type, row.Fields, "Seconds")
                ),
                "SetTagCooldown" => new SetTagCooldownDecorator(
                    Word(type, row.Fields, "Tag"),
                    BehaviorNodeSchema.Number(type, row.Fields, "Seconds")
                ),
                "Loop" => Loop(type, row),

                // Anything else is another assembly's, and the resolver is where it said so. The
                // builtin arms come first, so a project cannot shadow a shipped node and quietly
                // change what every existing file means.
                _ => Registered(type, row, aborts)
            };
        }

        BehaviorDecorator? Registered(BehaviorNodeType type, BehaviorAttachmentContent row, ObserverAborts aborts) {
            var decorator = resolver.BuildDecorator(Context(type, row.Fields), aborts);

            if (decorator is null) {
                diagnostics.Add(new(Symbol.Intern(row.Type), $"'{row.Type}' has no factory registered to build it."));
            }

            return decorator;
        }

        BehaviorBuildContext Context(BehaviorNodeType type, Dictionary<string, string> fields) =>
            new(type, layout, fields, diagnostics);

        BlackboardDecorator Blackboard(BehaviorNodeType type, BehaviorAttachmentContent row, ObserverAborts aborts) {
            var key = Key(type, row.Fields, "Key");
            var test = BehaviorNodeSchema.Choice<BlackboardTest>(type, row.Fields, "Test");

            if (test is BlackboardTest.IsSet or BlackboardTest.IsNotSet) {
                return BlackboardDecorator.Set(key, test == BlackboardTest.IsSet, aborts);
            }

            if (key.IsValid && key.Index < layout.Count && layout[key].Type == BlackboardValueType.Symbol) {
                return BlackboardDecorator.Word(key, Word(type, row.Fields, "Word"), test != BlackboardTest.NotEqual, aborts);
            }

            return BlackboardDecorator.Number(key, test, BehaviorNodeSchema.Number(type, row.Fields, "Value"), aborts);
        }

        static LoopDecorator Loop(BehaviorNodeType type, BehaviorAttachmentContent row) {
            var times = BehaviorNodeSchema.Integer(type, row.Fields, "Times");
            var timeout = BehaviorNodeSchema.Number(type, row.Fields, "Timeout");

            return times > 0 ? new LoopDecorator(times) : LoopDecorator.UntilFailure(timeout);
        }

        BehaviorService? Service(BehaviorAttachmentContent row) {
            if (!resolver.Schema.TryGet(row.Type, out var type) || type is not { Slot: BehaviorSlot.Service }) {
                diagnostics.Add(new(Symbol.Intern(row.Type), $"'{row.Type}' is not a service this build knows."));

                return null;
            }

            if (!string.Equals(type.Type, "UpdateBlackboard", StringComparison.Ordinal)) {
                var registered = resolver.BuildService(Context(type, row.Fields));

                if (registered is null) {
                    diagnostics.Add(new(Symbol.Intern(row.Type), $"'{row.Type}' has no factory registered to build it."));
                }

                return registered;
            }

            var name = BehaviorNodeSchema.Read(type, row.Fields, "Sensor");

            if (!resolver.TryGetSensor(name, out var sensor) || sensor is null) {
                diagnostics.Add(new(Symbol.Intern(row.Type), $"No sensor called '{name}' is registered."));

                return null;
            }

            return new UpdateBlackboardService(sensor, Key(type, row.Fields, "Key"));
        }

        /// <summary>
        ///     The registered action for one task node, registering it the first time it is seen.
        /// </summary>
        /// <remarks>
        ///     ⚠ <b>Keyed on the type <i>and its fields</i>, not on the type alone.</b> Two
        ///     <c>Wait</c>s with different durations are two different actions — an action object
        ///     carries its own settings and is shared by every agent — so a registry keyed on the type
        ///     would give the second one the first one's duration.
        /// </remarks>
        internal string Action(BehaviorNodeContent content, BehaviorNodeType type) {
            var key = Key(type, content.Fields);

            if (actionsByKey.ContainsKey(key)) {
                return key;
            }

            // ⚠ And the registry as well as this build's own table, because a resolver outlives a
            // compile. A game compiles every `.vxbt` it ships against one resolver, and two trees that
            // both contain `Wait(1)` name the same action — which is the sharing this key exists for.
            // Registering it twice threw, so the second tree in a project was a crash at start-up.
            if (resolver.Actions.TryGetIndex(Symbol.Intern(key), out var existing)) {
                actionsByKey[key] = existing;

                return key;
            }

            var built = Build(content, type) is { } action
                ? new BehaviorTaskBuild(action, StateSize(type.Type))
                : resolver.BuildTask(Context(type, content.Fields));

            if (built is null) {
                diagnostics.Add(new(Symbol.Intern(content.Name), $"'{type.Type}' could not be built."));
                built = new BehaviorTaskBuild(new FinishWithTask(ActionStatus.Failed));
            }

            actionsByKey[key] = resolver.Actions.Register(key, built.Value.Action, built.Value.StateSize);

            return key;
        }

        IAgentAction? Build(BehaviorNodeContent content, BehaviorNodeType type) => type.Type switch {
            "Wait" => new WaitTask(BehaviorNodeSchema.Number(type, content.Fields, "Seconds")),
            "WaitBlackboardTime" => new WaitBlackboardTimeTask(
                Key(type, content.Fields, "Key"),
                BehaviorNodeSchema.Number(type, content.Fields, "Deviation")
            ),
            "FinishWith" => new FinishWithTask(BehaviorNodeSchema.Choice<ActionStatus>(type, content.Fields, "Result")),
            "SetBlackboardValue" => SetValue(type, content),
            "ClearBlackboardValue" => new ClearBlackboardValueTask(Key(type, content.Fields, "Key")),
            "Log" => new LogTask(Word(type, content.Fields, "Message")),
            "RunSubtreeDynamic" => new RunSubtreeDynamicTask(Key(type, content.Fields, "Key"), resolver.Library),
            _ => null
        };

        SetBlackboardValueTask SetValue(BehaviorNodeType type, BehaviorNodeContent content) {
            var key = Key(type, content.Fields, "Key");
            var from = BehaviorNodeSchema.Read(type, content.Fields, "From");

            if (from.Length > 0) {
                return SetBlackboardValueTask.Copy(key, Key(type, content.Fields, "From"));
            }

            if (key.IsValid && key.Index < layout.Count && layout[key].Type == BlackboardValueType.Symbol) {
                return SetBlackboardValueTask.Word(key, Word(type, content.Fields, "Word"));
            }

            return SetBlackboardValueTask.Number(key, BehaviorNodeSchema.Number(type, content.Fields, "Value"));
        }

        static int StateSize(string type) => type switch {
            "Wait" => WaitTask.StateSize,
            "WaitBlackboardTime" => WaitBlackboardTimeTask.StateSize,
            "RunSubtreeDynamic" => RunSubtreeDynamicTask.StateSize,
            _ => 0
        };

        static string Key(BehaviorNodeType type, Dictionary<string, string> fields) {
            var text = new System.Text.StringBuilder(type.Type);

            foreach (var field in type.Fields) {
                text.Append('|').Append(BehaviorNodeSchema.Read(type, fields, field.Name));
            }

            return text.ToString();
        }

        BlackboardKey Key(BehaviorNodeType type, Dictionary<string, string> fields, string field) {
            var name = BehaviorNodeSchema.Read(type, fields, field);

            if (name.Length == 0) {
                diagnostics.Add(new(Symbol.Intern(type.Type), $"'{type.Label}' needs a key for {field}."));

                return BlackboardKey.Invalid;
            }

            if (layout.TryGetKey(Symbol.Intern(name), out var key)) {
                return key;
            }

            diagnostics.Add(new(Symbol.Intern(type.Type), $"'{name}' is not a key on this tree's blackboard."));

            return BlackboardKey.Invalid;
        }

        static Symbol Word(BehaviorNodeType type, Dictionary<string, string> fields, string field) =>
            Symbol.Intern(BehaviorNodeSchema.Read(type, fields, field));
    }
}
