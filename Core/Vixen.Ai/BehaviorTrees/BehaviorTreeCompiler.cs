// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>Something wrong with an authored tree, and where.</summary>
/// <param name="Node">The node's name, or <see cref="Symbol.None" /> for the tree itself.</param>
/// <param name="Message">What is wrong, written to be read by the person who drew it.</param>
/// <remarks>
///     A value rather than an exception, because P2's editor draws these in a list beside the canvas
///     and clicks through to the node — the same shape <c>NodeDiagnostic</c> has for the shader and
///     VFX graphs. <see cref="BehaviorTreeCompiler.Compile" /> throws only when a caller asked it to.
/// </remarks>
public readonly record struct BehaviorTreeDiagnostic(Symbol Node, string Message) {
    /// <inheritdoc />
    public override string ToString() => Node.IsSome ? $"{Node}: {Message}" : Message;
}

/// <summary>Turns an authored tree into the flat, immutable thing a thousand agents share.</summary>
/// <remarks>
///     <para>
///         The whole of the layout decision lives here: nodes are written out <b>depth-first in
///         pre-order</b>, so an index is a priority; each one records its
///         <see cref="BehaviorNode.LastDescendant" />, so a subtree is a contiguous range and the
///         abort test is two comparisons; and every node, decorator and service is assigned a byte
///         range in one block whose total size is known before a single agent exists.
///     </para>
///     <para>
///         ⚠ <b>A static subtree is spliced, not referenced.</b> Splicing is what keeps pre-order
///         equal to priority across the boundary, so a decorator in the parent tree can abort a
///         branch inside the child and the range test still means something. The cost is that a
///         subtree used in four places is compiled four times, which is bytes rather than behaviour,
///         and a cycle is refused by name rather than by stack overflow.
///     </para>
/// </remarks>
public static class BehaviorTreeCompiler {
    /// <summary>Compiles a tree.</summary>
    /// <param name="asset">The authored tree.</param>
    /// <param name="actions">Where a task's action name is resolved.</param>
    /// <param name="layout">The blackboard shape its decorators read.</param>
    /// <param name="diagnostics">Everything wrong with it.</param>
    /// <param name="template">The compiled tree, or null if anything was wrong.</param>
    /// <returns>Whether it compiled.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static bool TryCompile(
        BehaviorTreeAsset asset,
        AgentActionRegistry actions,
        BlackboardLayout layout,
        out IReadOnlyList<BehaviorTreeDiagnostic> diagnostics,
        out BehaviorTreeTemplate? template
    ) {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(layout);

        var state = new CompileState(actions, layout);

        state.Walk(asset.Root, -1, [asset]);

        diagnostics = state.Diagnostics;

        if (state.Diagnostics.Count > 0) {
            template = null;

            return false;
        }

        template = state.Build(asset.Name, layout);

        return true;
    }

    /// <summary>Compiles a tree, and refuses to carry on if it will not.</summary>
    /// <param name="asset">The authored tree.</param>
    /// <param name="actions">Where a task's action name is resolved.</param>
    /// <param name="layout">The blackboard shape its decorators read.</param>
    /// <returns>The compiled tree.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="InvalidOperationException">The tree does not compile; the message lists why.</exception>
    /// <remarks>For setup code, tests and samples. An importer uses the other one and shows the list.</remarks>
    public static BehaviorTreeTemplate Compile(
        BehaviorTreeAsset asset,
        AgentActionRegistry actions,
        BlackboardLayout layout
    ) {
        if (TryCompile(asset, actions, layout, out var diagnostics, out var template)) {
            return template!;
        }

        throw new InvalidOperationException(
            $"'{asset?.Name}' does not compile:{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", diagnostics)
        );
    }

    /// <summary>Everything the walk accumulates, so that the walk itself stays readable.</summary>
    sealed class CompileState(AgentActionRegistry actions, BlackboardLayout layout) {
        readonly List<BehaviorNode> nodes = [];
        readonly List<BehaviorDecoratorSlot> decorators = [];
        readonly List<BehaviorServiceSlot> services = [];
        readonly List<BlackboardKey> keys = [];
        readonly List<BehaviorDecorator> pendingDecorators = [];

        int nested;

        public List<BehaviorTreeDiagnostic> Diagnostics { get; } = [];

        /// <summary>Writes one authored node and everything under it, in pre-order.</summary>
        /// <param name="definition">The authored node.</param>
        /// <param name="parent">Its parent's compiled index, or -1.</param>
        /// <param name="open">The assets already being spliced, so a cycle is a message and not a crash.</param>
        /// <returns>Its compiled index.</returns>
        public int Walk(BehaviorNodeDefinition definition, int parent, List<BehaviorTreeAsset> open) {
            // A static subtree is spliced in place of its node, so the node it was authored as does
            // not survive into the template at all — which is what makes the boundary invisible to
            // the abort test.
            if (definition.Subtree is not null) {
                if (open.Contains(definition.Subtree)) {
                    Diagnostics.Add(new(definition.Name, $"'{definition.Subtree.Name}' contains itself."));

                    return Emit(definition, parent);
                }

                open.Add(definition.Subtree);

                var spliced = Walk(definition.Subtree.Root, parent, open);

                open.RemoveAt(open.Count - 1);

                return spliced;
            }

            var index = Emit(definition, parent);

            if (definition.Kind == BehaviorNodeKind.Task) {
                Finish(index);

                return index;
            }

            if (definition.Children.Count == 0) {
                Diagnostics.Add(new(definition.Name, "A composite with no children can never do anything."));
            }

            if (definition.Composite == BehaviorCompositeKind.Parallel) {
                CheckParallel(definition);
            }

            if (definition.Composite == BehaviorCompositeKind.RandomSelector && definition.Children.Count > 64) {
                Diagnostics.Add(
                    new(definition.Name, "A random selector may have at most 64 children; its tried-mask is 64 bits.")
                );
            }

            var first = -1;

            foreach (var child in definition.Children) {
                var written = Walk(child, index, open);

                if (first < 0) {
                    first = written;
                }
            }

            var node = nodes[index];

            node.FirstChild = definition.Children.Count > 0 ? first : -1;
            node.ChildCount = definition.Children.Count;
            nodes[index] = node;

            Finish(index);

            return index;
        }

        /// <summary>Assigns every byte range and hands back the immutable thing.</summary>
        public BehaviorTreeTemplate Build(Symbol name, BlackboardLayout blackboard) {
            // The header first, then the two bitsets the stepper needs per decorator — what the
            // decorator last answered, and whether it has ever been asked. The second is not
            // redundant: "changed" is meaningless until there is a previous answer, and without it
            // every decorator would look like it had just flipped on the first key write.
            var offset = BehaviorTreeInstance.HeaderSize;

            offset = Align(offset, 8);

            var resultBits = offset;

            offset += ((decorators.Count + 63) / 64) * 8;

            var evaluatedBits = offset;

            offset += ((decorators.Count + 63) / 64) * 8;

            var serviceTimers = offset;

            offset += services.Count * sizeof(float);
            offset = Align(offset, 8);

            for (var index = 0; index < nodes.Count; index++) {
                var node = nodes[index];

                node.MemorySize = node.Kind == BehaviorNodeKind.Composite
                    ? BehaviorTreeInstance.CompositeStateSize
                    : actions.StateSize(node.Action);

                node.MemoryOffset = offset;
                offset = Align(offset + node.MemorySize, 8);
                nodes[index] = node;
            }

            for (var index = 0; index < decorators.Count; index++) {
                var slot = decorators[index];
                var size = slot.Decorator.StateSize;

                decorators[index] = slot with { MemoryOffset = offset, MemorySize = size };
                offset = Align(offset + size, 8);
            }

            for (var index = 0; index < services.Count; index++) {
                var slot = services[index];
                var size = slot.Service.StateSize;

                services[index] = slot with { MemoryOffset = offset, MemorySize = size };
                offset = Align(offset + size, 8);
            }

            var distinct = keys.Distinct().ToArray();
            var tags = decorators
                .Select(slot => slot.Decorator)
                .OfType<ITagCooldown>()
                .Select(cooldown => cooldown.Tag)
                .Distinct()
                .ToArray();

            return new(
                name,
                [.. nodes],
                [.. decorators],
                [.. services],
                [.. keys],
                distinct,
                tags,
                blackboard,
                offset,
                nested
            ) {
                ResultBitsOffset = resultBits,
                EvaluatedBitsOffset = evaluatedBits,
                ServiceTimerOffset = serviceTimers
            };
        }

        int Emit(BehaviorNodeDefinition definition, int parent) {
            var index = nodes.Count;

            nodes.Add(
                new() {
                    Name = definition.Name,
                    Kind = definition.Kind,
                    Composite = definition.Composite,
                    FinishMode = definition.FinishMode,
                    Parent = parent,
                    FirstChild = -1,
                    ChildCount = 0,
                    LastDescendant = index,
                    DecoratorStart = decorators.Count,
                    ServiceStart = services.Count,
                    Weight = definition.Weight > 0f ? definition.Weight : 1f,
                    NestedSlot = -1
                }
            );

            var node = nodes[index];

            if (definition.Kind == BehaviorNodeKind.Task) {
                node.Action = ResolveAction(definition);

                // A task that runs a tree named by a key cannot be spliced, so it keeps an instance
                // of its own — and the slot for it is assigned here, at compile time, so that an
                // agent's array of them is sized before it exists.
                if (actions[node.Action] is INestedTreeTask) {
                    node.NestedSlot = nested++;
                }
            } else if (definition.Services.Count > 0) {
                foreach (var service in definition.Services) {
                    if (service.Interval <= 0f) {
                        Diagnostics.Add(new(definition.Name, "A service's interval must be positive."));
                    }

                    if (service.RandomDeviation < 0f || service.RandomDeviation > service.Interval) {
                        Diagnostics.Add(
                            new(definition.Name, "A service's random deviation must be between zero and its interval.")
                        );
                    }

                    services.Add(new(service.Service, index, service.Interval, service.RandomDeviation, 0, 0));
                }
            }

            if (definition.Kind == BehaviorNodeKind.Task && definition.Services.Count > 0) {
                Diagnostics.Add(new(definition.Name, "A service attaches to a composite, not to a task."));
            }

            node.ServiceCount = services.Count - node.ServiceStart;
            nodes[index] = node;

            pendingDecorators.Clear();
            pendingDecorators.AddRange(definition.Decorators);

            foreach (var decorator in pendingDecorators) {
                AddDecorator(definition, index, decorator);
            }

            node = nodes[index];
            node.DecoratorCount = decorators.Count - node.DecoratorStart;
            nodes[index] = node;

            return index;
        }

        void AddDecorator(BehaviorNodeDefinition definition, int index, BehaviorDecorator decorator) {
            var start = keys.Count;

            foreach (var key in decorator.ObservedKeys) {
                if (!key.IsValid || key.Index >= layout.Count) {
                    Diagnostics.Add(
                        new(definition.Name, $"{decorator.GetType().Name} reads a key that is not in the blackboard.")
                    );

                    continue;
                }

                keys.Add(key);
            }

            var count = keys.Count - start;

            // ⚠ An observer with nothing to observe can never fire, which is the failure that looks
            // like "the AI sometimes gets stuck" rather than like a mistake. Said here, once, rather
            // than discovered in a play test.
            if (decorator.Aborts != ObserverAborts.None && count == 0) {
                Diagnostics.Add(
                    new(
                        definition.Name,
                        $"{decorator.GetType().Name} declares {decorator.Aborts} but reads no blackboard key, "
                        + "so nothing can ever wake it. Give it a key or set Aborts to None."
                    )
                );
            }

            decorators.Add(new(decorator, index, decorator.Aborts, start, count, 0, 0));
        }

        ushort ResolveAction(BehaviorNodeDefinition definition) {
            if (actions.TryGetIndex(definition.Action, out var action)) {
                return action;
            }

            Diagnostics.Add(new(definition.Name, $"No action called '{definition.Action}' is registered."));

            return 0;
        }

        void CheckParallel(BehaviorNodeDefinition definition) {
            if (definition.Children.Count != 2) {
                Diagnostics.Add(
                    new(
                        definition.Name,
                        "A parallel has exactly two children: the main task, then the background branch."
                    )
                );

                return;
            }

            if (definition.Children[0].Kind != BehaviorNodeKind.Task || definition.Children[0].Subtree is not null) {
                Diagnostics.Add(
                    new(definition.Name, "A parallel's first child must be a task. Unreal's restriction, and kept: "
                        + "two branches whose decorators can abort each other make the abort scope undefinable.")
                );
            }
        }

        void Finish(int index) {
            var node = nodes[index];

            node.LastDescendant = nodes.Count - 1;
            nodes[index] = node;
        }

        static int Align(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;
    }
}
