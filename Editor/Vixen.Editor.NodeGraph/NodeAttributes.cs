// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.NodeGraph;

/// <summary>Marks a class as a node type, and says where it appears in the create menu.</summary>
/// <param name="path">
///     The menu path, <c>Category/Title</c>, which is also the key the graph stores. A saved graph
///     names its nodes by this, so changing it renames a type and orphans every saved use of it.
/// </param>
/// <remarks>
///     <para>
///         <b>A node is an ordinary C# class, which is what makes the library extensible.</b> A
///         plugin adds nodes by adding classes; there is no table to register in and no switch to
///         extend. The generator reads the marked fields and writes the port metadata, so the
///         declaration and the metadata cannot drift apart — they are the same text.
///     </para>
///     <para>
///         The class has to be <c>partial</c>: the generator writes the half that binds the fields to
///         the graph's edges, and it writes it into the same class so a node's ports are its fields
///         rather than a dictionary it looks itself up in.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NodeAttribute(string path) : Attribute {
    /// <summary>The menu path, and the key a saved graph stores.</summary>
    public string Path { get; } = path;

    /// <summary>One line saying what the node is for. Shown in the create menu and on hover.</summary>
    public string Summary { get; init; } = "";

    /// <summary>Whether the node is worth drawing a preview thumbnail for.</summary>
    /// <remarks>
    ///     A hint to a view that does not exist yet, kept because it is a property of the node type
    ///     rather than of the view — a <c>Time</c> node has nothing to preview whatever is drawing it.
    /// </remarks>
    public bool Preview { get; init; }
}

/// <summary>Marks a field as an input port.</summary>
/// <remarks>
///     The field's declared type is the port's kind. Its initializer, if it has one, is the value the
///     port takes when nothing is connected — which is what lets a <c>Lerp</c> be dropped into a graph
///     and do something sensible before it is wired up.
/// </remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class InputAttribute : Attribute {
    /// <summary>What the port is called, if not the field's own name.</summary>
    /// <remarks>
    ///     For the cases where the port's name is not a C# identifier — <c>"UV"</c> reads better than
    ///     <c>Uv</c> — and for renaming a field without orphaning every saved edge, since a saved
    ///     graph stores port <i>names</i>.
    /// </remarks>
    public string Name { get; init; } = "";

    /// <summary>One line saying what the port means.</summary>
    public string Summary { get; init; } = "";

    /// <summary>The value the port takes when nothing is connected, one float per lane.</summary>
    /// <remarks>
    ///     Here as well as in the field initializer because a vector port cannot have one: the port
    ///     value types hold an <i>expression</i>, so <c>= 0.5f</c> is a conversion a scalar can
    ///     provide and a <c>float3</c> cannot. A scalar may use either and this wins, which keeps the
    ///     common case reading as ordinary C#.
    /// </remarks>
    public float[] Default { get; init; } = [];
}

/// <summary>Marks a field as an output port.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class OutputAttribute : Attribute {
    /// <summary>What the port is called, if not the field's own name.</summary>
    public string Name { get; init; } = "";

    /// <summary>One line saying what the port means.</summary>
    public string Summary { get; init; } = "";
}
