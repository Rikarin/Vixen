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
///         travels as the name or key that library published, which is what the artefact maps
///         supply.
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

    /// <summary>The artefact key each imported function was resolved by, which is not its name.</summary>
    /// <remarks>
    ///     A key, and kept, because a library built against another records a call into it by the
    ///     key that library published — while the name the function carries here is whatever was
    ///     free in this module.
    /// </remarks>
    internal IReadOnlyDictionary<IrFunction, string> ImportedFunctionNames { get; }

    /// <summary>The artefact key each imported struct was resolved by, which is not its name.</summary>
    internal IReadOnlyDictionary<IrStructType, string> ImportedStructKeys { get; }

    /// <summary>
    ///     The structs whose <em>name</em> is their identity: tuples and monomorphised generics.
    /// </summary>
    /// <remarks>
    ///     ⚠ These are exempt from qualification, and the exemption is load-bearing rather than an
    ///     optimisation. A tuple has no declaration to match on, so two libraries' <c>(float,
    ///     float)</c> must reach one struct object or a library function's return type stops
    ///     matching the caller's local — <c>Lowerer.LowerTuple</c> says exactly that. A
    ///     monomorphised generic is named the same way, from its arguments.
    /// </remarks>
    internal IReadOnlySet<IrStructType> StructuralStructs { get; }

    internal LoweringResult(
        IrModule module,
        IReadOnlyDictionary<(Symbol Member, BoundBodyKind Kind), IrFunction> functions,
        IReadOnlyDictionary<NamedTypeSymbol, IrStructType> structs,
        IReadOnlySet<IrFunction> importedFunctions,
        IReadOnlySet<IrStructType> importedStructs,
        IReadOnlyDictionary<IrFunction, string> importedFunctionNames,
        IReadOnlyDictionary<IrStructType, string> importedStructKeys,
        IReadOnlySet<IrStructType> structuralStructs
    ) {
        Module = module;
        Functions = functions;
        Structs = structs;
        ImportedFunctions = importedFunctions;
        ImportedStructs = importedStructs;
        ImportedFunctionNames = importedFunctionNames;
        ImportedStructKeys = importedStructKeys;
        StructuralStructs = structuralStructs;
    }

    /// <summary>
    ///     The artefact key for an imported function, falling back to the name for one this
    ///     compilation lowered. See <c>LibraryBuilder.Builder.Key</c>, which is where the fallback
    ///     is replaced by a key derived from the declaration.
    /// </summary>
    internal string ArtefactName(IrFunction function) =>
        ImportedFunctionNames.GetValueOrDefault(function) ?? function.Name;

    /// <summary>
    ///     The artefact key for a struct: the one the library it was linked from gave it, or one
    ///     this module coins by qualifying the name with its own.
    /// </summary>
    /// <remarks>
    ///     ⚠ Qualified, because the bare name was not an identity. Two libraries that each declared
    ///     a <c>struct Shape</c> became one object in any consumer that referenced both, carrying
    ///     the first one's fields — <c>RVN3010</c> when the widths differed and nothing at all when
    ///     they matched, at which point a read of <c>height</c> was a read of somebody else's
    ///     <c>drag</c>. <see cref="StructuralStructs" /> is exempt, and says why.
    /// </remarks>
    internal string ArtefactKey(IrStructType structType) =>
        ImportedStructKeys.GetValueOrDefault(structType)
        ?? (StructuralStructs.Contains(structType) ? structType.Name : Qualified(structType.Name));

    /// <summary>The readable name to record for a struct this module lowered.</summary>
    /// <remarks>
    ///     Bare, and separate from <see cref="ArtefactKey" />, for the reason a function's name is:
    ///     the GLSL a frame debugger shows should still say <c>Shape</c>.
    /// </remarks>
    internal static string ArtefactStructName(IrStructType structType) => structType.Name;

    /// <summary>This module's name and a struct's, which together name one declaration.</summary>
    /// <remarks>
    ///     <c>::</c> rather than a dot, because a dot already separates a package from a type and a
    ///     key that reused it could be read as either. Matched, never parsed — except by the
    ///     decoder's placeholder, which takes the half after it for a readable name.
    /// </remarks>
    string Qualified(string name) => $"{Module.Name}::{name}";
}
