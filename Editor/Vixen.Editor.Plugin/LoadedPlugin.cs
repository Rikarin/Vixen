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

    /// <summary>Whether it is a module the editor ships rather than something installed.</summary>
    /// <remarks>
    ///     ⚠ <b>Both are listed, and a manager that showed only the installed half would be one
    ///     where "what is running in my editor" has two answers.</b> What this distinguishes is what
    ///     a user can act on: a built-in has no folder to open, no files to update and nothing to
    ///     uninstall, and "no plugins are installed" is still the honest empty state for an editor
    ///     whose only entries are its own features.
    /// </remarks>
    public bool IsBuiltIn { get; internal set; }

    /// <summary>Why it did not start, if it did not.</summary>
    public Exception? Failure { get; internal set; }

    /// <summary>How many things it registered.</summary>
    public int Registrations => Scope.Count;

    internal PluginRegistrations Scope { get; } = new();

    internal IEditorPlugin? Instance { get; set; }

    /// <summary>
    ///     A built-in's module, kept while the user's switch may bring it back, because there is nowhere
    ///     to re-read it from.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only for a built-in.</b> Holding something loaded from a collectible context would be
    ///         the reference that stops it being collected.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And not past an <see cref="PluginHost.Unload" />, because holding it is not free.</b>
    ///         #1288 kept it "whatever its state" on the reasoning that a compiled-in module's
    ///         <i>assembly</i> never leaves; but this is the <i>instance</i>, and with it everything the
    ///         instance still points at. An unloaded row stays in the host's list, so this field was the
    ///         one path by which every <c>…ModuleReachTests</c> probe found its module alive after an
    ///         unload. <see cref="PluginHost.Disable" /> keeps it, since switching the module back on
    ///         is what a disabled row is for; a plain unload drops it, which is what it did before #1288.
    ///     </para>
    /// </remarks>
    internal IEditorPlugin? Module { get; set; }

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
