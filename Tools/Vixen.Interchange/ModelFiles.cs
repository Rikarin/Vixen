// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Assimp;
using AssimpFile = Silk.NET.Assimp.File;

namespace Vixen.Interchange;

/// <summary>
///     The files one Assimp import may open: the model, from bytes the caller already holds, and whatever it names
///     beside itself, from the caller's resolver. An OBJ's material library and a glTF's buffers are such files.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why the model is not handed over as bytes alone.</b> <c>aiImportFileFromMemory</c> reads the model from
///         memory and everything it names from the process's working directory, which is nowhere the model was. An OBJ
///         read that way never found its <c>mtllib</c> and every material came back as Assimp's default grey, and a
///         <c>.gltf</c> with its buffer in a <c>.bin</c> beside it did not read at all.
///     </para>
///     <para>
///         <b>Through a resolver rather than a path</b>, so the host decides how a side file is read: a project reads
///         it sharing write and delete, so an artist saving the material library is never refused, and an asset
///         pipeline reads it through its own file system. No resolver is a model that names nothing beside it.
///     </para>
///     <para>
///         One instance per import, reached from native code through a <see cref="GCHandle" /> in
///         <c>aiFileIO.UserData</c>. Every callback catches everything: an exception that unwinds into Assimp is a
///         crash the pipeline cannot catch, so a resolver that throws is a file that is not there, and the reason is
///         kept for the import's report.
///     </para>
/// </remarks>
sealed unsafe class ModelFiles {
    readonly string model;
    readonly byte* bytes;
    readonly long length;
    readonly Func<string, byte[]?>? resolve;
    readonly List<string> problems = [];

    /// <param name="model">The name the model is opened by.</param>
    /// <param name="bytes">Its bytes, pinned for as long as the import runs.</param>
    /// <param name="length">How many.</param>
    /// <param name="resolve">Reads a file the model names, as the model spells it, or answers null.</param>
    ModelFiles(string model, byte* bytes, long length, Func<string, byte[]?>? resolve) {
        this.model = model;
        this.bytes = bytes;
        this.length = length;
        this.resolve = resolve;
    }

    /// <summary>What went wrong reading side files, worded for the import's report.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Runs an import with these files behind it.</summary>
    /// <param name="model">The name the model is opened by, whose extension tells Assimp what it is.</param>
    /// <param name="bytes">The model's bytes.</param>
    /// <param name="resolve">Reads a file the model names, or answers null for one that is not there.</param>
    /// <param name="import">The import, given the file system to pass Assimp.</param>
    /// <returns>What the import returned, and what went wrong reading side files.</returns>
    public static (T Result, IReadOnlyList<string> Problems) Run<T>(
        string model,
        ReadOnlySpan<byte> bytes,
        Func<string, byte[]?>? resolve,
        Func<IntPtr, T> import
    ) {
        fixed (byte* data = bytes) {
            var files = new ModelFiles(model, data, bytes.Length, resolve);
            var handle = GCHandle.Alloc(files);
            try {
                var io = new FileIO {
                    OpenProc = new(&Open), CloseProc = new(&Close), UserData = (byte*)GCHandle.ToIntPtr(handle)
                };
                return (import((IntPtr)(&io)), files.Problems);
            } finally {
                handle.Free();
            }
        }
    }

    /// <summary>The bytes of one file, or null when it is not there.</summary>
    OpenFile? Find(string path) {
        if (string.Equals(path, model, StringComparison.Ordinal)) {
            return new(bytes, length, default);
        }

        if (resolve is null) {
            return null;
        }

        try {
            if (resolve(path) is not { } read) {
                return null;
            }

            var pin = GCHandle.Alloc(read, GCHandleType.Pinned);
            return new((byte*)pin.AddrOfPinnedObject(), read.Length, pin);
        } catch (Exception problem) {
            problems.Add($"'{path}' could not be read: {problem.Message}");
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static AssimpFile* Open(FileIO* io, byte* path, byte* mode) {
        try {
            var files = (ModelFiles)GCHandle.FromIntPtr((IntPtr)io->UserData).Target!;
            var name = Marshal.PtrToStringUTF8((IntPtr)path) ?? string.Empty;

            // Writing is never asked for by an import, and nothing here could honour it.
            if (Marshal.PtrToStringUTF8((IntPtr)mode) is { } asked && asked.Contains('w', StringComparison.Ordinal)) {
                return null;
            }

            if (files.Find(name) is not { } found) {
                return null;
            }

            var file = (AssimpFile*)NativeMemory.AllocZeroed((nuint)sizeof(AssimpFile));
            file->ReadProc = new(&Read);
            file->WriteProc = new(&Write);
            file->TellProc = new(&Tell);
            file->FileSizeProc = new(&Size);
            file->SeekProc = new(&Seek);
            file->FlushProc = new(&Flush);
            file->UserData = (byte*)GCHandle.ToIntPtr(GCHandle.Alloc(found));
            return file;
        } catch {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Close(FileIO* io, AssimpFile* file) {
        if (file is null) {
            return;
        }

        var handle = GCHandle.FromIntPtr((IntPtr)file->UserData);
        ((OpenFile)handle.Target!).Release();
        handle.Free();
        NativeMemory.Free(file);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static nuint Read(AssimpFile* file, byte* buffer, nuint size, nuint count) {
        if (size == 0 || count == 0) {
            return 0;
        }

        var open = Of(file);
        var whole = (ulong)Math.Max(open.Length - open.Position, 0) / size;
        var taken = Math.Min(whole, count);
        var span = (long)(taken * size);
        System.Buffer.MemoryCopy(open.Bytes + open.Position, buffer, span, span);
        open.Position += span;
        return (nuint)taken;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static nuint Write(AssimpFile* file, byte* buffer, nuint size, nuint count) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static nuint Tell(AssimpFile* file) => (nuint)Of(file).Position;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static nuint Size(AssimpFile* file) => (nuint)Of(file).Length;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static Return Seek(AssimpFile* file, nuint offset, Origin origin) {
        var open = Of(file);
        var to = origin switch {
            Origin.Set => (long)offset,
            Origin.Cur => open.Position + (long)offset,
            Origin.End => open.Length - (long)offset,
            _ => -1
        };
        if (to < 0 || to > open.Length) {
            return Return.Failure;
        }

        open.Position = to;
        return Return.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Flush(AssimpFile* file) { }

    static OpenFile Of(AssimpFile* file) => (OpenFile)GCHandle.FromIntPtr((IntPtr)file->UserData).Target!;

    /// <summary>One open file: its bytes, where the next read starts, and the pin that holds a side file still.</summary>
    sealed class OpenFile(byte* bytes, long length, GCHandle pin) {
        public byte* Bytes { get; } = bytes;

        public long Length { get; } = length;

        public long Position { get; set; }

        public void Release() {
            if (pin.IsAllocated) {
                pin.Free();
            }
        }
    }
}
