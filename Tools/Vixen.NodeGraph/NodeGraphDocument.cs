// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.NodeGraph;

/// <summary>One node, as a file holds it.</summary>
[DataContract("GraphNode")]
public sealed record GraphNodeAsset {
    /// <summary>Its identity within the graph.</summary>
    public int Id { get; init; }

    /// <summary>Which node type, by path.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Where it sits on the canvas, horizontally.</summary>
    /// <remarks>
    ///     Two floats rather than a <see cref="Vector2" />, because this is a file a person reads and
    ///     merges. A nested mapping for two numbers is two more lines per node in every diff, and the
    ///     model keeps the vector — this is the shape written down, not the shape worked with.
    /// </remarks>
    public float X { get; init; }

    /// <summary>And vertically.</summary>
    public float Y { get; init; }

    /// <summary>The inline values its unconnected inputs were given, port name to lanes.</summary>
    public Dictionary<string, float[]> Values { get; init; } = [];

    /// <summary>The inline texts its unconnected inputs were given, port name to text.</summary>
    /// <remarks>See <see cref="GraphNode.Texts" /> for why a name is not a lane of float.</remarks>
    public Dictionary<string, string> Texts { get; init; } = [];

    /// <summary>Whether it is switched off. See <see cref="GraphNode.Bypassed" />.</summary>
    public bool Bypassed { get; init; }
}

/// <summary>One connection, as a file holds it.</summary>
/// <remarks>
///     Four fields rather than two nested pairs, because this is a line in a diff. A reviewer looking
///     at a changed graph should be able to read which wire moved without unfolding two levels of
///     mapping, and a merge conflict in a flat record is one a person can resolve.
/// </remarks>
[DataContract("GraphEdge")]
public sealed record GraphEdgeAsset {
    /// <summary>The node the value leaves.</summary>
    public int FromNode { get; init; }

    /// <summary>Which of its outputs.</summary>
    public string FromPort { get; init; } = string.Empty;

    /// <summary>The node it arrives at.</summary>
    public int ToNode { get; init; }

    /// <summary>Which of its inputs.</summary>
    public string ToPort { get; init; } = string.Empty;
}

/// <summary>A group box, as a file holds it.</summary>
[DataContract("GraphGroup")]
public sealed record GraphGroupAsset {
    /// <summary>Its identity within the graph, or zero in a file written before groups had one.</summary>
    public int Id { get; init; }

    /// <summary>What the group is called.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Which nodes are in it.</summary>
    public int[] Nodes { get; init; } = [];

    /// <summary>The colour its frame is drawn in, as <c>#rrggbb</c>, or empty for the view's own choice.</summary>
    public string Color { get; init; } = string.Empty;

    /// <summary>What the nodes in it do together.</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>A sticky note, as a file holds it.</summary>
[DataContract("GraphComment")]
public sealed record GraphCommentAsset {
    /// <summary>Its identity within the graph, or zero in a file written before notes had one.</summary>
    public int Id { get; init; }

    /// <summary>What it says.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Where it sits, horizontally.</summary>
    public float X { get; init; }

    /// <summary>And vertically.</summary>
    public float Y { get; init; }

    /// <summary>How wide it is.</summary>
    public float Width { get; init; }

    /// <summary>How tall.</summary>
    public float Height { get; init; }
}

/// <summary>One port of a graph's sub-graph interface, as a file holds it.</summary>
/// <remarks>
///     Written down rather than derived from the boundary nodes, for the reason
///     <see cref="NodeGraphModel.Interface" /> gives: a signature that came and went with a node
///     would change under every containing graph when somebody deleted one.
/// </remarks>
[DataContract("GraphPort")]
public sealed record GraphPortAsset {
    /// <summary>What the port is called. An edge in a containing graph names it by this.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Which way it faces, as seen from a graph containing this one.</summary>
    public PortDirection Direction { get; init; }

    /// <summary>What it carries.</summary>
    public PortKind Kind { get; init; }

    /// <summary>What an unconnected input takes, one float per lane.</summary>
    public float[] Default { get; init; } = [];

    /// <summary>One line saying what it means.</summary>
    public string Summary { get; init; } = string.Empty;
}

/// <summary>One knob a graph exposes, as a file holds it.</summary>
/// <remarks>
///     ⚠ <b>Written down rather than derived, for <see cref="GraphPortAsset" />'s reason.</b> A
///     parameter list read off whichever nodes happen to mention a name would change under every
///     containing graph the moment somebody deleted a node — and a published graph's knobs are its
///     signature quite as much as its ports are.
/// </remarks>
[DataContract("GraphParameter")]
public sealed record GraphParameterAsset {
    /// <summary>What the parameter is called. An expression spells it and a containing node stores it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>What it is worth when nobody overrides it, as text.</summary>
    public string Default { get; init; } = string.Empty;

