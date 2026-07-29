// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vixen.Platform.Windows;

/// <summary>The Windows file pickers, through the shell's <c>IFileDialog</c>.</summary>
/// <remarks>
///     <para>
///         <b>COM by vtable rather than by interface declaration.</b> Five interfaces are needed and
///         nine methods of them; a <c>ComWrappers</c> generator or a set of
///         <c>[GeneratedComInterface]</c> declarations would be more code, not less, and would put a
///         marshalling layer between us and an ABI that is four function-pointer calls deep. What is
///         here is the calls, with the vtable slot beside each one, which is the form in which a
///         mistake is visible.
///     </para>
///     <para>
///         <b>Not WinRT, and therefore not <c>net10.0-windows</c>.</b> The plan
///         (<c>docs/plan/10</c>) named WinRT's <c>FileOpenPicker</c>; that needs a Windows-versioned
///         target framework, which would spread from here to every consumer that references it —
///         the app head, the editor, the samples — and turn a portable build graph into a
///         multi-targeted one. It would also take this assembly out of <c>nuke CheckApi</c>, which
///         only covers <c>net10.0</c>. The WinRT picker for a desktop application is a wrapper over
///         <c>IFileDialog</c>, so the cost buys nothing the user can see.
///     </para>
///     <para>
///         <b>Each dialog gets its own STA thread.</b> A modal shell dialog runs its own message
///         loop until the user is finished, which is as long as they take. Running it on the frame
///         thread would stop the frame loop for that whole time and Windows would draw the ghosted
///         "not responding" chrome over a window that is fine. The dialog is still modal to the
///         application — it is given the owner window, which Windows disables for the duration —
///         and one is shown at a time, which is what the gate is for.
///     </para>
///     <para>
///         <b>Cancellation is honoured before the dialog opens and not after.</b> Closing an open
///         <c>IFileDialog</c> means calling <c>Close</c> on the apartment that owns it, from outside
///         its message loop, which is a deadlock waiting for a slow network place to enumerate.
///         <see cref="INativeDialogs" /> says a token dismisses a dialog "where the platform allows
///         it"; this is one of the places it does not.
///     </para>
/// </remarks>
/// <param name="fallback">The portable dialogs, which keep the message boxes.</param>
[SupportedOSPlatform("windows")]
public sealed class WindowsDialogs(INativeDialogs fallback) : INativeDialogs, IDisposable {
    static readonly Guid FileOpenDialogClass = new("dc1c5a9c-e88a-4dde-a5a1-60f82a20aef7");
    static readonly Guid FileSaveDialogClass = new("c0b4e2f3-ba21-4773-8dba-335ec946eb8b");
    static readonly Guid FileOpenDialogId = new("d57c7288-d4ad-4768-be02-9d969532d960");
    static readonly Guid FileSaveDialogId = new("84bccd23-5fde-4cdb-aea4-af64b83d78ab");
    static readonly Guid ShellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc />
    public async ValueTask<string?> OpenFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) {
        var chosen = await ShowAsync(options, owner, DialogKind.Open, false, cancellationToken)
            .ConfigureAwait(false);

        return chosen.Count > 0 ? chosen[0] : null;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> OpenFilesAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ShowAsync(options, owner, DialogKind.Open, true, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<string?> SaveFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) {
        var chosen = await ShowAsync(options, owner, DialogKind.Save, false, cancellationToken)
            .ConfigureAwait(false);

        return chosen.Count > 0 ? chosen[0] : null;
    }

    /// <inheritdoc />
    public async ValueTask<string?> OpenFolderAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) {
        var chosen = await ShowAsync(options, owner, DialogKind.Folder, false, cancellationToken)
            .ConfigureAwait(false);

        return chosen.Count > 0 ? chosen[0] : null;
    }

    /// <summary>Shows the message box the portable implementation shows.</summary>
    /// <remarks>
    ///     SDL's message box is the OS's own <c>MessageBox</c>, with the button sets already
    ///     translated. There is nothing here to improve on, and a second implementation of it would
    ///     be a second thing to keep right.
    /// </remarks>
    public ValueTask<MessageBoxResult> ShowMessageAsync(
        MessageBoxOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        fallback.ShowMessageAsync(options, owner, cancellationToken);

    /// <summary>Releases the gate that keeps one dialog open at a time.</summary>
    /// <remarks>
    ///     Called by <see cref="WindowsPlatformSupplement" /> when the platform goes away. A dialog
    ///     that is open at that point is on its own thread with its own message loop and is not
    ///     interrupted — see the remarks on this class about why closing one from outside is worse
    ///     than leaving it.
    /// </remarks>
    public void Dispose() => gate.Dispose();

    enum DialogKind {
        Open,
        Save,
        Folder
    }

    async ValueTask<IReadOnlyList<string>> ShowAsync(
        FileDialogOptions options,
        IWindow? owner,
        DialogKind kind,
        bool multiple,
        CancellationToken cancellationToken
    ) {
        if (cancellationToken.IsCancellationRequested) {
            return [];
        }

        var handle = owner?.Surface.Handle is { Kind: Vixen.Core.SurfaceKind.Win32 } surface
            ? surface.Handle
            : nint.Zero;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try {
            return cancellationToken.IsCancellationRequested
                ? []
                : await OnStaThread(() => Show(options, handle, kind, multiple)).ConfigureAwait(false);
        } finally {
            gate.Release();
        }
    }

    /// <summary>Runs the dialog on a thread that exists for it and ends with it.</summary>
    static unsafe Task<string[]> OnStaThread(Func<string[]> work) {
        var completion = new TaskCompletionSource<string[]>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var thread = new Thread(() => {
                var initialised = Win32.CoInitializeEx(null, Win32.CoinitApartmentThreaded);

                try {
                    completion.SetResult(work());
                } catch (Exception error) {
                    completion.SetException(error);
                } finally {
                    // Only when this call is what initialised the apartment. A negative result is
                    // RPC_E_CHANGED_MODE — somebody else got here first with a different model —
                    // and balancing a call that did nothing tears down their apartment.
                    if (initialised >= 0) {
                        Win32.CoUninitialize();
                    }
                }
            }
        ) {
            IsBackground = true,
            Name = "Vixen file dialog"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    static unsafe string[] Show(FileDialogOptions options, nint owner, DialogKind kind, bool multiple) {
        var isSave = kind == DialogKind.Save;
        void* dialog = null;

        var created = Win32.CoCreateInstance(
            isSave ? FileSaveDialogClass : FileOpenDialogClass,
            null,
            Win32.ClsctxInprocServer,
            isSave ? FileSaveDialogId : FileOpenDialogId,
            &dialog
        );

        if (created < 0 || dialog is null) {
            return [];
        }

        try {
            var flags = Win32.FosForceFileSystem | Win32.FosPathMustExist;

            switch (kind) {
                case DialogKind.Folder:
                    flags |= Win32.FosPickFolders;
                    break;

                case DialogKind.Save:
                    flags |= Win32.FosOverwritePrompt;
                    break;

                default:
                    flags |= Win32.FosFileMustExist;

                    if (multiple) {
                        flags |= Win32.FosAllowMultiSelect;
                    }

                    break;
            }

            // Slot 9, IFileDialog::SetOptions.
            ((delegate* unmanaged[Stdcall]<void*, uint, int>)VTable(dialog)[9])(dialog, flags);

            Configure(dialog, options, kind);

            // Slot 3, IModalWindow::Show. A cancelled dialog returns
            // HRESULT_FROM_WIN32(ERROR_CANCELLED), which is a failure code and is not a failure.
            if (((delegate* unmanaged[Stdcall]<void*, nint, int>)VTable(dialog)[3])(dialog, owner) < 0) {
                return [];
            }

            return multiple && kind == DialogKind.Open ? Results(dialog) : Result(dialog);
        } finally {
            Release(dialog);
        }
    }

    static unsafe void Configure(void* dialog, FileDialogOptions options, DialogKind kind) {
        if (!string.IsNullOrEmpty(options.Title)) {
            // Slot 17, IFileDialog::SetTitle.
            fixed (char* title = options.Title) {
                ((delegate* unmanaged[Stdcall]<void*, char*, int>)VTable(dialog)[17])(dialog, title);
            }
        }

        if (!string.IsNullOrEmpty(options.SuggestedFileName)) {
            // Slot 15, IFileDialog::SetFileName.
            fixed (char* name = options.SuggestedFileName) {
                ((delegate* unmanaged[Stdcall]<void*, char*, int>)VTable(dialog)[15])(dialog, name);
            }
        }

        void* folder = null;

        if (!string.IsNullOrEmpty(options.InitialDirectory)
            && Win32.ShCreateItemFromParsingName(options.InitialDirectory, null, ShellItemId, &folder) >= 0
            && folder is not null) {
            try {
                // Slot 12, IFileDialog::SetFolder — not SetDefaultFolder, which is only a suggestion
                // for the first time the user ever opens this dialog and is silently ignored
                // afterwards. A caller that names a directory means that directory.
                ((delegate* unmanaged[Stdcall]<void*, void*, int>)VTable(dialog)[12])(dialog, folder);
            } finally {
                Release(folder);
            }
        }

        if (kind == DialogKind.Folder || options.Filters.Count == 0) {
            return;
        }

        var specs = stackalloc FilterSpec[options.Filters.Count];
        var allocated = new List<nint>(options.Filters.Count * 2);

        try {
            for (var index = 0; index < options.Filters.Count; index++) {
                var filter = options.Filters[index];
                var patterns = new string[Math.Max(1, filter.Extensions.Length)];
                patterns[0] = "*.*";

                for (var extension = 0; extension < filter.Extensions.Length; extension++) {
                    patterns[extension] = "*." + filter.Extensions[extension];
                }

                var pattern = string.Join(';', patterns);
                var name = Marshal.StringToHGlobalUni(filter.Name);
                var spec = Marshal.StringToHGlobalUni(pattern);

                allocated.Add(name);
                allocated.Add(spec);

                specs[index] = new() { Name = name, Spec = spec };
            }

            // Slot 4, IFileDialog::SetFileTypes.
            ((delegate* unmanaged[Stdcall]<void*, uint, FilterSpec*, int>)VTable(dialog)[4])(
                dialog,
                (uint)options.Filters.Count,
                specs
            );

            var first = options.Filters[0].Extensions;

            if (first.Length > 0) {
                // Slot 22, IFileDialog::SetDefaultExtension. Without it a save dialog produces a
                // file with no extension whenever the user types a name and does not add one.
                fixed (char* extension = first[0]) {
                    ((delegate* unmanaged[Stdcall]<void*, char*, int>)VTable(dialog)[22])(dialog, extension);
                }
            }
        } finally {
            foreach (var pointer in allocated) {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    static unsafe string[] Result(void* dialog) {
        void* item = null;

        // Slot 20, IFileDialog::GetResult.
        if (((delegate* unmanaged[Stdcall]<void*, void**, int>)VTable(dialog)[20])(dialog, &item) < 0
            || item is null) {
            return [];
        }

        try {
            return PathOf(item) is { } path ? [path] : [];
        } finally {
            Release(item);
        }
    }

    static unsafe string[] Results(void* dialog) {
        void* array = null;

        // Slot 27, IFileOpenDialog::GetResults.
        if (((delegate* unmanaged[Stdcall]<void*, void**, int>)VTable(dialog)[27])(dialog, &array) < 0
            || array is null) {
            return [];
        }

        try {
            uint count;

            // Slot 7, IShellItemArray::GetCount.
            if (((delegate* unmanaged[Stdcall]<void*, uint*, int>)VTable(array)[7])(array, &count) < 0) {
                return [];
            }

            var paths = new List<string>((int)count);

            for (var index = 0u; index < count; index++) {
                void* item = null;

                // Slot 8, IShellItemArray::GetItemAt.
                if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)VTable(array)[8])(
                        array,
                        index,
                        &item
                    ) < 0
                    || item is null) {
                    continue;
                }

                try {
                    if (PathOf(item) is { } path) {
                        paths.Add(path);
                    }
                } finally {
                    Release(item);
                }
            }

            return [.. paths];
        } finally {
            Release(array);
        }
    }

    static unsafe string? PathOf(void* item) {
        char* name = null;

        // Slot 5, IShellItem::GetDisplayName. SIGDN_FILESYSPATH fails rather than inventing
        // something for an item that is not a file — a search result, a device, a photo still on a
        // phone — which is what FOS_FORCEFILESYSTEM is there to prevent the user picking.
        if (((delegate* unmanaged[Stdcall]<void*, int, char**, int>)VTable(item)[5])(
                item,
                Win32.SigdnFileSysPath,
                &name
            ) < 0
            || name is null) {
            return null;
        }

        try {
            return new(name);
        } finally {
            Win32.CoTaskMemFree(name);
        }
    }

    static unsafe void** VTable(void* instance) => *(void***)instance;

    static unsafe void Release(void* instance) =>
        // Slot 2, IUnknown::Release. The count it returns is only ever a debugging aid and is
        // meaningless the moment another thread has a reference.
        _ = ((delegate* unmanaged[Stdcall]<void*, uint>)VTable(instance)[2])(instance);
}
