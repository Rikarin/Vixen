// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;

namespace Tests;

/// <summary>
///     The host-visible interface of a SPIR-V module, read out of its disassembly.
/// </summary>
/// <remarks>
///     Everything here is a number the engine depends on: which descriptor a resource is at,
///     where a member sits inside its block, which location an attribute takes. Two modules
///     compiled from the same source must agree on every one of them or the host writes into the
///     wrong place on one of the two.
/// </remarks>
/// <param name="Descriptors">Resource name → its <c>(set, binding)</c> pair.</param>
/// <param name="Members">Block name → member index → decoration → value.</param>
/// <param name="Locations">Stage variable name → its location.</param>
/// <param name="ExecutionModel">The entry point's execution model, e.g. <c>Fragment</c>.</param>
public sealed record SpirvInterface(
    ImmutableSortedDictionary<string, (int Set, int Binding)> Descriptors,
    ImmutableSortedDictionary<string, ImmutableSortedDictionary<string, string>> Members,
    ImmutableSortedDictionary<string, int> Locations,
    string ExecutionModel
) {
    /// <summary>
    ///     Decorations worth comparing. Deliberately not all of them: <c>Block</c> and
    ///     <c>RelaxedPrecision</c> say nothing about where bytes land, and glslang emits some
    ///     that Raven has no reason to.
    /// </summary>
    static readonly string[] MemberDecorations = ["Offset", "MatrixStride", "ArrayStride", "ColMajor", "RowMajor"];

    /// <summary>Reads the interface out of a <c>spirv-dis</c> listing.</summary>
    /// <remarks>
    ///     Names rather than ids, because the two compilers number ids in their own order. A
    ///     uniform block's variable may be unnamed — glslang leaves it as <c>%_</c> — so a block
    ///     is identified by the struct type in its pointer type instead, which both spell the
    ///     same because both take it from the block's name.
    /// </remarks>
    public static SpirvInterface Read(string disassembly) {
        ArgumentNullException.ThrowIfNull(disassembly);

        var lines = disassembly.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var sets = new Dictionary<string, int>(StringComparer.Ordinal);
        var bindings = new Dictionary<string, int>(StringComparer.Ordinal);
        var locations = new Dictionary<string, int>(StringComparer.Ordinal);
        var members = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var executionModel = string.Empty;

        foreach (var raw in lines) {
            var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            switch (tokens) {
                // %albedo = OpVariable %_ptr_UniformConstant_18 UniformConstant
                case [var id, "=", "OpVariable", var pointer, ..]:
                    names[id] = BlockOf(pointer) ?? id.TrimStart('%');
                    break;

                // OpEntryPoint Fragment %main "main" …
                case ["OpEntryPoint", var model, ..]:
                    executionModel = model;
                    break;

                case ["OpDecorate", var id, "DescriptorSet", var value]:
                    sets[id] = Number(value);
                    break;

                case ["OpDecorate", var id, "Binding", var value]:
                    bindings[id] = Number(value);
                    break;

                case ["OpDecorate", var id, "Location", var value]:
                    locations[id] = Number(value);
                    break;

                // OpMemberDecorate %LitPerViewUniforms 0 MatrixStride 16
                case ["OpMemberDecorate", var block, var index, var decoration, ..]
                    when MemberDecorations.Contains(decoration):
                    members.TryAdd(block.TrimStart('%'), []);
                    members[block.TrimStart('%')][$"{index}.{decoration}"] =
                        tokens.Length > 4 ? tokens[4] : string.Empty;
                    break;
            }
        }

        // Decorations arrive before the OpVariable they decorate, so the names are resolved
        // once everything has been read rather than as each line goes past.
        var descriptors = ImmutableSortedDictionary.CreateBuilder<string, (int, int)>(StringComparer.Ordinal);
        foreach (var (id, set) in sets) {
            descriptors[names.GetValueOrDefault(id, id.TrimStart('%'))] = (set, bindings.GetValueOrDefault(id, -1));
        }

        var located = ImmutableSortedDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (var (id, location) in locations) {
            located[names.GetValueOrDefault(id, id.TrimStart('%'))] = location;
        }

        return new(
            descriptors.ToImmutable(),
            members.ToImmutableSortedDictionary(
                block => block.Key,
                block => block.Value.ToImmutableSortedDictionary(StringComparer.Ordinal),
                StringComparer.Ordinal
            ),
            located.ToImmutable(),
            executionModel
        );
    }

    /// <summary>
    ///     The block name inside a pointer type, or null when the pointer is not to a block.
    /// </summary>
    /// <remarks>
    ///     <c>%_ptr_Uniform_LitPerViewUniforms</c> → <c>LitPerViewUniforms</c>. A pointer to an
    ///     unnamed type disassembles as <c>%_ptr_UniformConstant_18</c>, whose tail is an id
    ///     rather than a name; those are opaque resources, which have names of their own.
    /// </remarks>
    static string? BlockOf(string pointer) {
        const string Prefix = "%_ptr_Uniform_";

        if (!pointer.StartsWith(Prefix, StringComparison.Ordinal)) {
            return null;
        }

        var tail = pointer[Prefix.Length..];
        return tail.Length > 0 && !tail.All(char.IsAsciiDigit) ? tail : null;
    }

    static int Number(string text) => int.Parse(text, CultureInfo.InvariantCulture);
}
