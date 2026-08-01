// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>Compiles the shipped Raven library on demand, which is what a shipping build bakes.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Without this the game runs, reports nothing wrong, and draws a black screen.</b> A
///         shipping build's only source of shader variants is <c>shaders.effects</c> beside the
///         catalog, and <c>vixen content build</c> writes that file from
///         <c>ProjectSettings/Shaders.effects.json</c> — a manifest produced by playing the game
///         against a compiler and writing out <c>EffectSystem.Requests</c>. A project that has not
///         been through that loop yet ships no bundle, every material resolves to a miss, and every
///         miss draws nothing. The host says so in one line at start-up
///         (<c>No baked shaders: There is no shaders.effects at …</c>) and nothing else complains.
///     </para>
///     <para>
///         <b>So a development build brings its own provider, which is what the samples do and what
///         <c>ContentMount.Shaders</c> means by "whatever provider it adds itself".</b> The compiler
///         lives in <c>Tools/Vixen.ShaderCompiler</c> and is deliberately never linked into a shipped
///         game — a game that could compile a variant would never notice a manifest was missing,
///         which is the failure this class exists to make visible rather than to hide.
///     </para>
///     <para>
///         The library is found by walking up from the binary, because a sample runs from the
///         repository rather than from an install. A real project would name its own shader
///         directory, and a shipped one would not have this class at all.
///     </para>
/// </remarks>
/// <param name="loader">Turns compiled data into an effect on the device.</param>
public sealed class DevelopmentEffects(EffectLoader loader) : IEffectProvider {
    readonly Dictionary<EffectKey, Effect> cache = [];
    RavenEffectCompiler? compiler;

    /// <summary>How many variants have been compiled, which is what says the frame had shaders.</summary>
    public int CompiledCount => cache.Count;

    /// <inheritdoc />
    public Effect? TryGet(EffectKey key) {
        if (cache.TryGetValue(key, out var existing)) {
            return existing;
        }

        // One compiler over the whole library, built on the first ask. Enumerating the directory is
        // not free and a headless run that draws nothing should not pay for it.
        compiler ??= new(Directory.GetFiles(Library(), "*.rvn", SearchOption.AllDirectories));

        if (compiler.TryGet(key) is not { } data) {
            return null;
        }

        var effect = loader.Load(data);
        cache[key] = effect;

        return effect;
    }

    /// <summary>Adds one to a device's effect system, if the library can be found.</summary>
    /// <param name="effects">The effect system.</param>
    /// <param name="device">The device the effects are built on.</param>
    /// <returns>The provider, or <see langword="null" /> if there is no library to compile from.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     A missing library is not fatal here. A published build has no <c>Raven/Library</c> beside
    ///     it and does not want one — it wants the baked bundle — so failing would break exactly the
    ///     case this is not for.
    /// </remarks>
    public static DevelopmentEffects? Attach(EffectSystem effects, IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(device);

        if (!TryFindLibrary(out _)) {
            return null;
        }

        var provider = new DevelopmentEffects(new(device));

        effects.AddProvider(provider);
        return provider;
    }

    static string Library() =>
        TryFindLibrary(out var path)
            ? path
            : throw new DirectoryNotFoundException($"Raven/Library was not found above '{AppContext.BaseDirectory}'.");

    static bool TryFindLibrary(out string path) {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library");

            if (Directory.Exists(candidate)) {
                path = candidate;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }
}
