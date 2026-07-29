// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.AnimationGraph;
using Vixen.Editor.Core;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>An animation graph, open for editing.</summary>
/// <remarks>
///     <para>
///         The third graph doc 11 names. The model and the compiler are
///         <c>Vixen.Editor.AnimationGraph</c>'s — which knows nothing about a project, a document or
///         a panel, so its tests compile a graph with no editor in the way — and this is the YAML end
///         of it and the undo stack over it.
///     </para>
///     <para>
///         ⚠ <b>Compiled when it is asked for, not on every edit.</b> Adding a state produces a graph
///         with a state nothing transitions to; adding a transition produces one whose destination is
///         briefly the wrong state. Compiling on every change would fill the panel with complaints
///         about a state somebody is halfway through leaving — the same rule
///         <c>CompositorDocument</c> states.
///     </para>
///     <para>
///         ⚠ <b>Clips are not resolved and the compiler is told so.</b> Loading an
///         <c>AnimationClip</c> needs the skeleton it is baked against, which the editor has no way to
///         choose from a graph alone; the compiler reports each unresolved clip and checks the
///         topology anyway, which is the half of a graph an author can actually get wrong.
///     </para>
/// </remarks>
public sealed class AnimationGraphDocument : EditorDocument {
    /// <summary>What an animation graph is written as.</summary>
    public const string Extension = AnimationGraphAsset.Extension;

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The graph.</summary>
    public AnimationGraphAsset Graph { get; private set; }

    /// <summary>Why the file would not read, or <see langword="null" />.</summary>
    public string? LoadError { get; }

    /// <summary>What the last <see cref="Compile" /> had to say.</summary>
    public IReadOnlyList<AnimationGraphDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>What the last <see cref="Compile" /> produced, or <see langword="null" />.</summary>
    public AnimationGraphArtefact? Artefact { get; private set; }

    /// <summary>Raised after anything changes the graph.</summary>
    public event Action<AnimationGraphDocument>? Changed;

    /// <summary>Opens a graph.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public AnimationGraphDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        var text = AssetFile.Read(path);

        if (text.Trim().Length == 0) {
            // ⚠ A new graph is one layer with one state rather than nothing, because the compiler's
            // first two diagnostics are "no layers" and "no states" — a file that opened complaining
            // about itself would be one nobody trusts. Idle is what the first state is called in
            // every character graph anybody has written.
            Graph = new() {
                Name = Path.GetFileNameWithoutExtension(path),
                Layers = [new() { Name = "Base", Default = "Idle", States = [new() { Name = "Idle", X = 120f, Y = 120f }] }]
            };

            return;
        }

        try {
            Graph = YamlSerializer.Parse<AnimationGraphAsset>(text);

            if (Graph.Version > AnimationGraphAsset.Current) {
                throw new NotSupportedException(
                    $"This graph is version {Graph.Version} and this build reads {AnimationGraphAsset.Current}."
                );
            }
        } catch (Exception exception) when (exception is YamlBindingException
            or YamlParseException or NotSupportedException) {
            Graph = new() { Name = Path.GetFileNameWithoutExtension(path) };
            LoadError = exception.Message;
        }

        if (Graph.Name.Length == 0) {
            Graph.Name = Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <summary>The layer at an index, or <see langword="null" />.</summary>
    /// <param name="index">Which layer.</param>
    /// <returns>The layer.</returns>
    public AnimationLayerData? Layer(int index) =>
        index >= 0 && index < Graph.Layers.Count ? Graph.Layers[index] : null;

    /// <summary>Adds a layer, undoably.</summary>
    /// <param name="name">What it is called.</param>
    public void AddLayer(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var added = new AnimationLayerData { Name = name, Blend = Vixen.Animation.LayerBlend.Override };

        Run("Add Layer", () => Graph.Layers.Add(added), () => Graph.Layers.Remove(added));
    }

    /// <summary>Removes a layer, undoably.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveLayer(AnimationLayerData layer) {
        ArgumentNullException.ThrowIfNull(layer);

        var index = Graph.Layers.IndexOf(layer);

        if (index < 0) {
            return false;
        }

        Run("Remove Layer", () => Graph.Layers.RemoveAt(index), () => Graph.Layers.Insert(index, layer));

        return true;
    }

    /// <summary>Adds a state to a layer, undoably.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="name">What it is called.</param>
    /// <param name="x">Where its box sits.</param>
    /// <param name="y">And how far down.</param>
    /// <returns>The state.</returns>
    public AnimationStateData AddState(AnimationLayerData layer, string name, float x, float y) {
        ArgumentNullException.ThrowIfNull(layer);

        var added = new AnimationStateData {
            Name = Unique(name, layer.States.Select(state => state.Name)),
            X = x,
            Y = y
        };

        Run(
            "Add State",
            () => {
                layer.States.Add(added);

                if (layer.Default.Length == 0) {
                    layer.Default = added.Name;
                }
            },
            () => {
                layer.States.Remove(added);

                if (string.Equals(layer.Default, added.Name, StringComparison.Ordinal)) {
                    layer.Default = layer.States.Count > 0 ? layer.States[0].Name : string.Empty;
                }
            }
        );

        return added;
    }

