// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Raven.Transpile.Tests;

/// <summary>
///     Whether SPIRV-Cross's native library loaded, said once, the way a missing driver is said.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Six of nine cases in this suite were reporting themselves as defects in the shader
///         compiler on the ubuntu leg, and none of them is one</b> (#1027). Every one died inside
///         <c>Cross.GetApi()</c> with <c>FileNotFoundException: Could not load from any of the
///         possible library names</c>; the three that passed are the three that never reach the
///         native library. A missing library is an environment fact and a suite has to say which it
///         is, because six red cases with a stack trace each read as six code failures and a leg
///         nobody reads.
///     </para>
///     <para>
///         ⚠ <b>Skipping alone would be worse than the red it replaces</b>, which is the failure
///         this repository has already paid for: two CI legs never installed shaderc, every case
///         returned early, and the differential oracle reported a pass on both without running.
///         So the skip is gated the way <c>VIXEN_REQUIRE_VULKAN</c> gates the Vulkan suites — a leg
///         that says it has the library <em>fails</em> when it does not, and only a leg that has
///         promised nothing is allowed to skip. <c>ci.yml</c> sets the promise on macOS and Windows,
///         where the library has been observed to load, and deliberately not on ubuntu, which is
///         #1027's open half.
///     </para>
///     <para>
///         ⚠ And unlike a driver, this library is a <em>restored</em> asset:
///         <c>Silk.NET.SPIRV.Cross.Native</c> ships <c>libspirv-cross.so</c> for linux-x64 and the
///         file needs nothing but glibc, libm, libpthread and libdl — so "it is not installed" is
///         not an explanation here, and the promise variable is what stops that turning into a
///         permanent shrug.
///     </para>
///     <para>
///         ⚠ <b>And the explanation, which was #1027's open half: shipping the native is not the
///         same as finding it.</b> <c>Cross.GetApi()</c> does not go through a <c>DllImport</c>, so
///         the .NET host's NATIVE_DLL_SEARCH_DIRECTORIES — which does include
///         <c>runtimes/&lt;rid&gt;/native</c> — is not what resolves it. Silk.NET's own loader is,
///         and what that finds differs per platform. <c>Directory.Packages.props</c> already
///         records the same thing for Assimp, measured on Linux: the loader does not read
///         <c>runtimes/</c> there, and the identical file beside the assembly loads. Deleting
///         <c>runtimes/osx-arm64</c> out of this suite's output reproduces the ubuntu run here
///         exactly — six skipped, three passed — and putting the dylib flat beside the assembly
///         with that directory still gone turns all eleven green.
///         <c>Raven/Directory.Build.targets</c> makes that copy at build time now.
///     </para>
///     <para>
///         So a skip here should be rare rather than normal, and the ubuntu leg's skip count is
///         what says whether the copy worked on the platform nobody here can run. The promise
///         stays off on that leg until it reads zero, which is the one direction of evidence a
///         leg that already skips can give.
///     </para>
/// </remarks>
static class CrossCompilerRequirement {
    /// <summary>The environment variable a leg sets to say it has the library.</summary>
    public const string Promise = "VIXEN_REQUIRE_SPIRV_CROSS";

    /// <summary>Passes when SPIRV-Cross loaded; otherwise fails if promised, and skips if not.</summary>
    public static void Available() {
        if (SpirvCrossTranspiler.TryLoad(out var reason)) {
            return;
        }

        var said = $"SPIRV-Cross's native library (Silk.NET.SPIRV.Cross.Native, "
            + $"runtimes/<rid>/native/libspirv-cross) did not load: {reason}";

        if (Environment.GetEnvironmentVariable(Promise) is "1" or "true" or "TRUE") {
            Assert.Fail($"{Promise} is set, so this may not be skipped. {said}");
        }

        Assert.Skip(said);
    }
}
