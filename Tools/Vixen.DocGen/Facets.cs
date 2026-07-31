// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;

namespace Vixen.DocGen;

/// <summary>Reads the kind-specific facts a page shows — docs/plan/25 § 2.3 and § 2.6.</summary>
/// <remarks>
///     <para>
///         This is the half of the taxonomy that earns it. Knowing a type is a component is a label;
///         knowing it is <b>eight bytes, written by <c>MovementSystem</c> in <c>FixedUpdate</c>, sent
///         unreliably at 20 Hz and quantised to 16 bits</b> is documentation — and every one of those
///         is already declared, in an attribute the engine reads at compile time.
///     </para>
///     <para>
///         Everything here is derived or absent. Where a fact cannot be computed honestly — a struct
///         whose layout the compiler decides, a system whose declarations are inferred — the facet is
///         null rather than a guess.
///     </para>
/// </remarks>
static class Facets {
    const string Reads = "Vixen.Ecs.Systems.ReadsAttribute";
    const string Writes = "Vixen.Ecs.Systems.WritesAttribute";
    const string UpdateInGroup = "Vixen.Ecs.Systems.UpdateInGroupAttribute";
    const string UpdateBefore = "Vixen.Ecs.Systems.UpdateBeforeAttribute";
    const string UpdateAfter = "Vixen.Ecs.Systems.UpdateAfterAttribute";
    const string Replicated = "Vixen.Net.Replication.ReplicatedAttribute";
    const string Quantize = "Vixen.Net.Replication.QuantizeAttribute";
    const string Importer = "Vixen.Editor.Assets.ImporterAttribute";
    const string Node = "Vixen.Editor.NodeGraph.NodeAttribute";
    const string AttributeUsage = "System.AttributeUsageAttribute";

    /// <summary>`Archetype.ChunkBudget` — the per-chunk byte budget the ECS allocates.</summary>
    const int ChunkBudget = 16 * 1024;

    public static DocFacets? For(INamedTypeSymbol type, DocKind kind) {
        var facets = kind switch {
            DocKind.Component or DocKind.SceneComponent => Component(type),
            DocKind.System => System(type),
            DocKind.ReplicatedComponent => Replication(type),
            DocKind.Importer => new DocFacets { Extensions = Some(Strings(type, Importer)) },
            DocKind.GraphNode => GraphNode(type),
            DocKind.Annotation => Annotation(type),
            _ => null
        };

        // A component may also be replicated, and a replicated component is still a component: the
        // kind picks one page shape, the facts are not exclusive.
        if (kind is DocKind.Component or DocKind.SceneComponent && Find(type, Replicated) is not null) {
            facets = (facets ?? new DocFacets()) with {
                Channel = Replication(type)?.Channel,
                SendRate = Replication(type)?.SendRate,
                Quantized = Replication(type)?.Quantized
            };
        }

        return facets is null || facets.IsEmpty ? null : facets;
    }

    static DocFacets Component(INamedTypeSymbol type) {
        var size = TypeLayout.SizeOf(type);

        return new DocFacets {
            SizeBytes = size,
            // What a chunk holds when this component is the only one on the archetype — an upper
            // bound, and the number that says whether a component is cheap to iterate. A real
            // archetype carries several columns and fits fewer.
            EntitiesPerChunk = size is > 0 ? ChunkBudget / (TypeLayout.EntityBytes + size) : null
        };
    }

    static DocFacets System(INamedTypeSymbol type) => new() {
        // Without [UpdateInGroup] a system lands in Update, which the attribute's own documentation
        // says — so the absence is a fact rather than a blank.
        Phase = Find(type, UpdateInGroup) is { ConstructorArguments.Length: > 0 } group
            ? EnumName(group.ConstructorArguments[0])
            : "Update",
        Reads = Some(Types(type, Reads)),
        Writes = Some(Types(type, Writes)),
        RunsBefore = Some(Types(type, UpdateBefore)),
        RunsAfter = Some(Types(type, UpdateAfter))
    };

