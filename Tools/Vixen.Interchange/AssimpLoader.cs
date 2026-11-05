// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Vixen.Platform.Native;
using AssimpApi = Silk.NET.Assimp.Assimp;

namespace Vixen.Interchange;

/// <summary>Finding Assimp: the copy the application carries, and only then the machine's.</summary>
/// <remarks>
///     <para>
///         <c>Assimp.GetApi()</c> is not called, because it decides where to look through Silk.NET's
///         own <c>DefaultPathResolver</c>, and that looks in a different set of places on every
///         platform. On Linux it does not read <c>runtimes/linux-x64/native</c>, which is where every
///         build without a runtime identifier leaves the file: the binding brought its native and a
///         distribution's <c>libassimp5</c> was needed anyway, and a distribution without that package
///         (Ubuntu 26.04) could read no model at all.
///     </para>
///     <para>
///         <b>Both layouts are one search.</b> A build keeps natives in
///         <c>runtimes/&lt;rid&gt;/native/</c> and <c>dotnet publish -r</c> flattens them beside the
///         executable, and <see cref="NativeSearch" /> already names both, in the order
///         <c>VulkanLoader</c> uses. So there is no copying step to keep in step with a project
///         reference, which a package's targets would not reach anyway.
///     </para>
///     <para>
///         ⚠ <b>The file name is the binding's, spelt out, and never the unversioned one.</b>
///         <c>NativeLibraryNames</c> would try <c>libassimp.so</c> first, a development symlink that
///         on a machine with Assimp 6 leads to Assimp 6. <c>Silk.NET.Assimp</c> 2.23.0 binds Assimp
///         5's C ABI, and a 6 would load and then be wrong in ways a signature cannot catch.
///         <c>Ultz.Native.Assimp</c> carries both majors, so the major is the one thing this may not
///         get wrong. <c>AssimpLoaderTests</c> holds these names to the binding's.
///     </para>
///     <para>
///         ⚠ <b>Windows has no choice and is the exception.</b> Its one <c>Assimp64.dll</c> is 6.0.2 by
///         its version resource, so that platform already runs this binding over a 6. The reader's
///         tests pass over the macOS 6 too, which says this surface survives it and does not say the
///         ABIs match.
///     </para>
///     <para>
///         The bare soname comes last, for a platform the package has no native for, such as musl.
///         It can only find a library that declares itself ABI 5.
///     </para>
/// </remarks>
static class AssimpLoader {
    /// <summary>The library <see cref="Load" /> opened, for logging and for a bug report.</summary>
    public static string? ResolvedPath { get; private set; }

    /// <summary>Opens Assimp over the first candidate that loads.</summary>
    /// <returns>The API.</returns>
    /// <exception cref="DllNotFoundException">No candidate loaded. The message says what was tried.</exception>
    public static AssimpApi Load() {
        var refusals = new List<string>();
        var candidates = Candidates(
            NativeLibraries.BaseDirectory,
            NativeRid.Chain,
            FileName(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), Environment.Is64BitProcess)
        ).ToArray();

        foreach (var candidate in candidates) {
            if (Open(candidate, refusals) is var handle && handle != 0) {
                ResolvedPath = candidate;
                return new(new LamdaNativeContext(
                    name => NativeLibrary.TryGetExport(handle, name, out var address) ? address : 0
                ));
            }
        }

        throw new DllNotFoundException(Failure(candidates, refusals));
    }

    /// <summary>What the binding calls the library on a platform.</summary>
    /// <param name="windows">Whether to spell it Windows's way.</param>
    /// <param name="macOS">Whether to spell it macOS's way.</param>
    /// <param name="is64Bit">Whether the process is 64-bit, which only Windows's name says.</param>
    /// <returns>The file name, with the major of the ABI the binding was generated from.</returns>
    /// <remarks>Takes the platform as arguments so one machine's tests can read all of them.</remarks>
    internal static string FileName(bool windows, bool macOS, bool is64Bit) =>
        windows ? is64Bit ? "Assimp64.dll" : "Assimp32.dll"
        : macOS ? "libassimp.5.dylib"
        : "libassimp.so.5";

    /// <summary>Every path to try, in order, without trying any of them.</summary>
    /// <param name="baseDirectory">The application's directory.</param>
    /// <param name="ridChain">The runtime identifiers to accept, most specific first.</param>
    /// <param name="fileName">What the library is called.</param>
    /// <returns>The application's copies, then the bare name for the dynamic linker to search.</returns>
    internal static IEnumerable<string> Candidates(
        string baseDirectory,
        IReadOnlyList<string> ridChain,
        string fileName
    ) =>
        NativeSearch.Paths(NativeSearch.Directories(baseDirectory, ridChain), [fileName]).Append(fileName);

    /// <summary>Loads one candidate, keeping the linker's reason when the file is there and refuses.</summary>
    /// <param name="candidate">A full path, or a bare name.</param>
    /// <param name="refusals">Where the reason goes.</param>
    /// <returns>The handle, or zero.</returns>
    /// <remarks>
    ///     ⚠ <b>A file that will not load is a different report from a file that is absent.</b> Too old a
    ///     <c>libstdc++</c> looks like this, and its fix is not the one "not found" suggests.
    /// </remarks>
    static nint Open(string candidate, List<string> refusals) {
        if (Path.IsPathRooted(candidate) && !File.Exists(candidate)) {
            return 0;
        }

        try {
            return NativeLibrary.Load(candidate);
        } catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException) {
            if (File.Exists(candidate)) {
                refusals.Add(exception.Message);
            }

            return 0;
        }
    }

    static string Failure(IEnumerable<string> candidates, IReadOnlyCollection<string> refusals) {
        var tried = $"Assimp could not be loaded. Tried: {string.Join(", ", candidates)}.";

        return refusals.Count == 0
            ? $"{tried} The application should carry it, from the Ultz.Native.Assimp package this assembly depends on."
            : $"{tried} {string.Join(" ", refusals)}";
    }
}
