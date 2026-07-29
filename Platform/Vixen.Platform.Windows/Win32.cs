// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vixen.Platform.Windows;

/// <summary>One logical processor's place in the machine: which group, and which bit of it.</summary>
/// <remarks>
///     A <c>GROUP_AFFINITY</c>. Windows numbers processors within a group of at most 64, because the
///     mask is one machine word, and a machine with more than 64 of them has more than one group. A
///     32-core desktop never sees a second group and a dual-socket server always does, which is why
///     this is the shape the affinity calls take rather than a bare mask.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
unsafe struct GroupAffinity {
    public nuint Mask;
    public ushort Group;

    // Three reserved words the kernel writes and nobody reads. A fixed buffer rather than three
    // named fields, because three fields nothing ever assigns are three things every analyser has
    // an opinion about and this is one thing with a size.
    fixed ushort reserved[3];
}

/// <summary>A <c>PROCESSOR_RELATIONSHIP</c>: one physical core, and how fast it is.</summary>
[StructLayout(LayoutKind.Sequential)]
unsafe struct ProcessorRelationship {
    public byte Flags;

    /// <summary>Higher is faster. Zero on a machine whose cores are all the same.</summary>
    public byte EfficiencyClass;

    fixed byte reserved[20];

    public ushort GroupCount;

    /// <summary>The first of <see cref="GroupCount" /> masks, the rest following it in memory.</summary>
    public GroupAffinity FirstGroupMask;
}

/// <summary>A <c>SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX</c>, of the processor-core kind.</summary>
/// <remarks>
///     The real structure is a union over five relationship kinds and this is only the one
///     <see cref="Win32.RelationProcessorCore" /> returns. Reading it as this shape is safe because
///     the caller asks for one kind at a time and <c>Size</c> is what walks the buffer, not
///     <c>sizeof</c>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
struct LogicalProcessorInformation {
    public int Relationship;
    public int Size;
    public ProcessorRelationship Processor;
}

/// <summary>A <c>SYSTEM_POWER_STATUS</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
struct SystemPowerStatus {
    public byte AcLineStatus;
    public byte BatteryFlag;
    public byte BatteryLifePercent;

    /// <summary><c>1</c> when the user has battery saver on. The one thing here SDL cannot see.</summary>
    public byte SystemStatusFlag;

    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;
}

/// <summary>A <c>COMDLG_FILTERSPEC</c>: what to call a file type, and what matches it.</summary>
[StructLayout(LayoutKind.Sequential)]
struct FilterSpec {
    public nint Name;
    public nint Spec;
}

/// <summary>The Win32 entry points this assembly needs, and nothing else.</summary>
/// <remarks>
///     <para>
///         <c>[LibraryImport]</c> throughout, so the marshalling is generated at compile time and
///         there is nothing for ILC to complain about — <c>Directory.Build.props</c> makes every
///         assembly under <c>Platform/</c> AOT- and trim-clean, and iOS makes that mandatory rather
///         than aspirational even though this assembly will never run there.
///     </para>
///     <para>
///         Nothing here is public. A P/Invoke on a public surface is a promise about an operating
///         system's ABI, which is not ours to make.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
static unsafe partial class Win32 {
    public const int RelationProcessorCore = 0;

    public const uint CfDib = 8;
    public const uint CfDibV5 = 17;

    public const uint GmemMoveable = 0x0002;

    public const int SigdnFileSysPath = unchecked((int)0x80058000);

    public const uint ClsctxInprocServer = 0x1;

    /// <summary><c>COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE</c>.</summary>
    public const uint CoinitApartmentThreaded = 0x2 | 0x4;

    public const uint FosOverwritePrompt = 0x00000002;
    public const uint FosPickFolders = 0x00000020;
    public const uint FosForceFileSystem = 0x00000040;
    public const uint FosAllowMultiSelect = 0x00000200;
    public const uint FosPathMustExist = 0x00000800;
    public const uint FosFileMustExist = 0x00001000;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetClipboardData(uint format, nint data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalFree(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial void* GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint GlobalSize(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLogicalProcessorInformationEx(
        int relationship,
        byte* buffer,
        uint* returnedLength
    );

    [LibraryImport("kernel32.dll")]
    public static partial ushort GetActiveProcessorGroupCount();

    [LibraryImport("kernel32.dll")]
    public static partial uint GetActiveProcessorCount(ushort group);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadGroupAffinity(nint thread, in GroupAffinity affinity, out GroupAffinity previous);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetThreadGroupAffinity(nint thread, out GroupAffinity affinity);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(void* reserved, uint model);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(
        in Guid classId,
        void* outer,
        uint context,
        in Guid interfaceId,
        void** instance
    );

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(void* memory);

    [LibraryImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName",
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial int ShCreateItemFromParsingName(
        string path,
        void* bindContext,
        in Guid interfaceId,
        void** item
    );
}
