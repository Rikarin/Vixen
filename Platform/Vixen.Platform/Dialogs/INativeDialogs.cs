// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>One entry in a file dialog's type filter.</summary>
/// <param name="Name">What to show the user, such as <c>"Vixen scene"</c>.</param>
/// <param name="Extensions">Extensions without the dot, such as <c>["vxscene"]</c>.</param>
public readonly record struct FileFilter(string Name, params string[] Extensions);

/// <summary>What to ask a file dialog for.</summary>
public readonly record struct FileDialogOptions() {
    /// <summary>The dialog's title.</summary>
    public string? Title { get; init; }

    /// <summary>Where to start, as a native path.</summary>
    /// <remarks>
    ///     Native rather than virtual, and this is the one place in the engine where that is right:
    ///     the user is picking a file that by definition sits outside anything the VFS has mounted.
    ///     What comes back is a native path too, and turning it into something the engine can read
    ///     means mounting it.
    /// </remarks>
    public string? InitialDirectory { get; init; }

    /// <summary>The file name to suggest, for a save dialog.</summary>
    public string? SuggestedFileName { get; init; }

    /// <summary>The type filters, most likely first.</summary>
    public IReadOnlyList<FileFilter> Filters { get; init; } = [];

    /// <summary>Whether the user may pick more than one file.</summary>
    /// <remarks>Ignored by save and folder dialogs.</remarks>
    public bool AllowsMultipleSelection { get; init; }
}

/// <summary>Which buttons a message box offers.</summary>
public enum MessageBoxButtons : byte {
    /// <summary>One button that dismisses the box.</summary>
    Ok = 0,

    /// <summary>Go ahead, or do not.</summary>
    OkCancel = 1,

    /// <summary>Answer the question one way or the other.</summary>
    YesNo = 2,

    /// <summary>Answer the question, or step away from it.</summary>
    YesNoCancel = 3
}

/// <summary>How serious a message box is, which decides its icon and sound.</summary>
public enum MessageBoxSeverity : byte {
    /// <summary>Something worth knowing.</summary>
    Information = 0,

    /// <summary>Something that may go wrong.</summary>
    Warning = 1,

    /// <summary>Something that has gone wrong.</summary>
    Error = 2
}

/// <summary>Which button the user pressed.</summary>
public enum MessageBoxResult : byte {
    /// <summary>The dialog was dismissed without a choice, or could not be shown at all.</summary>
    None = 0,

    /// <summary>The user acknowledged, or agreed.</summary>
    Ok = 1,

    /// <summary>The user backed out.</summary>
    Cancel = 2,

    /// <summary>The user said yes.</summary>
    Yes = 3,

    /// <summary>The user said no, which is not the same as backing out.</summary>
    No = 4
}

/// <summary>What to show in a message box.</summary>
public readonly record struct MessageBoxOptions() {
    /// <summary>The bold first line.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The body text.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Which buttons to offer.</summary>
    public MessageBoxButtons Buttons { get; init; } = MessageBoxButtons.Ok;

    /// <summary>How serious it is.</summary>
    public MessageBoxSeverity Severity { get; init; } = MessageBoxSeverity.Information;
}

/// <summary>The OS's own file pickers and message boxes.</summary>
/// <remarks>
///     <para>
///         Native, not drawn by <c>Vixen.Ui</c>, and that is worth being firm about: a file picker
///         is where the user's places, tags, network shares, cloud providers, search and
///         accessibility settings live. A drawn one has none of those, and every application that
///         has shipped one has been told so. It is also the only way to obtain a file at all inside
///         a Flatpak or macOS sandbox, where the picker's result is what grants the read permission.
///     </para>
///     <para>
///         Asynchronous because these are modal: the platform runs its own event loop while one is
///         open, and a synchronous call would either block the frame loop or reenter it. Awaiting
///         keeps the loop running and keeps the reentrancy inside the implementation, where it can
///         be dealt with once.
///     </para>
///     <para>
///         Every method returns nothing-chosen rather than throwing when the user cancels, and the
///         same when <see cref="PlatformCapabilities.NativeDialogs" /> is absent — a headless build
///         asked to open a picker has no user to ask, and that is a case the caller already has to
///         handle for cancellation.
///     </para>
/// </remarks>
public interface INativeDialogs {
    /// <summary>Asks the user to pick one existing file.</summary>
    /// <param name="options">What to ask for.</param>
    /// <param name="owner">The window to attach the sheet to, where the platform has sheets.</param>
    /// <param name="cancellationToken">Dismisses the dialog, where the platform allows it.</param>
    /// <returns>A native path, or <see langword="null" /> if nothing was chosen.</returns>
    ValueTask<string?> OpenFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Asks the user to pick one or more existing files.</summary>
    /// <param name="options">What to ask for.</param>
    /// <param name="owner">The window to attach the sheet to, where the platform has sheets.</param>
    /// <param name="cancellationToken">Dismisses the dialog, where the platform allows it.</param>
    /// <returns>Native paths, empty if nothing was chosen.</returns>
    ValueTask<IReadOnlyList<string>> OpenFilesAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Asks the user where to save.</summary>
    /// <param name="options">What to ask for.</param>
    /// <param name="owner">The window to attach the sheet to, where the platform has sheets.</param>
    /// <param name="cancellationToken">Dismisses the dialog, where the platform allows it.</param>
    /// <returns>A native path, or <see langword="null" /> if nothing was chosen.</returns>
    ValueTask<string?> SaveFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Asks the user to pick a folder.</summary>
    /// <param name="options">What to ask for.</param>
    /// <param name="owner">The window to attach the sheet to, where the platform has sheets.</param>
    /// <param name="cancellationToken">Dismisses the dialog, where the platform allows it.</param>
    /// <returns>A native path, or <see langword="null" /> if nothing was chosen.</returns>
    ValueTask<string?> OpenFolderAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Shows a message box and waits for an answer.</summary>
    /// <param name="options">What to show.</param>
    /// <param name="owner">The window to attach the sheet to, where the platform has sheets.</param>
    /// <param name="cancellationToken">Dismisses the dialog, where the platform allows it.</param>
    /// <returns>Which button was pressed, or <see cref="MessageBoxResult.None" /> if the box could
    /// not be shown.</returns>
    ValueTask<MessageBoxResult> ShowMessageAsync(
        MessageBoxOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    );
}
