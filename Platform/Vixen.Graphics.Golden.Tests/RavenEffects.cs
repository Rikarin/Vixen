// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.ShaderCompiler;
using Vixen.Shaders;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>Compiling the shipped library on demand, the way a content build would have baked it.</summary>
/// <remarks>
///     <para>
///         Shared rather than private to one fixture, because every device test that draws something
///         the engine actually ships needs the same three things: where the library is, a compiler over
///         it, and something that turns a key the frame asks for into an <see cref="Effect" />. It was
///         private to <see cref="ClusteredShadingDeviceTests" /> until a second frame wanted it, which
///         is the right time to move it and not before.
///     </para>
///     <para>
///         ⚠ <b>A compiler per shader name, not one for the frame.</b> The material shaders' compose
///         slots are bound by a composition, so a source set holding them compiles nothing without one
///         — and a compute pass has no composition and no business having one. Two passes in one frame
///         can therefore need different libraries, which a content build does per effect anyway.
///     </para>
/// </remarks>
sealed class Compiling(EffectLoader loader, Func<string, RavenEffectCompiler>? sources = null) : IEffectProvider {
    readonly Dictionary<EffectKey, Effect> cache = [];
    readonly Dictionary<string, RavenEffectCompiler> compilers = [];

    public Effect? TryGet(EffectKey key) {
        if (cache.TryGetValue(key, out var existing)) {
            return existing;
        }

        if (!compilers.TryGetValue(key.ShaderName, out var compiler)) {
            compilers[key.ShaderName] = compiler = (sources ?? RavenEffects.Everything)(key.ShaderName);
        }

        if (compiler.TryGet(key) is not { } data) {
            return null;
        }

        var effect = loader.Load(data);
        cache[key] = effect;

        return effect;
    }
}

/// <summary>Where the shipped library is, and a compiler over it.</summary>
static class RavenEffects {
    /// <summary>A compiler over the whole library.</summary>
    public static RavenEffectCompiler Everything(string shaderName) => Everything();

    /// <summary>A compiler over the whole library.</summary>
    public static RavenEffectCompiler Everything() =>
        new(Directory.GetFiles(Library, "*.rvn", SearchOption.AllDirectories));

    /// <summary>A compiler over named packages only, plus named files.</summary>
    /// <param name="packages">Directory names under the library.</param>
    /// <param name="files">Paths relative to the library.</param>
    /// <returns>The compiler.</returns>
    /// <remarks>
    ///     For a shader that composes nothing: handing it the material tree would make it compile
    ///     nothing at all, because a slot the sources declare has to be bound whether or not this
    ///     shader reaches it.
    /// </remarks>
    public static RavenEffectCompiler Only(IEnumerable<string> packages, params string[] files) {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(files);

        var sources = new List<string>();

        foreach (var package in packages) {
            sources.AddRange(Directory.GetFiles(Path.Combine(Library, package), "*.rvn"));
        }

        foreach (var file in files) {
            sources.Add(Path.Combine(Library, file));
        }

        return new(sources);
    }

    /// <summary>The shader library, found by walking up rather than by counting directories.</summary>
    public static string Library {
        get {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
                var candidate = Path.Combine(directory.FullName, "Raven", "Library");

                if (Directory.Exists(candidate)) {
                    return candidate;
                }
            }

            throw new DirectoryNotFoundException($"Raven/Library was not found above '{AppContext.BaseDirectory}'.");
        }
    }
}
