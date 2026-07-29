// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Shaders.Generators;

/// <summary>
///     The parts of Raven's <c>.reflect.json</c> this generator reads.
/// </summary>
/// <remarks>
///     <para>
///         Hand-modelled rather than shared with <c>Vixen.Raven.Reflection</c>, and that is forced
///         rather than chosen: a source generator targets netstandard2.1 and runs inside the C#
///         compiler, while <c>Vixen.Raven</c> targets net10.0 — so the generator could not reference
///         the compiler even if the dependency were desirable. What crosses between them is the
///         JSON, which is a schema and not a type.
///     </para>
///     <para>
///         Only the fields used are declared. An unknown field is skipped by the reader, so Raven can
///         add to the schema without breaking a build here — the coupling is to the names read, not
///         to the shape of the document.
///     </para>
/// </remarks>
sealed class ShaderReflection {
    public List<DescriptorSet> Sets { get; } = [];
    public List<VertexInput> VertexInputs { get; } = [];
    public List<Parameter> Parameters { get; } = [];
    public List<Permutation> Permutations { get; } = [];
    public List<string> Stages { get; } = [];
    public List<string> UsedPermutationKeys { get; } = [];
}

/// <summary>One vertex attribute: what the host feeds the vertex stage, and where.</summary>
/// <remarks>
///     <para>
///         The list <em>is</em> the vertex layout — a value the pipeline supplies rather than the
///         host is absent from it rather than listed with a placeholder — so its length is the
///         attribute count and nothing here needs to know which parameters were built-ins.
///     </para>
///     <para>
///         Raven reports a type with each, and it is deliberately not read: a format and an offset
///         come from the host's own vertex struct, which is the half of a vertex layout the shader
///         has no opinion about. Only the fields used are declared, as the rest of this file is.
///     </para>
/// </remarks>
sealed class VertexInput {
    public int Location { get; set; }
    public string Name { get; set; } = string.Empty;
}

sealed class DescriptorSet {
    public int Set { get; set; }
    public List<Binding> Bindings { get; } = [];
}

sealed class Binding {
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int Size { get; set; }
    public bool IsWritable { get; set; }
    public List<Member> Members { get; } = [];
}

sealed class Member {
    public string Name { get; set; } = string.Empty;
    public DataType Type { get; set; } = new();
    public int Offset { get; set; }
    public int Size { get; set; }
    public int ArrayStride { get; set; }
    public int MatrixStride { get; set; }
}

sealed class Parameter {
    public string Name { get; set; } = string.Empty;
    public DataType Type { get; set; } = new();
    public int Set { get; set; }
    public int Binding { get; set; }
    public int Offset { get; set; }
    public int Size { get; set; }
    public int ArrayStride { get; set; }
    public int MatrixStride { get; set; }

    /// <summary>The author's initialiser as text, or empty. See <c>ParameterInfo.DefaultValue</c>.</summary>
    public string DefaultValue { get; set; } = string.Empty;
}

sealed class Permutation {
    public string Name { get; set; } = string.Empty;
    public DataType Type { get; set; } = new();
    public string DefaultValue { get; set; } = string.Empty;
}

sealed class DataType {
    public string Scalar { get; set; } = "Float";
    public int Rows { get; set; } = 1;
    public int Columns { get; set; } = 1;
    public int? ArrayLength { get; set; }
    public string? StructName { get; set; }
    public bool IsMatrix { get; set; }
    public bool IsArray { get; set; }
    public bool IsStruct { get; set; }
}
