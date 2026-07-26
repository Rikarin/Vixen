// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Artefacts;
using Vixen.Raven.Binding;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;
using Vixen.Raven.Symbols.Metadata;

namespace Vixen.Raven.Lowering;

/// <summary>
///     Linking referenced libraries' lowered IR into the module being built.
/// </summary>
/// <remarks>
///     <para>
///         This is the half of <c>.rvnlib</c> that makes a reference more than a type-check. The
///         symbol half lets the binder resolve a call into a library; this half gives that call an
///         <see cref="IrFunction" /> to point at, by rebuilding the library's functions in the
///         module being compiled and mapping each metadata symbol onto the function its body
///         lowered to when the library was built.
///     </para>
///     <para>
///         The consequence is that no other phase changes. <c>LowerCall</c> looks a callee up in the
///         same dictionary whether it came from source or from a library; <c>LowerType</c> finds a
///         library struct in the same map as a local one; both backends see one module of ordinary
///         IR. A reference costs nothing at runtime for the same reason <c>compose</c> does — it is
///         resolved before the backend runs.
///     </para>
/// </remarks>
public sealed partial class Lowerer {
    readonly Dictionary<IrFunction, string> importedFunctionNames = [];
    readonly HashSet<IrFunction> importedFunctions = [];
    readonly Dictionary<IrStructType, string> importedStructNames = [];
    readonly HashSet<IrStructType> importedStructs = [];

    /// <summary>
    ///     Imported structs by the name they carry in this module, for the types that are identified
    ///     structurally rather than by declaration.
    /// </summary>
    /// <remarks>
    ///     A nominal type is matched through its symbol, which is how a library struct reaches
    ///     <c>structs</c>. A tuple has no symbol to match on either side of the boundary — its
    ///     <c>TupleTypeSymbol</c> is constructed on demand — so it is matched by the name lowering
    ///     derives from its element types. See <c>LowerTuple</c>.
    /// </remarks>
    readonly Dictionary<string, IrStructType> importedStructsByName = new(StringComparer.Ordinal);

    /// <summary>
    ///     Decodes every referenced library's structs and prepares its functions.
    /// </summary>
    /// <returns>
    ///     The link to finish once the compilation's own function names are taken, or null when
    ///     nothing was referenced.
    /// </returns>
    ReferenceLink? LinkReferences(IReadOnlyList<NamedTypeSymbol> declaredTypes) {
        if (compilation.Metadata is not { Libraries.Count: > 0 } loader) {
            return null;
        }

        // Names the compilation is going to use, reserved before a library gets to keep one. Source
        // wins by construction rather than by luck: a library's `Saturate` gives way to the
        // shader's, which is the same precedence a shadowed type name gets.
        var link = new ReferenceLink(this, loader, ReserveDeclaredNames(declaredTypes));
        link.LinkStructs();
        return link;
    }

    /// <summary>
    ///     Every IR name the compilation's own declarations will claim.
    /// </summary>
    /// <remarks>
    ///     Derived from the same <see cref="MemberBodies" /> walk that names the functions for real,
    ///     with reporting off, so the two cannot disagree about what a member is called.
    /// </remarks>
    HashSet<string> ReserveDeclaredNames(IReadOnlyList<NamedTypeSymbol> declaredTypes) {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (var type in declaredTypes) {
            if (type.TypeKind is not (TypeKind.Shader or TypeKind.Struct)) {
                continue;
            }

            if (type.TypeKind == TypeKind.Struct) {
                names.Add(type.Name);
            } else {
                names.Add($"{type.Name}.<init>");
            }

            foreach (var (name, _) in MemberBodies(type, report: false)) {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>One compilation's worth of linking, in the two stages the ordering requires.</summary>
    sealed class ReferenceLink {
        readonly LibraryIrDecoder decoder;
        readonly MetadataLoader loader;
        readonly Lowerer lowerer;
        readonly HashSet<string> taken;

        public ReferenceLink(Lowerer lowerer, MetadataLoader loader, HashSet<string> taken) {
            this.lowerer = lowerer;
            this.loader = loader;
            this.taken = taken;
            decoder = new(Unique);
        }

        /// <summary>
        ///     Decodes every library's structs into the module and maps the metadata types onto
        ///     them, so a source signature may mention a library struct.
        /// </summary>
        public void LinkStructs() {
            foreach (var library in loader.Libraries) {
                decoder.DecodeStructs(library.Ir);
            }

            Publish();

            foreach (var type in Types()) {
                if (type.IrStructName is { } name && decoder.Structs.GetValueOrDefault(name) is { } structType) {
                    lowerer.structs[type] = structType;
                }
            }
        }

        /// <summary>
        ///     Decodes every library's functions into the module and maps the metadata members onto
        ///     them, so a call bound against a library resolves to a real callee.
        /// </summary>
        public void LinkFunctions() {
            decoder.DecodeFunctions();

            // Publish again: resolving a signature can name a struct no loaded library declares,
            // and the placeholder that stands in for it still has to reach the module.
            Publish();

            foreach (var type in Types()) {
                foreach (var member in type.GetMembers()) {
                    Map(member);
                }
            }
        }

        /// <summary>Adds anything the decoder has produced but the module has not seen.</summary>
        void Publish() {
            foreach (var (name, structType) in decoder.Structs) {
                if (lowerer.importedStructs.Add(structType)) {
                    lowerer.importedStructNames[structType] = name;
                    lowerer.importedStructsByName[structType.Name] = structType;
                    lowerer.module.Add(structType);
                }
            }

            foreach (var (name, function) in decoder.Functions) {
                if (lowerer.importedFunctions.Add(function)) {
                    lowerer.importedFunctionNames[function] = name;
                    lowerer.module.Add(function);
                }
            }
        }

        void Map(Symbol member) {
            switch (member) {
                case MetadataMethodSymbol { IrFunctionName: { } name } method: {
                    if (decoder.Functions.GetValueOrDefault(name) is { } function) {
                        var kind = method.IsConstructor ? BoundBodyKind.Constructor : BoundBodyKind.Method;
                        lowerer.functions[(method, kind)] = function;
                    }

                    break;
                }

                case MetadataPropertySymbol property: {
                    if (property.IrGetterName is { } getter
                        && decoder.Functions.GetValueOrDefault(getter) is { } getterFunction) {
                        lowerer.functions[(property, BoundBodyKind.PropertyGetter)] = getterFunction;
                    }

                    if (property.IrSetterName is { } setter
                        && decoder.Functions.GetValueOrDefault(setter) is { } setterFunction) {
                        lowerer.functions[(property, BoundBodyKind.PropertySetter)] = setterFunction;
                    }

                    break;
                }
            }
        }

        IEnumerable<MetadataNamedTypeSymbol> Types() =>
            lowerer.compilation.GetReferencedTypes().OfType<MetadataNamedTypeSymbol>();

        /// <summary>
        ///     A module-unique name for a library entity, qualified by nothing until it has to be.
        /// </summary>
        /// <remarks>
        ///     Renaming only on collision, rather than qualifying everything, so the GLSL a frame
        ///     debugger shows still says <c>Saturate</c> — which is the other job that backend does.
        /// </remarks>
        string Unique(string name) {
            if (taken.Add(name)) {
                return name;
            }

            var candidate = $"{name}#1";
            var suffix = 2;

            while (!taken.Add(candidate)) {
                candidate = $"{name}#{suffix++}";
            }

            return candidate;
        }
    }
}
