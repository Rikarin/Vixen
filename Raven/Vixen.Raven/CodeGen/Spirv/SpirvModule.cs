// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>
///     A SPIR-V module under construction. Instructions go into the sections the
///     spec's logical layout demands (§2.4) rather than into one list, because the
///     order between sections is fixed and the order within one is not.
/// </summary>
/// <remarks>
///     Ids are allocated from one counter for the whole module, and types, constants
///     and pointer types are interned: emitting <c>f32</c> twice must yield the same
///     id, both because the spec requires type uniqueness and because duplicated
///     aggregates would fail validation.
/// </remarks>
public sealed class SpirvModule {
    /// <summary>The word every SPIR-V binary starts with.</summary>
    public const uint MagicNumber = 0x07230203;

    readonly List<SpirvInstruction> capabilities = [];
    readonly List<SpirvInstruction> extensions = [];
    readonly List<SpirvInstruction> imports = [];
    readonly List<SpirvInstruction> memoryModel = [];
    readonly List<SpirvInstruction> entryPoints = [];
    readonly List<SpirvInstruction> executionModes = [];
    readonly List<SpirvInstruction> debug = [];
    readonly List<SpirvInstruction> annotations = [];
    readonly List<SpirvInstruction> declarations = [];
    readonly List<SpirvInstruction> functions = [];

    readonly Dictionary<string, uint> interned = new(StringComparer.Ordinal);
    readonly HashSet<SpirvCapability> declaredCapabilities = [];
    readonly HashSet<string> declaredExtensions = new(StringComparer.Ordinal);

    /// <summary>The SPIR-V version word, e.g. <c>0x00010000</c> for 1.0.</summary>
    public uint Version { get; }

    /// <summary>One past the highest id used — the binary header's bound.</summary>
    public uint Bound { get; private set; } = 1;

    // --- Output ------------------------------------------------------------

    /// <summary>Every instruction, in the spec's logical layout order.</summary>
    public IEnumerable<SpirvInstruction> Instructions =>
        capabilities
            .Concat(extensions)
            .Concat(imports)
            .Concat(memoryModel)
            .Concat(entryPoints)
            .Concat(executionModes)
            .Concat(debug)
            .Concat(annotations)
            .Concat(declarations)
            .Concat(functions);

    public SpirvModule(uint version) {
        Version = version;
    }

    public uint AllocateId() => Bound++;

    // --- Sections ----------------------------------------------------------

    public void AddCapability(SpirvCapability capability) {
        if (declaredCapabilities.Add(capability)) {
            capabilities.Add(new(SpirvOp.Capability, null, null, SpirvOperand.Enumerant(capability)));
        }
    }

    /// <summary>Declares an <c>OpExtension</c>, once however many things ask for it.</summary>
    /// <param name="name">The extension's name, as the registry spells it.</param>
    /// <remarks>
    ///     Deduplicated for the same reason capabilities are, and it matters more here: a module with
    ///     the same <c>OpExtension</c> twice is rejected by <c>spirv-val</c> rather than tolerated,
    ///     and the second one would come from whichever binding happened to be second.
    /// </remarks>
    public void AddExtension(string name) {
        if (declaredExtensions.Add(name)) {
            extensions.Add(new(SpirvOp.Extension, null, null, SpirvOperand.String(name)));
        }
    }

    public uint AddExtendedInstructionSet(string name) {
        var id = AllocateId();
        imports.Add(new(SpirvOp.ExtInstImport, null, id, SpirvOperand.String(name)));
        return id;
    }

    public void SetMemoryModel(SpirvAddressingModel addressing, SpirvMemoryModel memory) {
        memoryModel.Clear();
        memoryModel.Add(
            new(
                SpirvOp.MemoryModel,
                null,
                null,
                SpirvOperand.Enumerant(addressing),
                SpirvOperand.Enumerant(memory)
            )
        );
    }

    public void AddEntryPoint(SpirvExecutionModel model, uint function, string name, IEnumerable<uint> @interface) {
        entryPoints.Add(
            new(
                SpirvOp.EntryPoint,
                null,
                null,
                [
                    SpirvOperand.Enumerant(model),
                    SpirvOperand.Id(function),
                    SpirvOperand.String(name),
                    .. @interface.Select(SpirvOperand.Id)
                ]
            )
        );
    }

