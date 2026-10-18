// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The one way the eight committed UI modules are built, executable rather than described.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>"Run <c>glslc</c>" was never a recipe, because which <c>glslc</c> decides the bytes.</b>
///         #1257 found the eight split three <c>-O</c> to five <c>-O0</c> and the ledger grew a column
///         for it — but on the machine that regenerates them most there is no <c>glslc</c> on PATH at
///         all, and the one compiler on disk that answers to the name, the Android NDK r23c's shaderc
///         v2021.0, rebuilds <i>none</i> of the eight: an older glslang, so generator version 10
///         where every committed module says 11, and one <c>NonWritable</c> decoration short on the
///         mask's buffer. Following the instruction with that compiler produces eight valid modules
///         that are all different from the committed ones, and the ledger accepted the result in the
///         same breath as it would accept the right one.
///     </para>
///     <para>
///         <b>So the recipe is a test.</b> <see cref="TheRecipeRebuildsEveryCommittedModuleByteForByte" />
///         drives <c>shaderc</c>'s C API — the library <c>glslc</c> is a front end for — with exactly
///         what <c>glslc</c> passes it for these files: GLSL, the stage from the extension, entry
///         point <c>main</c>, Vulkan 1.0's defaults, and the optimisation level the ledger's fourth
///         column records. (No include resolver: shaderc declares <c>GL_GOOGLE_include_directive</c>
///         in every module whether one is installed or not — measured, a run without one rebuilds all
///         eight — and no UI shader includes anything.) It compares the result against the committed module byte for byte,
///         and under <c>VIXEN_REBUILD_UI_MODULES=1</c> it writes it. A compiler that does not
///         reproduce the committed eight is refused before it writes one.
///     </para>
///     <para>
///         ⚠ <b>Measured, on Windows:</b> the <c>shaderc_shared.dll</c> Blender 4.5 ships reproduces
///         all eight byte-for-byte. That is the reference build; any other library that passes the
///         comparison is the same recipe by definition, and one that fails it is not — Blender 4.0's,
///         an older glslang at generator version 10 like the NDK's, is refused on the first module.
///     </para>
///     <para>
///         <b>And a module built any other way is red without a compiler.</b>
///         <see cref="EveryCommittedModuleCarriesTheRecipesHeader" /> holds what a module says about
///         its own making to what this recipe produces: SPIR-V 1.0, generator <c>shaderc over
///         glslang</c> at version 11, and none of the instructions <c>-g</c> adds. The NDK's
///         <c>glslc</c>, a <c>--target-env=vulkan1.1</c>, a <c>-g</c> or another front end entirely each
///         move one of those. What it cannot see is a different shaderc at the <i>same</i> generator
///         version — only a compiler can, which is the test above.
///     </para>
/// </remarks>
public partial class SharedUiShaderTests {
    /// <summary>The SPIR-V version the recipe targets, Vulkan 1.0's: 1.0.</summary>
    internal const uint RecipeVersion = 0x00010000;

    /// <summary>The generator word: tool 13 (<c>shaderc over glslang</c>), glslang's generator version 11.</summary>
    internal const uint RecipeGenerator = 0x000D000B;

    /// <summary>Where the recipe's compiler is, from <c>VIXEN_SHADERC</c>: a shaderc shared library.</summary>
    static string? ShadercLibrary => Environment.GetEnvironmentVariable("VIXEN_SHADERC") is { Length: > 0 } path ? path : null;

    /// <summary>Writes the rebuilt modules rather than only comparing them.</summary>
    static bool Rebuilding =>
        Environment.GetEnvironmentVariable("VIXEN_REBUILD_UI_MODULES") is "1" or "true" or "TRUE";

