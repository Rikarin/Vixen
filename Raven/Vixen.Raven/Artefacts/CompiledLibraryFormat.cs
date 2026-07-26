// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vixen.Raven.Artefacts;

/// <summary>
///     The <c>.rvnlib</c> container: a magic number, a version, a payload length, and the whole
///     library as JSON.
/// </summary>
/// <remarks>
///     <para>
///         All JSON, unlike <c>.rvnfx</c>, and for the same reason that format keeps its header in
///         JSON: a shipped artefact should be inspectable without a bespoke viewer, and the schema
///         will grow fields. <c>.rvnfx</c> appends its modules raw because SPIR-V is already
///         compact binary and base64 would cost a third of its size for nothing. A library has no
///         such payload — its bodies are structure, not bytes — so there is nothing to keep out
///         of the JSON, and a diffable artefact is worth more than the space.
///     </para>
///     <para>
///         The length prefix is not redundant with "JSON runs to the end of the file". It is what
///         makes a truncation report itself as a truncation rather than as a parse error a few
///         thousand lines in, which is the same treatment <c>.rvnfx</c> gives a short module.
///     </para>
///     <para>
///         Every integer is little-endian, matching <c>.rvnfx</c> and SPIR-V.
///     </para>
/// </remarks>
public static class CompiledLibraryFormat {
    /// <summary>Magic number: <c>RVNLIB</c> followed by <c>0x1A</c>.</summary>
    /// <remarks>
    ///     The trailing byte is the DOS end-of-file character, as PNG and <c>.rvnfx</c> both use:
    ///     a tool that treats the file as text stops there. Written as an escape so it is visible
    ///     in source and survives an editor that re-encodes.
    /// </remarks>
    public static ReadOnlySpan<byte> Magic => "RVNLIB\u001a"u8;

    /// <summary>
    ///     Format version. Bump on any change a previous reader would misread; the reader rejects
    ///     anything it does not know rather than guessing.
    /// </summary>
    public const int Version = 1;

    /// <summary>The conventional extension, so callers do not spell it themselves.</summary>
    public const string Extension = ".rvnlib";

    internal static readonly JsonSerializerOptions Json = new() {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    ///     The same schema, indented — for <c>--emit-library-json</c> and for a test that wants to
    ///     read what it produced.
    /// </summary>
    internal static readonly JsonSerializerOptions IndentedJson = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>Writes a <see cref="CompiledLibrary" /> as <c>.rvnlib</c>.</summary>
public static class CompiledLibraryWriter {
    /// <summary>Serialises to bytes.</summary>
    public static byte[] Write(CompiledLibrary library) {
        ArgumentNullException.ThrowIfNull(library);

        var payload = JsonSerializer.SerializeToUtf8Bytes(library, CompiledLibraryFormat.Json);

        var output = new MemoryStream(CompiledLibraryFormat.Magic.Length + 8 + payload.Length);
        output.Write(CompiledLibraryFormat.Magic);
        WriteInt32(output, CompiledLibraryFormat.Version);
        WriteInt32(output, payload.Length);
        output.Write(payload);

        return output.ToArray();
    }

    /// <summary>Serialises to a file.</summary>
    public static void WriteFile(string path, CompiledLibrary library) => File.WriteAllBytes(path, Write(library));

    /// <summary>
    ///     The library as indented JSON, with no container around it. For inspection and for the
    ///     tests; the reader does not accept it, which is the point of the magic number.
    /// </summary>
    public static string WriteJson(CompiledLibrary library) =>
        JsonSerializer.Serialize(library, CompiledLibraryFormat.IndentedJson);

    static void WriteInt32(Stream stream, int value) {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}

/// <summary>Reads a <c>.rvnlib</c> back.</summary>
public static class CompiledLibraryReader {
    /// <summary>Deserialises from bytes.</summary>
    /// <exception cref="InvalidDataException">
    ///     The bytes are not a <c>.rvnlib</c>, are a version this reader does not know, or are
    ///     truncated. Each is reported rather than half-read: a partly-loaded library would
    ///     surface as a missing member on a type whose source nobody has.
    /// </exception>
    public static CompiledLibrary Read(ReadOnlySpan<byte> bytes) {
        var magic = CompiledLibraryFormat.Magic;

        if (bytes.Length < magic.Length + 8 || !bytes[..magic.Length].SequenceEqual(magic)) {
            throw new InvalidDataException("Not a Raven compiled library: the magic number does not match.");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes[magic.Length..]);
        if (version != CompiledLibraryFormat.Version) {
            throw new InvalidDataException(
                $"Compiled library is version {version}; this reader understands {CompiledLibraryFormat.Version}."
            );
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[(magic.Length + 4)..]);
        var start = magic.Length + 8;

        if (length < 0 || start + length > bytes.Length) {
            throw new InvalidDataException("Compiled library is truncated: the payload runs past the end.");
        }

        try {
            return JsonSerializer.Deserialize<CompiledLibrary>(
                bytes.Slice(start, length),
                CompiledLibraryFormat.Json
            ) ?? throw new InvalidDataException("Compiled library has an unreadable payload.");
        } catch (JsonException exception) {
            throw new InvalidDataException($"Compiled library has an unreadable payload: {exception.Message}");
        }
    }

    /// <summary>Deserialises from a file.</summary>
    public static CompiledLibrary ReadFile(string path) => Read(File.ReadAllBytes(path));
}