    /// <summary>One line saying what it means.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>How its text is read.</summary>
    public SettingKind Kind { get; init; }

    /// <summary>The bottom of its range, or negative infinity for none.</summary>
    public float Minimum { get; init; } = float.NegativeInfinity;

    /// <summary>The top of it.</summary>
    public float Maximum { get; init; } = float.PositiveInfinity;

    /// <summary>Which section of an inspector it belongs to.</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>Every value it may hold, or empty when it may hold any name.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The member <see cref="SettingDefinition.Accepted" /> has had since
    ///         <a href="https://github.com/Rikarin/Vixen/issues/964">#964</a> and this shape had not, so
    ///         a graph's own knob could not be a choice however the model spelled it.
    ///     </b>
    ///     A
    ///     <see cref="SettingKind.Text" /> parameter with a list is what a published texture graph
    ///     declares to expose an inner node's setting
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/1060">#1060</a>); one without a list is
    ///     what every file written before this member has, which is why the two mean different
    ///     things and the list rather than the kind is what says so.
    /// </remarks>
    public string[] Accepted { get; init; } = [];

    /// <summary>What an inspector calls it, or empty for its name.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Which control an inspector draws for it.</summary>
    public SettingWidget Widget { get; init; }

    /// <summary>The parameter whose value decides whether this one is shown, or empty for always.</summary>
    /// <remarks>
    ///     Three flat fields rather than one nested <see cref="SettingCondition" />, for
    ///     <see cref="GraphEdgeAsset" />'s reason: this is a line in a diff.
    /// </remarks>
    public string VisibleIf { get; init; } = string.Empty;

    /// <summary>The value <see cref="VisibleIf" /> is compared with.</summary>
    public string VisibleValue { get; init; } = string.Empty;

    /// <summary>True to show this one while <see cref="VisibleIf" /> does not hold the value.</summary>
    public bool VisibleNegated { get; init; }
}

/// <summary>A named set of parameter values, as a file holds it. See <see cref="GraphPreset" />.</summary>
[DataContract("GraphPreset")]
public sealed record GraphPresetAsset {
    /// <summary>What the preset is called.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>What each parameter is set to, by name.</summary>
    public Dictionary<string, string> Values { get; init; } = [];
}

/// <summary>
///     A whole graph, as a file holds it.
/// </summary>
/// <remarks>
///     <para>
///         <b>A separate shape from <see cref="NodeGraphModel" />, on purpose.</b> The model is a
///         thing with invariants — an input takes one edge, a cycle cannot be made — and enforcing
///         those while loading half a file is not possible. The document has no invariants at all: it
///         is what was written down, including whatever a hand edit or a bad merge left. Turning one
///         into the other is where the checking happens, and where a file that has gone wrong
///         produces a diagnostic rather than a half-built model.
///     </para>
///     <para>
///         <b>This assembly does not choose a file format.</b> These are <c>[DataContract]</c>
///         records described by the reflection generator, so a caller writes them as YAML with
///         <c>Vixen.Core.Yaml</c> or bakes them with the binary serializer, and neither parser is
///         linked by anything that only wants to compile a graph. The same arrangement the compositor
///         asset uses, for the same reason.
///     </para>
/// </remarks>
[DataContract("NodeGraph")]
public sealed record NodeGraphAsset {
    /// <summary>The file shape's version.</summary>
    /// <remarks>
    ///     Written now so that a reader can refuse a file from the future with a sentence rather than
    ///     a stack trace. There is one version and nothing yet reads it, which is the cheapest moment
    ///     to add it.
    /// </remarks>
    public int Version { get; init; } = 1;

    /// <summary>What the graph is called.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Its nodes.</summary>
    public GraphNodeAsset[] Nodes { get; init; } = [];

    /// <summary>Its edges.</summary>
    public GraphEdgeAsset[] Edges { get; init; } = [];

    /// <summary>Its group boxes.</summary>
    public GraphGroupAsset[] Groups { get; init; } = [];

    /// <summary>Its sticky notes.</summary>
    public GraphCommentAsset[] Comments { get; init; } = [];

    /// <summary>The ports it has when another graph contains it as a node.</summary>
    public GraphPortAsset[] Interface { get; init; } = [];