    /// <summary>
    ///     The flavour to rebuild all eight in, from <c>VIXEN_REBUILD_UI_FLAVOUR</c> (<c>-O</c> or
    ///     <c>-O0</c>), or <see langword="null" /> to keep each module's recorded one.
    /// </summary>
    /// <remarks>
    ///     Honoured only with <see cref="Reflavouring" /> set as well, so that normalising the eight is
    ///     the same two-key decision the ledger already makes it — see <see cref="Flavour" />.
    /// </remarks>
    static string? RebuildFlavour => Environment.GetEnvironmentVariable("VIXEN_REBUILD_UI_FLAVOUR") is { Length: > 0 } flavour ? flavour : null;

    /// <summary>The instruction every regeneration message gives, in one place so they cannot disagree.</summary>
    internal const string RecipeInstruction =
        "Rebuild with the recipe, not with whichever glslc is to hand: set VIXEN_SHADERC to a shaderc "
        + "shared library that reproduces the committed modules (Blender 4.5's blender.shared/shaderc_shared.dll "
        + "does) and VIXEN_REBUILD_UI_MODULES=1, run `dotnet test Platform/Vixen.Graphics.Golden.Tests "
        + "--filter FullyQualifiedName~TheRecipeRebuildsEveryCommittedModuleByteForByte`, then rerun "
        + "SharedUiShaderTests with VIXEN_UPDATE_SHADER_DIGESTS=1 (to change the eight's flavour, add "
        + "VIXEN_REBUILD_UI_FLAVOUR and VIXEN_UPDATE_SHADER_FLAVOUR=1 to both runs)";

    /// <summary>Every committed UI module is what the recipe builds from the source beside it.</summary>
    /// <remarks>
    ///     <para>
    ///         Skipped without <c>VIXEN_SHADERC</c>, because CI carries no compiler and a skip is the
    ///         honest answer to "was this checked". The skip reason names the variable.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>When rebuilding, the compiler is proved on the modules whose source has not
    ///         moved before it is allowed to write any.</b> A library that reproduces nothing the
    ///         ledger already agrees with is a different recipe, and writing its output would replace
    ///         eight committed artefacts with a stranger's — which is what the NDK's would do.
    ///     </para>
    ///     <para>
    ///         The proof compiles each module in the flavour the ledger records for it, and the output
    ///         is built in that flavour too — or, for the normalisation #1257 leaves to a decision, in
    ///         <see cref="RebuildFlavour" />. So the same run that proves the compiler can change the
    ///         eight's flavour, and only when both keys are turned.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheRecipeRebuildsEveryCommittedModuleByteForByte() {
        if (ShadercLibrary is not { } library) {
            Assert.Skip("VIXEN_SHADERC is not set, so no compiler is available to rebuild the UI modules. " + RecipeInstruction + ".");
            return;
        }

        var recorded = Recorded();

        if (RebuildFlavour is { } wanted) {
            Assert.True(
                Rebuilding && Reflavouring && wanted is Optimised or Unoptimised,
                $"VIXEN_REBUILD_UI_FLAVOUR={wanted} needs VIXEN_REBUILD_UI_MODULES=1 and VIXEN_UPDATE_SHADER_FLAVOUR=1, "
                + $"and is `{Optimised}` or `{Unoptimised}`: changing the eight's flavour moves the arithmetic census."
            );
        }

        using var shaderc = new Shaderc(library);

        var built = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var unchangedSources = 0;
        var reproduced = new List<string>();
        var differing = new List<string>();

