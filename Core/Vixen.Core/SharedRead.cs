// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Core;

/// <summary>
///     Reads a file as <c>File.ReadAllBytes</c>, <c>File.ReadAllText</c> and <c>File.OpenRead</c> do, but lets anybody
///     else write, replace or delete it meanwhile.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The framework's readers share reading only.</b> On Windows anybody who opens the file to write it, or
///         deletes it, while one of them reads is refused with "being used by another process". A host that saves a
///         document Vixen happens to be reading — a texture graph, a picture, a shader cache entry another process is
///         filling — then fails its save (Patina #277). Share modes are advisory on Linux and macOS, so this only ever
///         shows on Windows.
///     </para>
///     <para>
///         A read that meets a file being rewritten may see it part written; every reader here reads a whole file and
///         checks what it read, as it must anyway for a file another program wrote. <c>SharedReadTests</c> sweeps the
///         libraries a host reads its users' files through for a framework reader.
///     </para>
/// </remarks>
public static class SharedRead {
    /// <summary>What anybody else may do to a file while one of these reads it.</summary>
    public static FileShare Share => FileShare.ReadWrite | FileShare.Delete;

    /// <summary>Opens a file to read, as <c>File.OpenRead</c> does but sharing write and delete.</summary>
    /// <param name="path">The file.</param>
    /// <param name="asynchronous">Whether the handle is for asynchronous reads.</param>
    public static FileStream Open(string path, bool asynchronous = false) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return new(
            path,
            new FileStreamOptions {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = Share,
                Options = FileOptions.SequentialScan | (asynchronous ? FileOptions.Asynchronous : FileOptions.None)
            }
        );
    }

    /// <summary>A whole file's bytes, read to its end, so one that grows or shrinks meanwhile is still whole.</summary>
    /// <param name="path">The file.</param>
    public static byte[] Bytes(string path) {
        using var file = Open(path);
        using MemoryStream bytes = new(file.Length <= Array.MaxLength ? (int)file.Length : 0);
        file.CopyTo(bytes);

        return bytes.Length == bytes.Capacity ? bytes.GetBuffer() : bytes.ToArray();
    }

    /// <summary>A whole file's text, UTF-8 unless a byte order mark says otherwise.</summary>
    /// <param name="path">The file.</param>
    public static string Text(string path) {
        using StreamReader reader = new(Open(path), Encoding.UTF8, true);

        return reader.ReadToEnd();
    }
}
