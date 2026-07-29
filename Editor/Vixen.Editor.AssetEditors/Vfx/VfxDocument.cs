// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.VfxGraph;
using Vixen.Vfx;

namespace Vixen.Editor.AssetEditors.Vfx;

/// <summary>An effect, open for editing as a graph, with a simulation running beside it.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's E5 opens with "VFX graph reachable", and reachable is the whole of what was
///         missing.</b> The node library, the compiler and both targets were written for
///         <see cref="VfxGraphCompiler" /> and tested — what did not exist was a document, a factory
///         and a registration, so nothing in the editor could turn a double-click into an effect.
///         This is that, and it is the same shape <c>CompositorDocument</c> already had.
///     </para>
///     <para>
///         ⚠ <b>The file holds the graph, not the compiled effect.</b> A <c>.vxvfx</c> is a
///         <c>NodeGraphAsset</c> — nodes, edges, positions, the numbers an author typed — and
///         <see cref="Compile" /> turns it into the <see cref="VfxCompiledGraph" /> a simulation runs
///         and the shader a device runs. Saving the compiled form would throw away the layout and
///         make the document unopenable; saving both would be two files that can disagree.
///     </para>
///     <para>
///         ⚠ <b>The preview is a real simulation, not an approximation of one.</b>
///         <see cref="Preview" /> is a <see cref="VfxSystem" /> over the artefact this document just
///         produced, stepped by the panel — the same class a game runs. What the editor cannot yet do
///         is <i>draw</i> particles the way a game does, because that is a material and doc 14's
///         Phase 7 owns it; so <c>VfxPreviewView</c> projects the buffer itself and says what it is
///         showing. The half that would be wrong to fake is the simulation, and it is not faked.
///     </para>
///     <para>
///         ⚠ <b>A file this build cannot read opens empty and says why.</b>
///         <c>NodeGraphDocument.Load</c> repairs what it can and refuses a graph from a later
///         version outright; both arrive in <see cref="LoadDiagnostics" /> rather than as an
///         exception, so the panel that could show the problem is reachable.
///     </para>
/// </remarks>
public sealed class VfxDocument : EditorDocument {
    /// <summary>What a VFX graph is written as.</summary>
    public const string Extension = ".vxvfx";

    VfxSystem? preview;

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The graph.</summary>
    public NodeGraphModel Graph { get; }

    /// <summary>The node types this document is edited against.</summary>
    public NodeTypeRegistry Registry { get; }

    /// <summary>What reading the file had to say — repairs, and a refusal.</summary>
    public IReadOnlyList<NodeDiagnostic> LoadDiagnostics { get; } = [];

    /// <summary>What the last <see cref="Compile" /> had to say.</summary>
    public IReadOnlyList<NodeDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>What the last <see cref="Compile" /> produced, or <see langword="null" />.</summary>
    public VfxGraphArtefact? Artefact { get; private set; }

    /// <summary>The simulation the preview steps, or <see langword="null" /> until one compiles.</summary>
    public VfxSystem? Preview => preview;

    /// <summary>Raised after <see cref="Compile" /> has run.</summary>
    public event Action<VfxDocument>? Compiled;

    /// <summary>Opens an effect graph.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    /// <param name="registry">The node types, or <see langword="null" /> for this build's.</param>
    public VfxDocument(
        EditorProject project,
        AssetId asset,
        string path,
        NodeTypeRegistry? registry = null
    ) : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;
        Registry = registry ?? VfxNodeLibrary.Create();

        var text = AssetFile.Read(path);

        if (text.Trim().Length == 0) {
            // ⚠ A new effect is a spawner and an output rather than an empty canvas, and the reason
            // is the compiler's own first two diagnostics: a graph with no spawner produces nothing
            // at all, and one with no output has nothing to draw. Both are the first two nodes
            // anybody would add, and starting with them is the difference between a new file that
            // compiles and one that opens complaining.
            Graph = new() { Name = Path.GetFileNameWithoutExtension(path) };

            var spawn = Graph.Add("Vfx/Spawn/Rate", new(80f, 80f));
            var lifetime = Graph.Add("Vfx/Initialize/Lifetime", new(280f, 80f));
            var velocity = Graph.Add("Vfx/Initialize/Random Velocity", new(480f, 80f));
            var integrate = Graph.Add("Vfx/Update/Integrate", new(680f, 80f));
            var output = Graph.Add("Vfx/Output/Billboard", new(880f, 80f));

            NodeId[] chain = [spawn.Id, lifetime.Id, velocity.Id, integrate.Id, output.Id];

            for (var index = 1; index < chain.Length; index++) {
                Graph.Connect(new(chain[index - 1], "Out"), new(chain[index], "In"));
            }

            return;
        }

        try {
            var stored = YamlSerializer.Parse<NodeGraphAsset>(text);

            Graph = NodeGraphDocument.Load(stored, out var diagnostics);
            LoadDiagnostics = diagnostics;
        } catch (Exception exception) when (exception is YamlBindingException
            or YamlParseException or NotSupportedException) {
            Graph = new() { Name = Path.GetFileNameWithoutExtension(path) };
            LoadDiagnostics = [new("VF0000", exception.Message, NodeId.None)];
        }
    }

    /// <summary>Compiles the graph, and restarts the preview over what came out.</summary>
    /// <returns>The effect and its shader, or <see langword="null" /> when the graph is wrong.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Run when it is asked for, not on every edit.</b> Wiring a block produces a graph
    ///         that is briefly incomplete — a chain with a gap, an output nothing feeds — and
    ///         compiling on every change would fill the panel with complaints about a state the
    ///         author is halfway through leaving.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The old simulation is disposed before the new one exists.</b>
    ///         <c>ParticleBuffer</c> holds pooled arrays, and a compile that left the previous system
    ///         to the collector would leak one buffer per press of a button people press constantly.
    ///     </para>
    /// </remarks>
    public VfxGraphArtefact? Compile() {
        var result = new VfxGraphCompiler(Registry) { DefaultName = Title.Peek() }.Compile(Graph);

        Artefact = result.Artefact;
        Diagnostics = result.Diagnostics;

        preview?.Dispose();
        preview = Artefact is { } artefact ? new VfxSystem(artefact.Graph) : null;

        Compiled?.Invoke(this);
        return Artefact;
    }

    /// <summary>Starts the preview again from nothing, keeping the compiled effect.</summary>
    public void Restart() => preview?.Reset();

    /// <summary>The graph as this document would write it, without writing it.</summary>
    /// <returns>The YAML.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(NodeGraphDocument.Save(Graph));

    /// <summary>The compute shader the graph emits, for a host that wants the GPU half.</summary>
    /// <returns>The Raven source, or <see langword="null" /> when the graph does not compile.</returns>
    /// <remarks>
    ///     Doc 06's dual target, reachable from the editor: the same call that produced the effect
    ///     produced this, so there is no way for the two to have understood the graph differently.
    /// </remarks>
    public string? ShaderSource() => (Artefact ?? Compile())?.Shader.Source;

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, ToYaml());

    /// <inheritdoc />
    protected override void OnClosed() {
        base.OnClosed();

        preview?.Dispose();
        preview = null;
    }
}
