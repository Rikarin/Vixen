// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Vixen.Raven.Binding;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Symbols;
using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.Artefacts;

/// <summary>
///     Builds a <see cref="CompiledLibrary" /> out of a bound and lowered compilation.
/// </summary>
/// <remarks>
///     <para>
///         What a library exports is what its own compilation declared: every type, and the body of
///         every member whose body can stand on its own. Types and functions linked in from another
///         library are <em>not</em> re-exported — a call into one travels as a name, and the consumer
///         resolves it against its own references, which is what keeps one struct from having two
///         identities in a module that references both libraries.
///     </para>
///     <para>
///         Two things are checked here rather than left to a consumer, because here is where they
///         can be fixed. A body that reads a shader binding cannot be linked into another shader, so
///         it is refused (<c>RVN5001</c>) instead of exported to fail somewhere with no source in
///         sight. And a stage entry point is not something a library supplies, which is said
///         (<c>RVN5002</c>) rather than silently dropped.
///     </para>
/// </remarks>
public static class LibraryBuilder {
    /// <summary>
    ///     Builds the library for a compilation.
    /// </summary>
    /// <param name="compilation">The compilation, already bound.</param>
    /// <param name="lowered">Its lowered form, from <see cref="Lowerer.LowerWithLinks" />.</param>
    /// <param name="diagnostics">Where the export checks report.</param>
    public static CompiledLibrary Build(
        Compilation compilation,
        LoweringResult lowered,
        DiagnosticBag diagnostics
    ) {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(lowered);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var types = compilation.GetAllTypes();
        var unexportable = FindUnexportable(lowered);
        var builder = new Builder(lowered, unexportable, diagnostics);

        // A permutation key read while building the library is folded into the bodies being
        // exported, and no consumer's --define can reach back in. Said here because the symptom
        // otherwise is a define that appears to be ignored.
        foreach (var key in compilation.UsedPermutationKeys) {
            diagnostics.Add(LibraryDiagnostics.PermutationBakedIn, Location.None, key);
        }

        var exported = types.Select(builder.BuildType).ToImmutableArray();

        // Every struct the compilation lowered, not only the ones a declared type maps to: a
        // function that returns a tuple names a struct lowering synthesized, and an artefact that
        // omitted it would decode that return type as an empty aggregate of the same name.
        var ownStructs = lowered.Module.Structs.Where(s => !lowered.ImportedStructs.Contains(s));

        return new() {
            Name = compilation.AssemblyName,
            Types = exported,
            Ir = LibraryIrEncoder.Encode(
                ownStructs,
                builder.ExportedFunctions,
                lowered.ArtefactName,
                lowered.ArtefactName
            ),
            SourceHash = HashSources(compilation)
        };
    }

    /// <summary>
    ///     The functions that cannot be exported: those that touch a shader binding, and everything
    ///     that calls one.
    /// </summary>
    /// <remarks>
    ///     Transitive, and it has to be. A helper that reads a uniform is obviously unexportable; a
    ///     function that merely calls that helper is just as unexportable, because linking it into a
    ///     consumer would drag the helper along and name storage the consumer never declared.
    /// </remarks>
    static Dictionary<IrFunction, string> FindUnexportable(LoweringResult lowered) {
        Dictionary<IrFunction, string> unexportable = [];

        foreach (var function in lowered.Module.AllFunctions) {
            if (Globals(function.Body).FirstOrDefault() is { } binding) {
                unexportable[function] = binding;
            }
        }

        // Propagate to callers until nothing changes. The call graph has no recursion, but a fixed
        // point is what makes the pass independent of the order functions appear in.
        bool changed;

        do {
            changed = false;

            foreach (var function in lowered.Module.AllFunctions) {
                if (unexportable.ContainsKey(function)) {
                    continue;
                }

                foreach (var callee in CallGraph.Calls(function.Body)) {
                    if (unexportable.TryGetValue(callee, out var binding)) {
                        unexportable[function] = binding;
                        changed = true;
                        break;
                    }
                }
            }
        } while (changed);

        return unexportable;
    }

    /// <summary>Every shader-level binding a body reaches, by name.</summary>
    static IEnumerable<string> Globals(IrStatement statement) {
        switch (statement) {
            case IrBlock block: {
                foreach (var name in block.Statements.SelectMany(Globals)) {
                    yield return name;
                }

                break;
            }

            case IrLoadInstruction { Place.Root: { Kind: IrVariableKind.Global } root }:
                yield return root.Name;
                break;

            case IrStoreInstruction { Place.Root: { Kind: IrVariableKind.Global } root }:
                yield return root.Name;
                break;

            case IrIfStatement conditional: {
                foreach (var name in Globals(conditional.Then)) {
                    yield return name;
                }

                if (conditional.Else is { } otherwise) {
                    foreach (var name in Globals(otherwise)) {
                        yield return name;
                    }
                }

                break;
            }

            case IrLoopStatement loop: {
                IrBlock?[] parts = [loop.Condition, loop.Body, loop.Continue];

                foreach (var name in parts.Where(p => p is not null).SelectMany(p => Globals(p!))) {
                    yield return name;
                }

                break;
            }
        }
    }