        foreach (var name in Names) {
            var source = Path.Combine(RepositoryRoot(), Shaders, name);
            var module = source + ".spv";

            Assert.True(
                recorded.TryGetValue(name, out var line) && line.Flavour is not null,
                $"{name} has no flavour in Shaders/modules.sha256, so the recipe cannot say how to build it."
            );

            var text = File.ReadAllText(source);
            var bytes = shaderc.Compile(text, name, line.Flavour == Optimised);
            built[name] = RebuildFlavour is { } target && target != line.Flavour
                ? shaderc.Compile(text, name, target == Optimised)
                : bytes;

            var same = File.Exists(module) && File.ReadAllBytes(module).AsSpan().SequenceEqual(bytes);
            (same ? reproduced : differing).Add(name);

            if (string.Equals(line.Code, Digest(Encoding.UTF8.GetBytes(Code(text))), StringComparison.Ordinal)) {
                unchangedSources++;

                // A source the ledger still agrees with is one the committed module was built from, so
                // the recipe has to give exactly that module back — or it is not the recipe.
                Assert.True(
                    same,
                    $"{library} built {name} at `{line.Flavour}` differently from the committed module, and the "
                    + "source has not changed since that module was built. This library is not the recipe the "
                    + "eight were built with, and nothing it writes may be committed. Its header says generator "
                    + $"0x{Header(bytes).Generator:x8}; the recipe's is 0x{RecipeGenerator:x8}."
                );
            }
        }

        if (Rebuilding) {
            Assert.True(
                unchangedSources > 0,
                "every UI source has changed since its module was built, so nothing proves this library is the "
                + "recipe before it writes. Rebuild one module first, with one source changed at a time."
            );

            foreach (var (name, bytes) in built) {
                File.WriteAllBytes(Path.Combine(RepositoryRoot(), Shaders, name + ".spv"), bytes);
            }

            return;
        }

        Assert.True(
            differing.Count == 0,
            $"the recipe does not give back {string.Join(", ", differing)}: their sources changed and their "
            + "modules were not rebuilt. " + RecipeInstruction + "."
        );