    /// <summary>What the graph itself is set to — a texture graph's base resolution and seed.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Until <a href="https://github.com/Rikarin/Vixen/issues/719">#719</a> there was no such
    ///         field, and a <c>.vxtexgraph</c> therefore came back at whatever resolution and seed the
    ///         host that opened it happened to default to.
    ///     </b>
    ///     A seed that is not in the file is a seed
    ///     that changes between machines, which doc 48 § D5 says plainly is not a source asset.
    /// </remarks>
    public Dictionary<string, string> Settings { get; init; } = [];

    /// <summary>The knobs it exposes when another graph, or a bake, contains it.</summary>
    public GraphParameterAsset[] Parameters { get; init; } = [];

    /// <summary>Named sets of values for <see cref="Parameters" />.</summary>
    public GraphPresetAsset[] Presets { get; init; } = [];

    /// <summary>What it makes and how it is meant to be used. See <see cref="GraphInfo" />.</summary>
    /// <remarks>
    ///     Four flat fields rather than one nested record, for <see cref="GraphEdgeAsset" />'s reason:
    ///     a changed description or a new tag is one line in a diff.
    /// </remarks>
    public string Description { get; init; } = string.Empty;

    /// <summary>Who made it.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Words to find it by.</summary>
    public string[] Tags { get; init; } = [];

    /// <summary>The width and height one tile stands for, in centimetres, or empty when it says nothing.</summary>
    public float[] PhysicalSize { get; init; } = [];
}

/// <summary>Between the model and the file shape.</summary>
public static class NodeGraphDocument {
    /// <summary>The current file shape's version.</summary>
    public const int Version = 1;

    /// <summary>Writes a model out.</summary>
    /// <param name="graph">The model.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    public static NodeGraphAsset Save(NodeGraphModel graph) {
        ArgumentNullException.ThrowIfNull(graph);

        graph.IdentifyFurniture();

        List<GraphNodeAsset> nodes = [];

        foreach (var node in graph.Nodes) {
            nodes.Add(
                new() {
                    Id = node.Id.Value,
                    Type = node.Type,
                    X = node.Position.X,
                    Y = node.Position.Y,
                    Values = new(node.Values),
                    Texts = new(node.Texts),
                    Bypassed = node.Bypassed
                }
            );
        }

        List<GraphEdgeAsset> edges = [];

        foreach (var edge in graph.Edges) {
            edges.Add(
                new() {
                    FromNode = edge.From.Node.Value,
                    FromPort = edge.From.Port,
                    ToNode = edge.To.Node.Value,
                    ToPort = edge.To.Port
                }
            );
        }

        List<GraphGroupAsset> groups = [];

        foreach (var group in graph.Groups) {
            groups.Add(
                new() {
                    Id = group.Id,
                    Title = group.Title,
                    Nodes = [..group.Nodes.Select(static id => id.Value)],
                    Color = group.Color,
                    Description = group.Description
                }
            );
        }

        List<GraphCommentAsset> comments = [];

        foreach (var comment in graph.Comments) {
            comments.Add(
                new() {
                    Id = comment.Id,
                    Text = comment.Text,
                    X = comment.Position.X,
                    Y = comment.Position.Y,
                    Width = comment.Size.X,
                    Height = comment.Size.Y
                }
            );
        }

        List<GraphPortAsset> ports = [];

        foreach (var port in graph.Interface) {
            ports.Add(
                new() {
                    Name = port.Name,
                    Direction = port.Direction,
                    Kind = port.Kind,
                    Default = [..port.Default],
                    Summary = port.Summary
                }
            );
        }

        List<GraphParameterAsset> parameters = [];

        foreach (var parameter in graph.Parameters) {
            parameters.Add(
                new() {
                    Name = parameter.Name,
                    Default = parameter.Default,
                    Summary = parameter.Summary,
                    Kind = parameter.Kind,
                    Minimum = parameter.Minimum,
                    Maximum = parameter.Maximum,
                    Group = parameter.Group,
                    Accepted = [..parameter.Accepted],
                    Label = parameter.Label,
                    Widget = parameter.Widget,
                    VisibleIf = parameter.VisibleIf?.Setting ?? string.Empty,
                    VisibleValue = parameter.VisibleIf?.Value ?? string.Empty,
                    VisibleNegated = parameter.VisibleIf?.Negated ?? false
                }
            );
        }

        List<GraphPresetAsset> presets = [];

        foreach (var preset in graph.Presets) {
            presets.Add(new() { Name = preset.Name, Values = new(preset.Values, StringComparer.Ordinal) });
        }