    /// <summary>
    ///     SHA-256 over the compilation's sources, so a stale library is detectable without
    ///     recompiling to compare. Same construction as <see cref="CompiledEffect.SourceHash" />.
    /// </summary>
    static string HashSources(Compilation compilation) {
        var joined = string.Join(
            "\n",
            compilation.SyntaxTrees.Select(tree => tree.Text?.ToString() ?? string.Empty)
        );

        return joined.Length == 0 ? string.Empty : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    /// <summary>Carries the per-build state the type walk needs.</summary>
    sealed class Builder(
        LoweringResult lowered,
        Dictionary<IrFunction, string> unexportable,
        DiagnosticBag diagnostics
    ) {
        readonly List<IrFunction> functions = [];

        /// <summary>The IR functions to put in the artefact, in the order the types named them.</summary>
        public IReadOnlyList<IrFunction> ExportedFunctions => functions;

        public LibraryType BuildType(NamedTypeSymbol type) {
            // Null for anything with no storage — a shader, a protocol, an enum — and for a struct
            // whose IR came from a referenced library, which that library exports.
            var irStruct = lowered.Structs.GetValueOrDefault(type) is { } structType
                && !lowered.ImportedStructs.Contains(structType)
                    ? structType.Name
                    : null;

            return new() {
                Namespace = type.ContainingNamespace?.QualifiedName ?? string.Empty,
                Name = type.Name,
                ContainingType = type.ContainingType is { } outer ? QualifiedName(outer) : null,
                Kind = type.TypeKind,
                BaseType = type.BaseType is { } baseType ? Reference(baseType) : null,
                Interfaces = [.. type.Interfaces.Select(Reference)],
                TypeParameters = [.. type.TypeParameters.Select(TypeParameter)],
                Fields = [.. type.GetMembers().OfType<FieldSymbol>().Select(Field)],
                Properties = [.. type.GetMembers().OfType<PropertySymbol>().Select(Property)],
                Methods = [.. type.GetMembers().OfType<MethodSymbol>().Select(m => Method(type, m))],
                IrStruct = irStruct
            };
        }

        LibraryField Field(FieldSymbol field) =>
            new() {
                Name = field.Name,
                Type = Reference(field.Type),
                IsStatic = field.IsStatic,
                IsReadOnly = field.IsReadOnly,
                IsConst = field.IsConst,
                IsPermutation = field.IsPermutation,
                IsValueParameter = field.IsValueParameter,
                IsCompose = field.IsCompose,
                // DeclaredValue, not ConstantValue: the latter answers with what this compilation
                // was given for a permutation key, which is per-variant and not a property of the
                // library. Reading it would also record a permutation use, so describing a shader
                // would change its cache key.
                DeclaredValue = LibraryValue.From(field.DeclaredValue),
                ResourceKind = field.ResourceKind,
                ResourceSet = field.ResourceSet,
                SemanticName = field.SemanticName
            };

        LibraryProperty Property(PropertySymbol property) =>
            new() {
                Name = property.Name,
                Type = Reference(property.Type),
                HasGetter = property.HasGetter,
                HasSetter = property.HasSetter,
                IsStatic = property.IsStatic,
                IrGetter = Export(property, BoundBodyKind.PropertyGetter, $"get_{property.Name}"),
                IrSetter = Export(property, BoundBodyKind.PropertySetter, $"set_{property.Name}")
            };

        LibraryMethod Method(NamedTypeSymbol declaringType, MethodSymbol method) {
            // A stage is generated per effect from the shader that declares it, so an entry point
            // is not part of what a library supplies. Said rather than dropped: an author who wrote
            // [PixelShader] in a library file believes something about what shipping it does.
            if (method.Stage != ShaderStage.None) {
                diagnostics.Add(
                    LibraryDiagnostics.EntryPointNotExported,
                    Location.None,
                    $"{declaringType.Name}.{method.Name}",
                    method.Stage
                );
            }

            var kind = method.IsConstructor ? BoundBodyKind.Constructor : BoundBodyKind.Method;

            return new() {
                Name = method.Name,
                MethodKind = method.MethodKind,
                ReturnType = Reference(method.ReturnType),
                Parameters = [.. method.Parameters.Select(Parameter)],
                TypeParameters = [.. method.TypeParameters.Select(TypeParameter)],
                IsStatic = method.IsStatic,
                Stage = method.Stage,
                SemanticName = method.SemanticName,
                IrFunction = method.Stage != ShaderStage.None
                    ? null
                    : Export(method, kind, method.ToDisplayString())
            };
        }

        /// <summary>
        ///     The IR function name to record for a member's body, or null when there is nothing to
        ///     export.
        /// </summary>
        /// <param name="member">The member whose body is being exported.</param>
        /// <param name="kind">Which of its bodies.</param>
        /// <param name="description">How to name the member in a diagnostic.</param>
        string? Export(Symbol member, BoundBodyKind kind, string description) {
            if (lowered.Functions.GetValueOrDefault((member, kind)) is not { } function) {
                // No body: a protocol's declaration, which is bodyless by construction and is what
                // a compose slot binds against.
                return null;
            }

            // Already an import — a member of a referenced library, reached because a compose slot
            // resolved to one. Its own artefact exports it; this one records the call by name.
            if (lowered.ImportedFunctions.Contains(function)) {
                return null;
            }

            if (unexportable.TryGetValue(function, out var binding)) {
                diagnostics.Add(
                    LibraryDiagnostics.BindingNotExportable,
                    member.DeclaringSyntax?.GetLocation() ?? Location.None,
                    description,
                    binding
                );
                return null;
            }

            functions.Add(function);
            return function.Name;
        }

        LibraryParameter Parameter(ParameterSymbol parameter) =>
            new() {
                Name = parameter.Name,
                Type = Reference(parameter.Type),
                Ordinal = parameter.Ordinal,
                HasDefaultValue = parameter.HasDefaultValue,
                DefaultValue = LibraryValue.From(parameter.DefaultValue),
                SemanticName = parameter.SemanticName
            };

        LibraryTypeParameter TypeParameter(TypeParameterSymbol parameter) =>
            new() {
                Name = parameter.Name,
                Ordinal = parameter.Ordinal,
                Constraints = [.. parameter.ConstraintTypes.Select(Reference)]
            };

        /// <summary>
        ///     Encodes a reference to a type.
        /// </summary>
        /// <remarks>
        ///     A primitive travels as its <see cref="SpecialType" />, which is the identity the whole
        ///     binder keys off; a declared type travels as a qualified name, which survives a
        ///     recompilation of the library and resolves against another library's types. Only
        ///     arrays and tuples carry their shape, because they have no name to be resolved by.
        /// </remarks>
        static LibraryTypeReference Reference(TypeSymbol type) =>
            type switch {
                PrimitiveTypeSymbol primitive => LibraryTypeReference.Primitive(primitive.SpecialType),
                BuiltInNamedTypeSymbol builtIn => new() {
                    Kind = LibraryTypeKind.BuiltIn, Special = builtIn.SpecialType
                },
                TypeParameterSymbol parameter => new() {
                    Kind = LibraryTypeKind.TypeParameter, Name = parameter.Name
                },
                ArrayTypeSymbol array => new() {
                    Kind = LibraryTypeKind.Array, Element = Reference(array.ElementType), Rank = array.Rank
                },
                TupleTypeSymbol tuple => new() {
                    Kind = LibraryTypeKind.Tuple,
                    Elements = [.. tuple.ElementTypes.Select(Reference)],
                    ElementNames = [.. tuple.ElementNames]
                },
                NamedTypeSymbol { IsErrorType: true } => LibraryTypeReference.ErrorType,
                NamedTypeSymbol { IsConstructed: true } constructed => new() {
                    Kind = LibraryTypeKind.Named,
                    Name = QualifiedName(constructed.OriginalDefinition),
                    TypeArguments = [.. constructed.TypeArguments.Select(Reference)]
                },
                NamedTypeSymbol named => new() { Kind = LibraryTypeKind.Named, Name = QualifiedName(named) },
                _ => LibraryTypeReference.ErrorType
            };

        /// <summary>
        ///     Namespace, declaring types and name, dotted — the key a type reference resolves
        ///     against, and the same construction <see cref="LibraryType.QualifiedName" /> uses.
        /// </summary>
        /// <remarks>
        ///     Not <c>ToDisplayString</c>, which appends type arguments: a reference names the
        ///     definition, and its arguments travel separately.
        /// </remarks>
        static string QualifiedName(NamedTypeSymbol type) {
            List<string> parts = [type.Name];

            for (var symbol = type.ContainingSymbol; symbol is not null; symbol = symbol.ContainingSymbol) {
                switch (symbol) {
                    case NamedTypeSymbol outer:
                        parts.Insert(0, outer.Name);
                        break;

                    case NamespaceSymbol { IsGlobalNamespace: false } ns:
                        parts.Insert(0, ns.Name);
                        break;
                }
            }

            return string.Join('.', parts);
        }
    }
}
