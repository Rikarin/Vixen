// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.App;
using Vixen.Assets;
using Vixen.Audio;
using Vixen.Audio.Backend.OpenAL;
using Vixen.Audio.Devices;
using Vixen.Audio.Mixing;
using Vixen.Core;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>Turns the addresses the content build published into the references components hold.</summary>
/// <remarks>
///     <para>
///         <b>Why this is a lookup and not a table of guids.</b> A <c>MeshRenderable</c> holds an
///         <c>AssetReference</c> — a guid and a sub-asset id — because that is what survives a file
///         being renamed. A scene file can carry one because the editor wrote it there; code cannot,
///         because a guid pasted into C# is a number nobody can check and nothing keeps up to date.
///     </para>
///     <para>
///         The catalog knows both halves: it is keyed by address and each entry carries the reference
///         naming the same chunk. So the game asks for <c>Assets/Models/player-torso.obj#PlayerTorso</c>,
///         which is a path a person can find on disk, and gets back what the renderer resolves.
///     </para>
/// </remarks>
public static class GameModels {
    /// <summary>The address of a model's mesh sub-asset.</summary>
    /// <param name="model">The file stem, as in <c>player-torso</c>.</param>
    /// <param name="mesh">What the importer named the mesh, as in <c>PlayerTorso</c>.</param>
    /// <returns>The address.</returns>
    public static string Address(string model, string mesh) => $"Assets/Models/{model}.obj#{mesh}";

    /// <summary>The address of one of the project's materials.</summary>
    /// <param name="material">The file stem, as in <c>player-body</c>.</param>
    /// <returns>The address.</returns>
    /// <remarks>
    ///     ⚠ <b>A renderable with a null material draws in the renderer's fallback, and every object
    ///     sharing one fallback is a level that renders as one grey mass.</b> The level's own
    ///     renderables name theirs in the scene file, where an artist would put it; the player's parts
    ///     are made in code and so name theirs here. Both end up as an <c>AssetReference</c> in a
    ///     <c>MeshRenderable</c>, which is the only thing the extraction reads.
    /// </remarks>
    public static string MaterialAddress(string material) => $"Assets/Materials/{material}.vxmat";

    /// <summary>What the mesh at an address is called in a component.</summary>
    /// <param name="assets">The catalog, or <see langword="null" /> in a build with no content.</param>
    /// <param name="address">The sub-asset address.</param>
    /// <returns>The reference, or a null one if nothing is published there.</returns>
    /// <remarks>
    ///     A miss is a null reference rather than a throw. <c>AssetMeshSource</c> counts a reference
    ///     it cannot resolve and leaves the entity alone, so the part is invisible and the rest of the
    ///     frame is fine — which is a level somebody can still walk around in and debug.
    /// </remarks>
    public static AssetReference Reference(AssetManager? assets, string address) =>
        assets is not null && assets.Catalog.TryGet(address, out var entry) ? entry.Reference : AssetReference.Null;
}

/// <summary>The sounds and the device they come out of.</summary>
/// <remarks>
///     <para>
///         <b>A device, an engine and six clips.</b> The clips are ordinary content, loaded by
///         address; the engine is not, because a machine may have no sound card and a game that
///         refused to start on one would fail every CI runner in existence. <see cref="Silent" /> is
///         what a headless run gets, and every call here is a no-op against it.
///     </para>
///     <para>
///         ⚠ <b>One-shots, unpositioned.</b> <c>AudioEngine.Play</c> takes a voice from the pool and
///         lets it run to the end; nothing here sets a position, because the listener is on the player
///         and every sound this game makes is the player's own. Spatialising them is
///         <c>AudioSpatial</c> and an <c>AudioListener</c> component, which is a different sample's
///         subject.
///     </para>
/// </remarks>
public sealed class GameSounds : IDisposable {
    readonly AudioEngine? engine;
    readonly IAudioDevice? device;
    readonly OpenALBackend? backend;

    GameSounds(AudioEngine? engine = null, IAudioDevice? device = null, OpenALBackend? backend = null) {
        this.engine = engine;
        this.device = device;
        this.backend = backend;
    }

    /// <summary>The set with no device behind it, which plays nothing and says nothing.</summary>
    public static GameSounds Silent { get; } = new();

    /// <summary>Whether there is a device to play through.</summary>
    public bool IsAudible => engine is not null;

    /// <summary>A left footstep.</summary>
    public AudioClip? FootstepLeft { get; private init; }

    /// <summary>A right footstep.</summary>
    public AudioClip? FootstepRight { get; private init; }

    /// <summary>The gun.</summary>
    public AudioClip? Gunshot { get; private init; }

    /// <summary>Leaving the ground.</summary>
    public AudioClip? Jump { get; private init; }

    /// <summary>Arriving back on it.</summary>
    public AudioClip? Land { get; private init; }

    /// <summary>The reload.</summary>
    public AudioClip? Reload { get; private init; }

    /// <summary>Opens a device if there is one, and loads every sound the game plays.</summary>
    /// <param name="services">What the host built.</param>
    /// <param name="logger">Where to say how it went.</param>
    /// <returns>The set, which may be <see cref="Silent" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    public static GameSounds Load(AppServices services, ILogger logger) {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Assets is not { } assets) {
            return Silent;
        }

        var (engine, device, backend) = OpenDevice(services);
        var missing = 0;

        AudioClip? One(string address) {
            try {
                if (assets.Load<AudioClip>(address).Result is { } clip) {
                    return clip;
                }
            } catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException)) {
                // Counted rather than thrown. A game that will not start because one effect is
                // missing is worse than a game that is quiet.
            }

            missing++;
            return null;
        }

        var sounds = new GameSounds(engine, device, backend) {
            FootstepLeft = One("Assets/Audio/footstep-left.wav"),
            FootstepRight = One("Assets/Audio/footstep-right.wav"),
            Gunshot = One("Assets/Audio/gunshot.wav"),
            Jump = One("Assets/Audio/jump.wav"),
            Land = One("Assets/Audio/land.wav"),
            Reload = One("Assets/Audio/reload.wav")
        };

        SampleLog.SoundsLoaded(logger, 6 - missing, missing);
        return sounds;
    }

    /// <summary>Plays a clip once, if there is anything to play it with.</summary>
    /// <param name="clip">The clip, or <see langword="null" /> for nothing.</param>
    /// <param name="gain">How loud, as a linear multiplier.</param>
    public void Play(AudioClip? clip, float gain = 1f) {
        if (engine is null || clip is null) {
            return;
        }

        engine.Play(clip, new PlaybackSettings { Gain = gain });
    }

    static (AudioEngine?, IAudioDevice?, OpenALBackend?) OpenDevice(AppServices services) {
        var backend = new OpenALBackend();

        if (!backend.IsAvailable) {
            return (null, null, null);
        }

        try {
            var device = backend.OpenDevice(new AudioDeviceOptions());
            var engine = new AudioEngine(device, new AudioEngineOptions(), services.LoggerFactory.CreateLogger("Audio"));

            return (engine, device, backend);
        } catch (AudioDeviceException) {
            // A machine with the library and no usable output — a container, a CI runner, a session
            // with no audio server. Quiet is the right answer and it is not an error.
            backend.Dispose();
            return (null, null, null);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        engine?.Dispose();
        device?.Dispose();
        backend?.Dispose();
    }
}
