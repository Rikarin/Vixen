// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.IR;

/// <summary>
///     The whole compilation, lowered. This is the boundary the backends work
///     against: GLSL, SPIR-V, HLSL and Metal all consume an <see cref="IrModule" />
///     and none of them reads the bound tree or the syntax tree.
/// </summary>
public sealed class IrModule(string name) {
    readonly List<IrFunction> functions = [];
    readonly List<IrShader> shaders = [];
    readonly List<IrStructType> structs = [];

    public string Name { get; } = name;

    public IReadOnlyList<IrStructType> Structs => structs;

    /// <summary>Functions not owned by a shader — free functions and struct methods.</summary>
    public IReadOnlyList<IrFunction> Functions => functions;

    public IReadOnlyList<IrShader> Shaders => shaders;

    /// <summary>Every function in the module, shaders' included.</summary>
    public IEnumerable<IrFunction> AllFunctions => functions.Concat(shaders.SelectMany(s => s.Functions));

    public override string ToString() => Name;

    internal void Add(IrStructType structType) => structs.Add(structType);
    internal void Add(IrFunction function) => functions.Add(function);
    internal void Add(IrShader shader) => shaders.Add(shader);

    /// <summary>
    ///     Drops every struct <paramref name="keepStruct" /> rejects and every function
    ///     <paramref name="keepFunction" /> rejects, in place.
    /// </summary>
    /// <remarks>
    ///     Exists for linking: a referenced library's whole IR has to be present before any body
    ///     is lowered, because a body may call anything in it, but only what something reached
    ///     belongs in the output. Referencing a library must not enlarge a shader that uses one
    ///     function from it.
    /// </remarks>
    internal void Prune(Func<IrStructType, bool> keepStruct, Func<IrFunction, bool> keepFunction) {
        structs.RemoveAll(s => !keepStruct(s));
        functions.RemoveAll(f => !keepFunction(f));
    }
}
