// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Tests;

/// <summary>
///     Node types for the framework's own tests, and the only ones that exercise the generator over a
///     class it has never seen.
/// </summary>
/// <remarks>
///     Deliberately not the shader graph's. What is under test here is the framework — the port
///     model, the typing, the binding — and borrowing a real library would mean a change to a maths
///     node could fail a test about cycle detection.
/// </remarks>
[Node("Test/Constant", Summary = "A number.")]
public sealed partial class TestConstantNode : Node {
    /// <summary>The number.</summary>
    [Output(Name = "Out")]
    public Scalar Out;
}

/// <summary>Two dynamically typed inputs and a dynamic output: the shape typing is about.</summary>
[Node("Test/Combine", Preview = true, Summary = "Two values, one result.")]
public sealed partial class TestCombineNode : Node {
    /// <summary>The first.</summary>
    [Input]
    public DynamicVector A = 0.25f;

    /// <summary>The second.</summary>
    [Input]
    public DynamicVector B;

    /// <summary>The result.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;
}

/// <summary>A fixed three-lane output, for something to resolve a dynamic port against.</summary>
[Node("Test/Vector", Summary = "A float3.")]
public sealed partial class TestVectorNode : Node {
    /// <summary>The vector.</summary>
    [Output(Name = "Out")]
    public Float3 Out;
}

/// <summary>A four-lane one, so the widest-wins rule has something to win over.</summary>
[Node("Test/Colour", Summary = "A float4.")]
public sealed partial class TestColourNode : Node {
    /// <summary>The colour.</summary>
    [Output(Name = "Out")]
    public Float4 Out;
}

/// <summary>A texture, which is what a dynamic port must refuse.</summary>
[Node("Test/Texture", Summary = "A texture.")]
public sealed partial class TestTextureNode : Node {
    /// <summary>The texture.</summary>
    [Output(Name = "Out")]
    public Texture Out;
}

/// <summary>An input with a named port and a declared vector default.</summary>
[Node("Test/Named", Summary = "A port whose name is not its field's.")]
public sealed partial class TestNamedNode : Node {
    /// <summary>The value.</summary>
    [Input(Name = "Base Colour", Default = [0.1f, 0.2f, 0.3f], Summary = "What it starts as.")]
    public Float3 BaseColour;

    /// <summary>The value again.</summary>
    [Output(Name = "Out")]
    public Float3 Out;
}