    /// <summary>Removes a state and every transition into it, undoably.</summary>
    /// <param name="layer">The layer it is in.</param>
    /// <param name="state">The state.</param>
    /// <returns>Whether it was there.</returns>
    /// <remarks>
    ///     ⚠ <b>The transitions into it go with it.</b> A transition to a state that is not there is
    ///     a diagnostic the compiler reports, and leaving the file in that condition because somebody
    ///     deleted a state would be the editor making a broken graph rather than an edit.
    /// </remarks>
    public bool RemoveState(AnimationLayerData layer, AnimationStateData state) {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(state);

        var index = layer.States.IndexOf(state);

        if (index < 0) {
            return false;
        }

        List<(AnimationStateData Source, AnimationTransitionData Transition, int At)> detached = [];

        foreach (var source in layer.States) {
            for (var slot = source.Transitions.Count - 1; slot >= 0; slot--) {
                if (string.Equals(source.Transitions[slot].To, state.Name, StringComparison.Ordinal)) {
                    detached.Add((source, source.Transitions[slot], slot));
                }
            }
        }

        Run(
            "Remove State",
            () => {
                foreach (var (source, transition, _) in detached) {
                    source.Transitions.Remove(transition);
                }

                layer.States.RemoveAt(index);
            },
            () => {
                layer.States.Insert(index, state);

                foreach (var (source, transition, at) in detached) {
                    source.Transitions.Insert(Math.Clamp(at, 0, source.Transitions.Count), transition);
                }
            }
        );

        return true;
    }

    /// <summary>Moves a state's box, undoably.</summary>
    /// <param name="state">The state.</param>
    /// <param name="x">Where to.</param>
    /// <param name="y">And how far down.</param>
    public void MoveState(AnimationStateData state, float x, float y) {
        ArgumentNullException.ThrowIfNull(state);

        var (wasX, wasY) = (state.X, state.Y);

        Run(
            "Move State",
            () => (state.X, state.Y) = (x, y),
            () => (state.X, state.Y) = (wasX, wasY)
        );
    }

    /// <summary>Adds a transition between two states, undoably.</summary>
    /// <param name="from">Where it leaves.</param>
    /// <param name="to">Where it arrives, by name.</param>
    /// <returns>The transition.</returns>
    public AnimationTransitionData AddTransition(AnimationStateData from, string to) {
        ArgumentNullException.ThrowIfNull(from);

        var added = new AnimationTransitionData { To = to };

        Run("Add Transition", () => from.Transitions.Add(added), () => from.Transitions.Remove(added));

        return added;
    }

    /// <summary>Removes a transition, undoably.</summary>
    /// <param name="from">The state it leaves.</param>
    /// <param name="transition">The transition.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveTransition(AnimationStateData from, AnimationTransitionData transition) {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(transition);

        var index = from.Transitions.IndexOf(transition);

        if (index < 0) {
            return false;
        }

        Run(
            "Remove Transition",
            () => from.Transitions.RemoveAt(index),
            () => from.Transitions.Insert(index, transition)
        );

        return true;
    }

    /// <summary>Adds a parameter, undoably.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="type">What it holds.</param>
    public void AddParameter(string name, Vixen.Animation.AnimationParameterType type) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var added = new AnimationParameterData {
            Name = Unique(name, Graph.Parameters.Select(parameter => parameter.Name)),
            Type = type
        };

        Run("Add Parameter", () => Graph.Parameters.Add(added), () => Graph.Parameters.Remove(added));
    }

    /// <summary>Removes a parameter, undoably.</summary>
    /// <param name="parameter">The parameter.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveParameter(AnimationParameterData parameter) {
        ArgumentNullException.ThrowIfNull(parameter);

        var index = Graph.Parameters.IndexOf(parameter);

        if (index < 0) {
            return false;
        }

        Run(
            "Remove Parameter",
            () => Graph.Parameters.RemoveAt(index),
            () => Graph.Parameters.Insert(index, parameter)
        );

        return true;
    }

    /// <summary>Records an arbitrary edit to the graph, undoably, by remembering the text.</summary>
    /// <param name="name">What the undo history calls it.</param>
    /// <param name="change">What to do.</param>
    /// <remarks>
    ///     ⚠ <b>The escape hatch for the dozens of scalar fields, and it is honest about its cost.</b>
    ///     A state has six settable members, a transition seven and a motion eight; a command type per
    ///     member would be twenty-one classes that each have to agree about what "changed" means.
    ///     Re-parsing the document is O(the file), the file is kilobytes, and it happens once per
    ///     committed field rather than per keystroke.
    /// </remarks>
    public void Edit(string name, Action change) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(change);

        var before = ToYaml();

        change();

        var after = ToYaml();

        if (string.Equals(before, after, StringComparison.Ordinal)) {
            return;
        }

        Stack.Execute(
            new DelegateCommand(
                name,
                _ => Apply(after),
                _ => Apply(before)
            )
        );
    }

    void Apply(string yaml) {
        Graph = YamlSerializer.Parse<AnimationGraphAsset>(yaml);
        Changed?.Invoke(this);
    }

    /// <summary>Compiles the graph and keeps what it said.</summary>
    /// <returns>The artefact, or <see langword="null" /> when nothing could be built.</returns>
    public AnimationGraphArtefact? Compile() {
        Artefact = AnimationGraphCompiler.Build(Graph);
        Diagnostics = Artefact.Diagnostics;

        return Artefact;
    }

    /// <summary>The graph as it would be written, without writing it.</summary>
    /// <returns>The YAML.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(Graph);

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, ToYaml());

    static string Unique(string wanted, IEnumerable<string> taken) =>
        Input.InputActionsDocument.Unique(wanted, taken);

    void Run(string name, Action apply, Action revert) {
        Stack.Execute(
            new DelegateCommand(
                name,
                _ => {
                    apply();
                    Changed?.Invoke(this);
                },
                _ => {
                    revert();
                    Changed?.Invoke(this);
                }
            )
        );
    }
}
