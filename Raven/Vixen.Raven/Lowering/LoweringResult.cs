// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Binding;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Lowering;

/// <summary>
///     A lowered module, plus the links between the symbols that produced it and the IR they became.
/// </summary>
/// <remarks>
///     <para>
///         The links are internal, and that is the point: an <see cref="IrModule" /> holds no
///         symbols, which is what lets a backend be written against the IR alone. Writing a
///         <c>.rvnlib</c> is the one job that needs both halves at once — a library's whole value is
///         that it says "this method's body is that function" — so the map is carried here rather
///         than smuggled into the IR.
///     </para>
///     <para>
///         The imported sets say which entities came from a referenced library. A library exports
///         only what its own compilation declared; a call into a library it was built against
///         travels as a name, which is what the artefact-name maps supply.
///     </para>
/// </remarks>
public sealed class LoweringResult {
    /// <summary>The lowered module, which is what a backend consumes.</summary>
    public IrModule Module { get; }

    /// <summary>Which IR function each member's body lowered to.</summary>
    internal IReadOnlyDictionary<(Symbol Member, BoundBodyKind Kind), IrFunction> Functions { get; }

    /// <summary>Which IR struct each aggregate type lowered to.</summary>
    internal IReadOnlyDictionary<NamedTypeSymbol, IrStructType> Structs { get; }

    /// <summary>Functions linked in from a referenced library rather than lowered here.</summary>
    internal IReadOnlySet<IrFunction> ImportedFunctions { get; }

    /// <summary>Structs linked in from a referenced library rather than lowered here.</summary>
    internal IReadOnlySet<IrStructType> ImportedStructs { get; }

    /// <summary>The name the artefact gave each imported function, which may not be the one it carries.</summary>
    internal IReadOnlyDictionary<IrFunction, string> ImportedFunctionNames { get; }

    /// <summary>The name the artefact gave each imported struct.</summary>
    internal IReadOnlyDictionary<IrStructType, string> ImportedStructNames { get; }

    internal LoweringResult(
        IrModule module,
        IReadOnlyDictionary<(Symbol Member, BoundBodyKind Kind), IrFunction> functions,
        IReadOnlyDictionary<NamedTypeSymbol, IrStructType> structs,
        IReadOnlySet<IrFunction> importedFunctions,
        IReadOnlySet<IrStructType> importedStructs,
        IReadOnlyDictionary<IrFunction, string> importedFunctionNames,
        IReadOnlyDictionary<IrStructType, string> importedStructNames
    ) {
        Module = module;
        Functions = functions;
        Structs = structs;
        ImportedFunctions = importedFunctions;
        ImportedStructs = importedStructs;
        ImportedFunctionNames = importedFunctionNames;
        ImportedStructNames = importedStructNames;
    }

    /// <summary>The artefact name for a function: the one it carries, or the one its library gave it.</summary>
    internal string ArtefactName(IrFunction function) =>
        ImportedFunctionNames.GetValueOrDefault(function) ?? function.Name;

    /// <summary>The artefact name for a struct, on the same terms.</summary>
    internal string ArtefactName(IrStructType structType) =>
        ImportedStructNames.GetValueOrDefault(structType) ?? structType.Name;
}
