// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Artefacts;

/// <summary>
///     The <c>.rvnfx</c> container: a magic number, a version, a JSON header and the modules'
///     bytes appended raw.
/// </summary>
/// <remarks>
///     <para>
///         Split deliberately. The header is JSON because it is read by tools, diffed by people
///         and will grow fields — and a shipped artefact should be inspectable without a bespoke
///         viewer. The module bytes sit after it verbatim because SPIR-V is already a compact
///         binary and base64-ing it into the JSON would cost a third of its size for nothing.
///     </para>
///     <para>
///         Every integer is little-endian, matching SPIR-V's own convention and every platform
///         Vixen targets.
///     </para>
/// </remarks>
public static class CompiledEffectFormat {
    /// <summary>
    ///     Magic number: <c>RVNFX</c> followed by <c>0x1A</c>.
    /// </summary>
    /// <remarks>
    ///     The trailing byte is the DOS end-of-file character, the same guard PNG uses: a tool
    ///     that treats the file as text stops there instead of printing the binary payload.
    ///     Written as an escape rather than a literal control character so it is visible in
    ///     source and survives an editor that trims or re-encodes.
    /// </remarks>
    public static ReadOnlySpan<byte> Magic => "RVNFX\u001a"u8;

    /// <summary>
    ///     Format version. Bump on any change a previous reader would misread; the reader
    ///     rejects anything it does not know rather than guessing.
    /// </summary>
    public const int Version = 1;

    internal static readonly JsonSerializerOptions Json = new() {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The JSON header. Mirrors <see cref="CompiledEffect" /> minus the module bytes.</summary>
    internal sealed record Header {
        public string Name { get; init; } = string.Empty;
        public string Target { get; init; } = string.Empty;
        public string SourceHash { get; init; } = string.Empty;
        public SortedDictionary<string, string> PermutationKey { get; init; } = [];
        public RavenReflection Reflection { get; init; } = new();
        public List<ModuleHeader> Modules { get; init; } = [];
    }

    /// <summary>Where one module's bytes are, rather than the bytes themselves.</summary>
    internal sealed record ModuleHeader {
        public ShaderStage Stage { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool IsBinary { get; init; }
        public int Offset { get; init; }
        public int Length { get; init; }
    }
}

/// <summary>Writes a <see cref="CompiledEffect" /> as <c>.rvnfx</c>.</summary>
public static class CompiledEffectWriter {
    /// <summary>Serialises to bytes.</summary>
    public static byte[] Write(CompiledEffect effect) {
        ArgumentNullException.ThrowIfNull(effect);

        var payload = new MemoryStream();
        var modules = new List<CompiledEffectFormat.ModuleHeader>(effect.Modules.Length);

        foreach (var module in effect.Modules) {
            var offset = (int)payload.Position;
            payload.Write(module.Bytes.AsSpan());

            modules.Add(
                new() {
                    Stage = module.Stage,
                    Name = module.Name,
                    IsBinary = module.IsBinary,
                    Offset = offset,
                    Length = module.Bytes.Length
                }
            );
        }

        var header = new CompiledEffectFormat.Header {
            Name = effect.Name,
            Target = effect.Target,
            SourceHash = effect.SourceHash,
            PermutationKey = new(effect.PermutationKey, StringComparer.Ordinal),
            Reflection = effect.Reflection,
            Modules = modules
        };

        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, CompiledEffectFormat.Json);

        var output = new MemoryStream();
        output.Write(CompiledEffectFormat.Magic);
        WriteInt32(output, CompiledEffectFormat.Version);
        WriteInt32(output, headerBytes.Length);
        output.Write(headerBytes);
        payload.Position = 0;
        payload.CopyTo(output);

        return output.ToArray();
    }

    /// <summary>Serialises to a file.</summary>
    public static void WriteFile(string path, CompiledEffect effect) => File.WriteAllBytes(path, Write(effect));

    static void WriteInt32(Stream stream, int value) {
        Span<byte> buffer = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}

/// <summary>Reads a <c>.rvnfx</c> back.</summary>
public static class CompiledEffectReader {
    /// <summary>
    ///     Deserialises from bytes.
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     The bytes are not a <c>.rvnfx</c>, are a version this reader does not know, or are
    ///     truncated. Every one of these is reported rather than half-read: a partly-loaded
    ///     effect would fail later somewhere far less obvious.
    /// </exception>
    public static CompiledEffect Read(ReadOnlySpan<byte> bytes) {
        var magic = CompiledEffectFormat.Magic;

        if (bytes.Length < magic.Length + 8 || !bytes[..magic.Length].SequenceEqual(magic)) {
            throw new InvalidDataException("Not a Raven compiled effect: the magic number does not match.");
        }

        var version = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[magic.Length..]);
        if (version != CompiledEffectFormat.Version) {
            throw new InvalidDataException(
                $"Compiled effect is version {version}; this reader understands {CompiledEffectFormat.Version}."
            );
        }

        var headerLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[(magic.Length + 4)..]);
        var headerStart = magic.Length + 8;

        if (headerLength < 0 || headerStart + headerLength > bytes.Length) {
            throw new InvalidDataException("Compiled effect is truncated: the header runs past the end.");
        }

        var header = JsonSerializer.Deserialize<CompiledEffectFormat.Header>(
            bytes.Slice(headerStart, headerLength),
            CompiledEffectFormat.Json
        ) ?? throw new InvalidDataException("Compiled effect has an unreadable header.");

        var payload = bytes[(headerStart + headerLength)..];
        var modules = ImmutableArray.CreateBuilder<EffectModule>(header.Modules.Count);

        foreach (var module in header.Modules) {
            if (module.Offset < 0 || module.Length < 0 || module.Offset + module.Length > payload.Length) {
                throw new InvalidDataException(
                    $"Compiled effect is truncated: module '{module.Name}' claims bytes past the end."
                );
            }

            modules.Add(
                new(module.Stage, module.Name, [.. payload.Slice(module.Offset, module.Length)], module.IsBinary)
            );
        }

        return new() {
            Name = header.Name,
            Target = header.Target,
            Modules = modules.ToImmutable(),
            Reflection = header.Reflection,
            PermutationKey = header.PermutationKey.ToImmutableSortedDictionary(StringComparer.Ordinal),
            SourceHash = header.SourceHash
        };
    }

    /// <summary>Deserialises from a file.</summary>
    public static CompiledEffect ReadFile(string path) => Read(File.ReadAllBytes(path));
}