    static DocFacets? Replication(INamedTypeSymbol type) {
        var replicated = Find(type, Replicated);

        if (replicated is null) {
            return null;
        }

        var named = replicated.NamedArguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        return new DocFacets {
            Channel = named.TryGetValue("Channel", out var channel) ? EnumName(channel) : "Unreliable",
            SendRate = named.TryGetValue("SendRate", out var rate) ? rate.Value as int? : 0,
            Priority = named.TryGetValue("Priority", out var priority) ? priority.Value as int? : 0,
            Quantized = Some([
                .. type.GetMembers()
                    .Select(member => (member, quantize: Find(member, Quantize)))
                    .Where(pair => pair.quantize is { ConstructorArguments.Length: 3 })
                    .Select(pair => new DocQuantized(
                        pair.member.Name,
                        Convert.ToSingle(pair.quantize!.ConstructorArguments[0].Value, Culture),
                        Convert.ToSingle(pair.quantize.ConstructorArguments[1].Value, Culture),
                        Convert.ToInt32(pair.quantize.ConstructorArguments[2].Value, Culture)))
            ])
        };
    }

    static DocFacets GraphNode(INamedTypeSymbol type) {
        var node = Find(type, Node);
        var summary = node?.NamedArguments
            .FirstOrDefault(pair => string.Equals(pair.Key, "Summary", StringComparison.Ordinal))
            .Value.Value as string;

        return new DocFacets {
            MenuPath = node is { ConstructorArguments.Length: > 0 }
                ? node.ConstructorArguments[0].Value as string
                : null,
            MenuSummary = string.IsNullOrEmpty(summary) ? null : summary
        };
    }

    static DocFacets Annotation(INamedTypeSymbol type) {
        var usage = Find(type, AttributeUsage);

        return new DocFacets {
            // What the attribute may be put on, which is the first thing anybody looks up about one.
            Targets = usage is { ConstructorArguments.Length: > 0 }
                ? Some(AttributeTargetNames(Convert.ToInt64(usage.ConstructorArguments[0].Value, Culture)))
                : null,
            AllowMultiple = usage?.NamedArguments
                .FirstOrDefault(pair => string.Equals(pair.Key, "AllowMultiple", StringComparison.Ordinal))
                .Value.Value as bool?
        };
    }

    /// <summary>`AttributeTargets` is a flags enum, and its names are the useful form.</summary>
    static IReadOnlyList<string> AttributeTargetNames(long flags) {
        (long Flag, string Name)[] targets = [
            (1, "Assembly"), (2, "Module"), (4, "Class"), (8, "Struct"), (16, "Enum"),
            (32, "Constructor"), (64, "Method"), (128, "Property"), (256, "Field"), (512, "Event"),
            (1024, "Interface"), (2048, "Parameter"), (4096, "Delegate"), (8192, "ReturnValue"),
            (16384, "GenericParameter")
        ];

        return flags == 32767
            ? ["All"]
            : [.. targets.Where(target => (flags & target.Flag) != 0).Select(target => target.Name)];
    }

    /// <summary>
    ///     An enum argument's member name. <c>TypedConstant</c> carries the underlying integer, and
    ///     <c>SystemPhase.FixedUpdate</c> printed as <c>2</c> is not documentation.
    /// </summary>
    static string? EnumName(TypedConstant constant) {
        if (constant.Value is null) {
            return null;
        }

        var member = (constant.Type as INamedTypeSymbol)?
            .GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(field => field.HasConstantValue && Equals(field.ConstantValue, constant.Value));

        return member?.Name ?? Convert.ToString(constant.Value, Culture);
    }

    /// <summary>Empty reads as absent, because that is what it means and what it costs least.</summary>
    static IReadOnlyList<T>? Some<T>(IReadOnlyList<T> values) => values.Count == 0 ? null : values;