        return new() {
            Version = Version,
            Name = graph.Name,
            Nodes = [..nodes],
            Edges = [..edges],
            Groups = [..groups],
            Comments = [..comments],
            Interface = [..ports],
            Settings = new(graph.Settings, StringComparer.Ordinal),
            Parameters = [..parameters],
            Presets = [..presets],
            Description = graph.Info.Description,
            Author = graph.Info.Author,
            Tags = [..graph.Info.Tags],
            PhysicalSize = graph.Info.PhysicalSize == default
                ? []
                : [graph.Info.PhysicalSize.X, graph.Info.PhysicalSize.Y]
        };
    }

    /// <summary>Reads a document into a model.</summary>
    /// <param name="asset">The document.</param>
    /// <param name="diagnostics">Everything that had to be dropped or repaired, in file order.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="asset" /> is null.</exception>
    /// <exception cref="NotSupportedException">The file is from a later version than this reader.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>It repairs rather than refuses, and says what it repaired.</b> An edge naming a node
    ///         that is not in the file is dropped; a duplicated identity is dropped; an edge arriving
    ///         at an input that already has one replaces it. Every one of those is what a bad merge
    ///         produces, and a graph editor that will not open the file is an editor that cannot be
    ///         used to fix it. What it does <i>not</i> repair is a file from a version it does not
    ///         know, because there it cannot tell what the bytes mean.
    ///     </para>
    ///     <para>
    ///         An edge that would close a cycle is dropped for the same reason: the model refuses
    ///         cycles by construction, so a file containing one has to lose an edge somewhere, and
    ///         losing the later one with a diagnostic is the version an author can act on.
    ///     </para>
    /// </remarks>
    public static NodeGraphModel Load(NodeGraphAsset asset, out IReadOnlyList<NodeDiagnostic> diagnostics) {
        ArgumentNullException.ThrowIfNull(asset);

        if (asset.Version > Version) {
            throw new NotSupportedException(
                $"This graph was written by version {asset.Version} and this reader understands {Version}. "
                + "Opening it would silently drop whatever the newer version added."
            );
        }

        List<NodeDiagnostic> found = [];
        var graph = new NodeGraphModel { Name = asset.Name };

        foreach (var (key, value) in asset.Settings) {
            // ⚠ Not validated here, deliberately: what a key means belongs to the front end that
            // reads it, and a reader that refused an unrecognised one would refuse a file written by
            // a newer version of the very editor that opened it.
            graph.Settings[key] = value;
        }

        HashSet<string> knobs = new(StringComparer.Ordinal);

        foreach (var entry in asset.Parameters) {
            if (string.IsNullOrWhiteSpace(entry.Name)) {
                found.Add(
                    new(
                        "NG0104",
                        "A parameter of the graph has no name, and an override is stored by name.",
                        NodeId.None
                    )
                );

                continue;
            }

            if (!knobs.Add(entry.Name)) {
                found.Add(
                    new(
                        "NG0104",
                        $"The graph declares two parameters called '{entry.Name}'. The second was dropped.",
                        NodeId.None
                    )
                );

                continue;
            }

            graph.Parameters.Add(
                new(
                    entry.Name,
                    entry.Default,
                    entry.Summary,
                    entry.Kind,
                    entry.Minimum,
                    entry.Maximum,
                    entry.Group,
                    [..entry.Accepted],
                    entry.Label,
                    entry.Widget,
                    entry.VisibleIf.Length > 0 ? new(entry.VisibleIf, entry.VisibleValue, entry.VisibleNegated) : null
                )
            );
        }

        graph.Info = InfoOf(asset, found);

        HashSet<string> presets = new(StringComparer.Ordinal);

        foreach (var entry in asset.Presets) {
            if (string.IsNullOrWhiteSpace(entry.Name) || !presets.Add(entry.Name)) {
                found.Add(
                    new(
                        "NG0105",
                        string.IsNullOrWhiteSpace(entry.Name)
                            ? "A preset of the graph has no name, and a preset is chosen by name. It was dropped."
                            : $"The graph declares two presets called '{entry.Name}'. The second was dropped.",
                        NodeId.None
                    )
                );

                continue;
            }

            GraphPreset preset = new() { Name = entry.Name };

            foreach (var (name, value) in entry.Values) {
                preset.Values[name] = value;
            }

            graph.Presets.Add(preset);
        }

        // Before the nodes, because the boundary nodes a sub-graph's entry and exit are drawn from
        // read it — see SubGraphs. A duplicate is dropped rather than kept: two ports of one name is
        // a signature where a containing graph's edge does not say which it arrives at.
        HashSet<(string Name, PortDirection Direction)> declared = [];

        foreach (var entry in asset.Interface) {
            if (string.IsNullOrWhiteSpace(entry.Name)) {
                found.Add(
                    new(
                        "NG0103",
                        "A port of the graph's interface has no name, and an edge is stored by port name.",
                        NodeId.None
                    )
                );

                continue;
            }

            if (!declared.Add((entry.Name, entry.Direction))) {
                found.Add(
                    new(
                        "NG0103",
                        $"The graph's interface declares two {entry.Direction} ports called '{entry.Name}'. The second was dropped.",
                        NodeId.None
                    )
                );

                continue;
            }

            graph.Interface.Add(new(entry.Name, entry.Direction, entry.Kind, [..entry.Default], entry.Summary));
        }

        // The identities are the file's, not fresh ones. See NodeGraphModel.Add(NodeId, ...).
        foreach (var entry in asset.Nodes) {
            var id = new NodeId(entry.Id);

            if (!id.IsValid) {
                found.Add(new("NG0100", $"A node in the file has identity {entry.Id}, which is not one.", id));

                continue;
            }

            if (graph.TryGet(id, out _)) {
                found.Add(new("NG0100", $"Two nodes in the file claim {id}. The second was dropped.", id));

                continue;
            }

            var node = graph.Add(id, entry.Type, new(entry.X, entry.Y));

            node.Bypassed = entry.Bypassed;

            foreach (var (port, value) in entry.Values) {
                node.SetValue(port, value);
            }

            foreach (var (port, value) in entry.Texts) {
                node.SetText(port, value);
            }
        }

        foreach (var entry in asset.Edges) {
            var from = new NodeId(entry.FromNode);
            var to = new NodeId(entry.ToNode);

            if (!graph.TryGet(from, out _)) {
                found.Add(new("NG0101", $"An edge leaves {from}, which is not in the file.", from));

                continue;
            }

            if (!graph.TryGet(to, out _)) {
                found.Add(new("NG0101", $"An edge arrives at {to}, which is not in the file.", to));

                continue;
            }

            try {
                graph.Connect(new(from, entry.FromPort), new(to, entry.ToPort));
            } catch (ArgumentException exception) {
                found.Add(new("NG0102", $"An edge was dropped: {exception.Message}", to, entry.ToPort));
            }
        }

        foreach (var entry in asset.Groups) {
            var group = new GraphGroup {
                Id = entry.Id, Title = entry.Title, Color = entry.Color, Description = entry.Description
            };

            foreach (var member in entry.Nodes) {
                var id = new NodeId(member);

                if (graph.TryGet(id, out _)) {
                    group.Nodes.Add(id);
                }
            }

            graph.Groups.Add(group);
        }

        foreach (var entry in asset.Comments) {
            graph.Comments.Add(
                new() {
                    Id = entry.Id,
                    Text = entry.Text,
                    Position = new(entry.X, entry.Y),
                    Size = new(entry.Width, entry.Height)
                }
            );
        }

        // After both lists, because a file written before these had identities holds zeros, and a
        // bad merge can hold one number twice.
        graph.IdentifyFurniture();

        diagnostics = found;

        return graph;
    }

