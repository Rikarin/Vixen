// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin;

/// <summary>Where a plugin got to.</summary>
public enum PluginState : byte {
    /// <summary>Its manifest says <c>enabled: false</c>. Not an error and not reported as one.</summary>
    Disabled,

    /// <summary>Its code is loaded and <see cref="IEditorPlugin.Activate" /> returned.</summary>
    Active,

    /// <summary>It did not start. <see cref="LoadedPlugin.Failure" /> says why.</summary>
    Failed,

    /// <summary>It started and has since been taken back out.</summary>
    Unloaded
}

/// <summary>One plugin, as the host is holding it.</summary>
/// <remarks>
///     What a plugin-management panel lists, and what a test asserts against. The instance and the
///     load context are deliberately not on it: a caller holding either would be the reference that
///     stops the context being collected, which is the one failure mode of this whole design that
///     produces no error message.
/// </remarks>
public sealed class LoadedPlugin {
    internal LoadedPlugin(PluginDescriptor descriptor) => Descriptor = descriptor;

    /// <summary>The plugin, as it was found on disk.</summary>
    public PluginDescriptor Descriptor { get; }

    /// <summary>What everything refers to it by.</summary>
    public string Id => Descriptor.Id;

    /// <summary>What it says about itself.</summary>
    public PluginManifest Manifest => Descriptor.Manifest;

    /// <summary>Where it got to.</summary>
    public PluginState State { get; internal set; } = PluginState.Disabled;

    /// <summary>Why it did not start, if it did not.</summary>
    public Exception? Failure { get; internal set; }

    /// <summary>How many things it registered.</summary>
    public int Registrations => Scope.Count;

    internal PluginRegistrations Scope { get; } = new();

    internal IEditorPlugin? Instance { get; set; }

    internal PluginLoadContext? Context { get; set; }

    /// <summary>
    ///     A weak handle on the unloaded assembly's context, which is how "did it really go away"
    ///     is answerable.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The runtime says nothing when a collectible context cannot be collected.</b> A
    ///     plugin that left a subscription behind unloads, reports success, and stays in memory
    ///     forever — and the second load of the same plugin then has two copies of its static state.
    ///     <see cref="PluginHost.WaitForCollection" /> reads this and is what turns the silence into
    ///     a warning.
    /// </remarks>
    internal WeakReference? Collectible { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"{Descriptor} ({State})";
}