        Assert.Equal(Names.Length, reproduced.Count);
    }

    /// <summary>Every committed UI module says it was made the way the recipe makes one.</summary>
    /// <remarks>
    ///     The part of the recipe a module carries in its own bytes, checked on every leg — so the
    ///     NDK's <c>glslc</c>, another target environment, <c>-g</c> or another front end is red here
    ///     with no compiler in the process. See this file's remarks for what it cannot see.
    /// </remarks>
    [Fact]
    public void EveryCommittedModuleCarriesTheRecipesHeader() {
        var checkedModules = 0;

        foreach (var name in Names) {
            var module = Path.Combine(RepositoryRoot(), Shaders, name + ".spv");

            var reason = NotTheRecipe(File.ReadAllBytes(module), name);

            Assert.True(reason is null, reason);
            checkedModules++;
        }

        Assert.Equal(8, checkedModules);
    }

    /// <summary>Why a module was not built by the recipe, or <see langword="null" /> if its bytes say it was.</summary>
    /// <param name="bytes">The module.</param>
    /// <param name="name">Its shader's name, for the message.</param>
    internal static string? NotTheRecipe(byte[] bytes, string name) {
        if (bytes.Length < 20 || bytes.Length % 4 != 0) {
            return $"{name}.spv is {bytes.Length} bytes, which is not a SPIR-V module.";
        }

        var (magic, version, generator) = Header(bytes);

        if (magic != 0x07230203) {
            return $"{name}.spv does not start with the SPIR-V magic number.";
        }

        if (version != RecipeVersion) {
            return $"{name}.spv is SPIR-V 0x{version:x8} where the recipe targets Vulkan 1.0's 0x{RecipeVersion:x8} -- "
                + "it was built with another --target-env. " + RecipeInstruction + ".";
        }

        if (generator != RecipeGenerator) {
            return $"{name}.spv carries generator 0x{generator:x8} where the recipe's is 0x{RecipeGenerator:x8} "
                + "(shaderc over glslang, generator version 11) -- it was built by another compiler; the Android "
                + "NDK r23c's glslc says 0x000d000a. " + RecipeInstruction + ".";
        }

        var words = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();

        for (var at = 5; at < words.Length;) {
            var opcode = (int)(words[at] & 0xFFFF);
            var length = (int)(words[at] >> 16);

            if (length == 0) {
                return $"{name}.spv has a zero-length instruction at word {at}.";
            }

            // OpString, OpLine and OpModuleProcessed: what `-g` adds and neither flavour carries.
            if (opcode is 7 or 8 or 330) {
                return $"{name}.spv carries opcode {opcode}, which only `-g` emits -- the recipe never passes it. "
                    + RecipeInstruction + ".";
            }

            at += length;
        }

        return null;
    }

    static (uint Magic, uint Version, uint Generator) Header(byte[] bytes) {
        var words = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan(0, 12));
        return (words[0], words[1], words[2]);
    }

    /// <summary>shaderc's C API, loaded from a path, with exactly the options <c>glslc</c> sets.</summary>
    internal sealed class Shaderc : IDisposable {
        readonly IntPtr library;
        readonly IntPtr compiler;
        readonly Release releaseCompiler;
        readonly OptionsInitialize optionsInitialize;
        readonly Release optionsRelease;
        readonly SetLevel setLevel;
        readonly CompileIntoSpv compile;
        readonly Release resultRelease;
        readonly ResultLength resultLength;
        readonly ResultPointer resultBytes;
        readonly ResultStatus resultStatus;
        readonly ResultPointer resultError;

        public Shaderc(string path) {
            library = NativeLibrary.Load(path);

            T Bind<T>(string symbol) where T : Delegate =>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, symbol));

            var initialize = Bind<Initialize>("shaderc_compiler_initialize");
            releaseCompiler = Bind<Release>("shaderc_compiler_release");
            optionsInitialize = Bind<OptionsInitialize>("shaderc_compile_options_initialize");
            optionsRelease = Bind<Release>("shaderc_compile_options_release");
            setLevel = Bind<SetLevel>("shaderc_compile_options_set_optimization_level");
            compile = Bind<CompileIntoSpv>("shaderc_compile_into_spv");
            resultRelease = Bind<Release>("shaderc_result_release");
            resultLength = Bind<ResultLength>("shaderc_result_get_length");
            resultBytes = Bind<ResultPointer>("shaderc_result_get_bytes");
            resultStatus = Bind<ResultStatus>("shaderc_result_get_compilation_status");
            resultError = Bind<ResultPointer>("shaderc_result_get_error_message");

            compiler = initialize();
        }

        /// <summary>Compiles one shader; the stage is the extension's, as glslc infers it.</summary>
        public byte[] Compile(string source, string name, bool optimise) {
            var kind = name.EndsWith(".vert", StringComparison.Ordinal) ? 0 : name.EndsWith(".frag", StringComparison.Ordinal) ? 1 : -1;

            Assert.True(kind >= 0, $"{name} has an extension the recipe does not map to a stage.");

            var options = optionsInitialize();

            try {
                // shaderc_optimization_level_zero = 0, _performance = 2: glslc's -O0 and -O.
                setLevel(options, optimise ? 2 : 0);

                var text = Encoding.UTF8.GetBytes(source);
                var result = compile(compiler, text, (nuint)text.Length, kind, name, "main", options);

                try {
                    Assert.True(
                        resultStatus(result) == 0,
                        $"shaderc refused {name}: {Marshal.PtrToStringUTF8(resultError(result))}"
                    );

                    var bytes = new byte[(int)resultLength(result)];
                    Marshal.Copy(resultBytes(result), bytes, 0, bytes.Length);
                    return bytes;
                } finally {
                    resultRelease(result);
                }
            } finally {
                optionsRelease(options);
            }
        }

        public void Dispose() {
            releaseCompiler(compiler);
            NativeLibrary.Free(library);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr Initialize();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr OptionsInitialize();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void Release(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void SetLevel(IntPtr options, int level);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr CompileIntoSpv(
            IntPtr compiler,
            byte[] source,
            nuint length,
            int kind,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string inputFileName,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string entryPoint,
            IntPtr options
        );

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate nuint ResultLength(IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr ResultPointer(IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int ResultStatus(IntPtr result);

    }
}