    /// <summary>What a file says about its graph, with blank and repeated tags dropped.</summary>
    /// <remarks>
    ///     A physical size that is not two positive numbers is dropped with <c>NG0106</c> rather than
    ///     kept, because a reader that takes a tile for zero or minus one centimetre across would
    ///     place the material at a size nobody wrote. A tag is dropped quietly: a blank or repeated
    ///     word finds nothing a kept one does not.
    /// </remarks>
    static GraphInfo InfoOf(NodeGraphAsset asset, List<NodeDiagnostic> found) {
        List<string> tags = [];

        foreach (var tag in asset.Tags) {
            var word = tag.Trim();

            if (word.Length > 0 && !tags.Contains(word, StringComparer.OrdinalIgnoreCase)) {
                tags.Add(word);
            }
        }

        Vector2 size = default;

        if (asset.PhysicalSize is [var width, var height]
            && float.IsFinite(width)
            && float.IsFinite(height)
            && width > 0f
            && height > 0f) {
            size = new(width, height);
        } else if (asset.PhysicalSize.Length > 0) {
            found.Add(
                new(
                    "NG0106",
                    "The graph's physical size is not a width and a height above zero, in centimetres. It was dropped.",
                    NodeId.None
                )
            );
        }

        return new(asset.Description, asset.Author, tags, size);
    }
}
