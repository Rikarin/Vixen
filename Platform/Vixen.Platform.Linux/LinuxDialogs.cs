// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.Linux;

/// <summary>The Linux file pickers, through whichever of them the session has.</summary>
/// <remarks>
///     <para>
///         <b>KDE's first under KDE, GNOME's otherwise.</b> A picker is where the user's places,
///         recent files and remote mounts live, and those belong to their desktop rather than to
///         their distribution — a KDE user given zenity's picker is shown somebody else's bookmarks.
///         <c>XDG_CURRENT_DESKTOP</c> is what the session sets to say which it is, and it is what is
///         read here. Where neither program is installed there is no picker, the platform does not
///         report <see cref="PlatformCapabilities.NativeDialogs" />, and the methods return
///         nothing-chosen — which is the case every caller already handles for a user who pressed
///         Cancel.
///     </para>
///     <para>
///         <b><c>qarma</c> and <c>matedialog</c> are zenity.</b> Both are re-implementations of its
///         command line, and both are what a distribution that does not ship GNOME's copy ships
///         instead. Trying them costs one <c>PATH</c> lookup each.
///     </para>
///     <para>
///         <b>There is no owner window.</b> Neither program takes one — they are separate processes,
///         and the window manager decides where their dialog goes. Under Wayland it could not be
///         parented anyway, since a client is not told its own position and cannot address another
///         client's surface. So <c>owner</c> is accepted and ignored, which is what the parameter's
///         "where the platform has sheets" is there to allow.
///     </para>
/// </remarks>
/// <param name="fallback">The portable dialogs, which keep the message boxes.</param>
[SupportedOSPlatform("linux")]
public sealed class LinuxDialogs(INativeDialogs fallback) : INativeDialogs {
    static readonly string[] ZenityPrograms = ["zenity", "qarma", "matedialog"];

    /// <summary>Whether a picker program is installed at all.</summary>
    /// <remarks>
    ///     What decides <see cref="PlatformCapabilities.NativeDialogs" />. Asking once at
    ///     construction would be cheaper and would be wrong for a session where the user installs
    ///     one while the editor is open — but the capability is read at boot regardless, so this is
    ///     honest about being a snapshot rather than pretending the flag tracks it.
    /// </remarks>
    public static bool IsAvailable => Resolve() is not null;

    /// <inheritdoc />
    public async ValueTask<string?> OpenFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) {
        var chosen = await ShowAsync(options, DialogKind.Open, false, cancellationToken).ConfigureAwait(false);
        return chosen.Count > 0 ? chosen[0] : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> OpenFilesAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        await ShowAsync(options, DialogKind.Open, true, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<string?> SaveFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) {
        var chosen = await ShowAsync(options, DialogKind.Save, false, cancellationToken).ConfigureAwait(false);
        return chosen.Count > 0 ? chosen[0] : null;
    }

    /// <inheritdoc />
    public async ValueTask<string?> OpenFolderAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) {
        var chosen = await ShowAsync(options, DialogKind.Folder, false, cancellationToken).ConfigureAwait(false);
        return chosen.Count > 0 ? chosen[0] : null;
    }

    /// <summary>Shows the message box the portable implementation shows.</summary>
    /// <remarks>
    ///     SDL's is the OS's own and needs no display-server-specific help, unlike the picker. It is
    ///     also the one dialog that has to work when the renderer has just failed, which is an
    ///     argument against making it depend on a program that may not be installed.
    /// </remarks>
    public ValueTask<MessageBoxResult> ShowMessageAsync(
        MessageBoxOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        fallback.ShowMessageAsync(options, owner, cancellationToken);

    /// <summary>Which picker program to use, or <see langword="null" /> if there is none.</summary>
    static (string Program, DialogTool Tool)? Resolve() {
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
        var prefersKde = desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase);

        if (prefersKde && ExternalTool.Exists("kdialog")) {
            return ("kdialog", DialogTool.KDialog);
        }

        foreach (var program in ZenityPrograms) {
            if (ExternalTool.Exists(program)) {
                return (program, DialogTool.Zenity);
            }
        }

        return ExternalTool.Exists("kdialog") ? ("kdialog", DialogTool.KDialog) : null;
    }

    static async Task<IReadOnlyList<string>> ShowAsync(
        FileDialogOptions options,
        DialogKind kind,
        bool multiple,
        CancellationToken cancellationToken
    ) {
        if (cancellationToken.IsCancellationRequested || Resolve() is not { } resolved) {
            return [];
        }

        var arguments = DialogArguments.For(resolved.Tool, options, kind, multiple);
        var (exitCode, output) = await ExternalTool.RunAsync(resolved.Program, arguments, cancellationToken)
            .ConfigureAwait(false);

        // Both tools exit 1 when the user cancels, which is a failure code for a thing that is not a
        // failure — hence a return of nothing rather than an exception.
        return exitCode == 0 ? DialogArguments.Parse(output) : [];
    }
}
