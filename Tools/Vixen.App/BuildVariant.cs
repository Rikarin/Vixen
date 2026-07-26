// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;

namespace Vixen.App;

/// <summary>Which of the five builds this is.</summary>
/// <remarks>
///     Orthogonal to platform: any of these can be built for any target. The full matrix and what
///     each one turns on is <c>docs/plan/17 § Build variants</c>; this enum is the runtime half of
///     it, so a subsystem can ask rather than guess from <c>#if DEBUG</c>.
/// </remarks>
public enum BuildVariant {
    /// <summary>Loose files, live import, everything on, JIT. The editor itself.</summary>
    Editor = 0,

    /// <summary>Loose files or bundles, assertions on, hot reload. Daily development.</summary>
    Debug = 1,

    /// <summary>
    ///     Bundles and an optimised build that still has the profiler, the console and the remote
    ///     inspector.
    /// </summary>
    /// <remarks>
    ///     The variant teams discover they need and engines often omit. Without it "it only
    ///     reproduces in release" is undiagnosable, which is why <c>docs/plan/17</c> gives it a row
    ///     of its own rather than treating it as release with a flag.
    /// </remarks>
    Development = 2,

    /// <summary>Bundles, assertions off, log ring and crash reporter only. Shipping.</summary>
    Release = 3,

    /// <summary>
    ///     A dedicated server: bundles with no textures, audio or shaders, no window, no GPU, full
    ///     logging and a metrics endpoint.
    /// </summary>
    Server = 4
}

/// <summary>Declares the variant an application was built as.</summary>
/// <remarks>
///     Applied to the entry assembly, normally by the SDK from the build configuration rather than
///     by hand. It exists because the variant is a build-time fact that has to survive into a
///     running process, and the alternatives are worse: <c>#if</c> cannot express five states across
///     packages that ship once, and an environment variable can be wrong.
/// </remarks>
/// <param name="variant">The variant this assembly was built as.</param>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class BuildVariantAttribute(BuildVariant variant) : Attribute {
    /// <summary>The variant this assembly was built as.</summary>
    public BuildVariant Variant { get; } = variant;
}

/// <summary>Which variant is running.</summary>
public static class BuildVariants {
    static BuildVariant? resolved;

    /// <summary>
    ///     The variant this process is running as, resolved once from three sources in order: the
    ///     <c>--vixen-variant</c> argument, a <see cref="BuildVariantAttribute" /> on the entry
    ///     assembly, and finally the compilation's own <c>DEBUG</c> flag.
    /// </summary>
    /// <remarks>
    ///     The last of those is a fallback and not a good one — it says nothing about whether
    ///     content is bundled or diagnostics are present — which is exactly why the attribute
    ///     exists. It is here so that a bare <c>dotnet run</c> of a project with no Vixen SDK still
    ///     answers something sensible.
    /// </remarks>
    public static BuildVariant Current => resolved ??= Detect(null);

    /// <summary>Resolves the variant, letting a command-line override win.</summary>
    /// <param name="requested">The variant named on the command line, or <see langword="null" />.</param>
    /// <returns>The variant to run as.</returns>
    public static BuildVariant Detect(BuildVariant? requested) {
        if (requested is { } explicitly) {
            return explicitly;
        }

        var declared = Assembly.GetEntryAssembly()?.GetCustomAttribute<BuildVariantAttribute>();

        if (declared is not null) {
            return declared.Variant;
        }

#if DEBUG
        return BuildVariant.Debug;
#else
        return BuildVariant.Release;
#endif
    }

    /// <summary>Whether this variant expects to open a window.</summary>
    /// <remarks>
    ///     <see cref="BuildVariant.Server" /> does not, and that is the whole of the difference at
    ///     boot: the host picks the headless platform and every subsystem takes the path it already
    ///     had to have.
    /// </remarks>
    public static bool IsHeadless(this BuildVariant variant) => variant == BuildVariant.Server;

    /// <summary>Whether this variant keeps its assertions and validation layers.</summary>
    public static bool HasValidation(this BuildVariant variant) => variant != BuildVariant.Release;

    /// <summary>Whether this variant carries the profiler, console and remote inspector.</summary>
    public static bool HasDiagnostics(this BuildVariant variant) => variant != BuildVariant.Release;

    internal static void Reset() => resolved = null;
}