    static AttributeData? Find(ISymbol symbol, string attribute) =>
        symbol.GetAttributes().FirstOrDefault(candidate =>
            string.Equals(candidate.AttributeClass?.ToDisplayString(), attribute, StringComparison.Ordinal));

    /// <summary>The documentation ids of a `params Type[]` attribute's arguments.</summary>
    static IReadOnlyList<string> Types(ISymbol symbol, string attribute) => [
        .. symbol.GetAttributes()
            .Where(candidate =>
                string.Equals(candidate.AttributeClass?.ToDisplayString(), attribute, StringComparison.Ordinal))
            .SelectMany(candidate => candidate.ConstructorArguments)
            .SelectMany(argument => argument.Kind == TypedConstantKind.Array && !argument.Values.IsDefault
                ? argument.Values
                : [argument])
            .Select(argument => (argument.Value as ITypeSymbol)?.GetDocumentationCommentId())
            .Where(id => id is not null)
            .Distinct(StringComparer.Ordinal)!
    ];

    /// <summary>The string arguments of a `params string[]` attribute — an importer's extensions.</summary>
    static IReadOnlyList<string> Strings(ISymbol symbol, string attribute) {
        var data = Find(symbol, attribute);

        if (data is null || data.ConstructorArguments.Length == 0) {
            return [];
        }

        var argument = data.ConstructorArguments[0];

        return argument.Kind == TypedConstantKind.Array
            ? argument.Values.IsDefault
                ? []
                : [.. argument.Values.Select(value => value.Value as string).Where(value => value is not null)!]
            : argument.Value is string single ? [single] : [];
    }

    static readonly IFormatProvider Culture = global::System.Globalization.CultureInfo.InvariantCulture;
}

/// <summary>
///     What a struct occupies, computed the way the runtime lays a sequential struct out.
/// </summary>
/// <remarks>
///     ⚠ <b>Null is the answer whenever the honest one is unknown.</b> A struct holding a reference,
///     a generic parameter, a pointer or an explicit layout is one whose size is the runtime's
///     business, and printing a number for it would be worse than printing nothing — a component's
///     size is read by somebody deciding whether to split it.
/// </remarks>
static class TypeLayout {
    /// <summary>`Vixen.Core.Entity` — two ints and a short, which the entity column costs per row.</summary>
    public const int EntityBytes = 12;

    public static int? SizeOf(INamedTypeSymbol type) => SizeOf(type, depth: 0);

    static int? SizeOf(ITypeSymbol type, int depth) {
        if (depth > 8) {
            return null;
        }

        if (Primitive(type) is { } primitive) {
            return primitive;
        }

        if (type.TypeKind != TypeKind.Struct || type.IsReferenceType || type is INamedTypeSymbol { IsGenericType: true }) {
            return null;
        }

        if (type.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.Name == "StructLayoutAttribute")) {
            return null;
        }

        var offset = 0;
        var alignment = 1;

        foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsStatic && !field.IsConst)) {
            var size = SizeOf(field.Type, depth + 1);

            if (size is not { } bytes) {
                return null;
            }

            var fieldAlignment = Math.Min(bytes, 8);

            if (fieldAlignment > 0) {
                offset = Align(offset, fieldAlignment);
                alignment = Math.Max(alignment, fieldAlignment);
            }

            offset += bytes;
        }

        // A struct with no fields is one byte, as the runtime gives it: an empty tag component still
        // occupies a row.
        return offset == 0 ? 1 : Align(offset, alignment);
    }

    static int Align(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;

    static int? Primitive(ITypeSymbol type) => type.SpecialType switch {
        SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte => 1,
        SpecialType.System_Char or SpecialType.System_Int16 or SpecialType.System_UInt16 => 2,
        SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => 4,
        SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double => 8,
        SpecialType.System_IntPtr or SpecialType.System_UIntPtr => 8,
        _ => type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol { EnumUnderlyingType: { } underlying }
            ? Primitive(underlying)
            : null
    };
}