    public void AddExecutionMode(uint function, SpirvExecutionMode mode, params uint[] literals) {
        executionModes.Add(
            new(
                SpirvOp.ExecutionMode,
                null,
                null,
                [SpirvOperand.Id(function), SpirvOperand.Enumerant(mode), .. literals.Select(SpirvOperand.Literal)]
            )
        );
    }

    /// <summary>Names a result, for a debugger and for whatever cross-compiles this.</summary>
    /// <remarks>
    ///     Through <see cref="SpirvNames.Of" />, because the second of those readers turns the name
    ///     into a variable declaration in a language with its own keywords — see that file for the
    ///     word that found it.
    /// </remarks>
    public void AddName(uint target, string name) =>
        debug.Add(new(SpirvOp.Name, null, null, SpirvOperand.Id(target), SpirvOperand.String(SpirvNames.Of(name))));

    /// <summary>Names one member of a struct, on the same terms.</summary>
    public void AddMemberName(uint target, int member, string name) =>
        debug.Add(
            new(
                SpirvOp.MemberName,
                null,
                null,
                SpirvOperand.Id(target),
                SpirvOperand.Literal(member),
                SpirvOperand.String(SpirvNames.Of(name))
            )
        );

    public void Decorate(uint target, SpirvDecoration decoration, params SpirvOperand[] extra) =>
        annotations.Add(
            new(
                SpirvOp.Decorate,
                null,
                null,
                [SpirvOperand.Id(target), SpirvOperand.Enumerant(decoration), .. extra]
            )
        );

    public void DecorateMember(uint target, int member, SpirvDecoration decoration, params SpirvOperand[] extra) =>
        annotations.Add(
            new(
                SpirvOp.MemberDecorate,
                null,
                null,
                [
                    SpirvOperand.Id(target),
                    SpirvOperand.Literal(member),
                    SpirvOperand.Enumerant(decoration),
                    .. extra
                ]
            )
        );

    /// <summary>Adds a type, constant or global variable to the declarations section.</summary>
    public uint AddDeclaration(SpirvOp op, uint? resultType, params SpirvOperand[] operands) {
        var id = AllocateId();
        declarations.Add(new(op, resultType, id, operands));
        return id;
    }

    /// <summary>
    ///     Adds a declaration once per key. The key is the caller's structural
    ///     identity for the thing — <c>"vec f32 3"</c>, say.
    /// </summary>
    public uint Intern(string key, Func<uint> create) {
        if (interned.TryGetValue(key, out var existing)) {
            return existing;
        }

        var id = create();
        interned[key] = id;
        return id;
    }

    public void AddFunctionInstruction(SpirvInstruction instruction) => functions.Add(instruction);

    /// <summary>The binary module: the five-word header followed by the instruction stream.</summary>
    public byte[] ToBytes() {
        List<uint> words = [
            MagicNumber,
            Version,
            // Generator magic number. 0 is "unknown"; the registered range belongs
            // to tools that have asked Khronos for one.
            0,
            Bound,
            // Reserved for instruction schema; always 0 today.
            0
        ];

        foreach (var instruction in Instructions) {
            instruction.Encode(words);
        }

        var bytes = new byte[words.Count * 4];
        for (var i = 0; i < words.Count; i++) {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        }

        return bytes;
    }

    /// <summary>
    ///     The module as an assembly listing, in the shape <c>spirv-dis</c> prints.
    ///     It is rendered from the same instructions the binary is encoded from, so a
    ///     golden file over it cannot drift from what actually shipped.
    /// </summary>
    public string ToAssembly() {
        var builder = new StringBuilder();

        builder.Append("; SPIR-V\n");
        builder.Append("; Version: ")
            .Append((Version >> 16) & 0xFF)
            .Append('.')
            .Append((Version >> 8) & 0xFF)
            .Append('\n');
        builder.Append("; Generator: Raven\n");
        builder.Append("; Bound: ").Append(Bound).Append('\n');
        builder.Append("; Schema: 0\n");

        foreach (var instruction in Instructions) {
            builder.Append(instruction).Append('\n');
        }

        return builder.ToString();
    }
}
