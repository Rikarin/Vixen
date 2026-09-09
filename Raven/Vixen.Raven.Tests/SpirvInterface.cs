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

    /// <summary>
    ///     Every laid-out struct in a module, as member name → offset and, where the member is an
    ///     array, its stride — identified by <em>what the struct holds</em> rather than by its name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Why this exists beside <see cref="Members" />, which already reads offsets.</b>
    ///         Two things defeat that map on a storage buffer, and both are silent. The first is that
    ///         <c>ArrayStride</c> is an <c>OpDecorate</c> on the <em>array type</em> and never an
    ///         <c>OpMemberDecorate</c>, so the decoration std430 is entirely about is not something
    ///         <see cref="Read" /> can ever see, however many storage buffers it is pointed at. The
    ///         second is that <see cref="Members" /> is keyed by the struct's name, and the two front
    ///         ends do not agree about that: Raven emits one type per layout rule and calls it
    ///         <c>Sample.Std430</c>, where glslang emits <c>Sample_0</c>, so a comparison keyed by
    ///         name reports a difference that is only a spelling.
    ///     </para>
    ///     <para>
    ///         So a struct is keyed by its member names in order — <c>position|age|weights|tint</c> —
    ///         which both spell identically because both take them from the source. Only structs
    ///         carrying an <c>Offset</c> decoration are returned, which is what keeps the undecorated
    ///         function-local copy of the same struct from arriving as a second entry with no numbers
    ///         in it.
    ///     </para>
    /// </remarks>
    public static ImmutableSortedDictionary<string, string> Layout(string disassembly) {
        ArgumentNullException.ThrowIfNull(disassembly);

        var lines = disassembly.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var memberNames = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.Ordinal);
        var memberTypes = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var offsets = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        var strides = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in lines) {
            var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            switch (tokens) {
                // OpMemberName %Sample 2 "weights"
                case ["OpMemberName", var type, var index, .. var name]:
                    memberNames.TryAdd(type, []);
                    memberNames[type][Number(index)] = string.Join(' ', name).Trim('"');
                    break;

                // %11 = OpTypeStruct %7 %5 %9 %10
                case [var type, "=", "OpTypeStruct", .. var members]:
                    memberTypes[type] = members;
                    break;

                // OpMemberDecorate %Sample 1 Offset 12
                case ["OpMemberDecorate", var type, var index, "Offset", var value]:
                    offsets.TryAdd(type, []);
                    offsets[type][Number(index)] = value;
                    break;

                // OpDecorate %_arr_float_uint_3 ArrayStride 4 — on the array type, never the member.
                case ["OpDecorate", var type, "ArrayStride", var value]:
                    strides[type] = value;
                    break;
            }
        }

        var layout = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var (type, byIndex) in offsets) {
            if (!memberNames.TryGetValue(type, out var names) || !memberTypes.TryGetValue(type, out var types)) {
                continue;
            }

            var signature = string.Join('|', names.Values);

            foreach (var (index, offset) in byIndex) {
                var name = names.GetValueOrDefault(index, index.ToString(CultureInfo.InvariantCulture));
                layout[$"{signature} :: {name}.Offset"] = offset;

                if (index < types.Length && strides.TryGetValue(types[index], out var stride)) {
                    layout[$"{signature} :: {name}.ArrayStride"] = stride;
                }
            }
        }

        return layout.ToImmutable();
    }
}
