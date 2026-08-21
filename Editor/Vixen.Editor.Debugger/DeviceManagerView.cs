// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Debugger;

/// <summary>What a build can be deployed to, and which of them is answering.</summary>
/// <remarks>
///     <para>
///         The panel is <c>DeviceManagerView.vxml</c>; this file is the accessibility modifier. The
///         emitter's partial carries no modifier, and both <c>Vixen.Editor.Diagnostics</c> and
///         <c>Vixen.Editor.App.Tests</c> hold this type — so the declaration that says
///         <c>public</c> has to be here.
///     </para>
///     <para>
///         ⚠ <b>What this panel is honest about is how much of it is a provider away.</b> The list,
///         the statuses, the selection and the hand-off to the remote inspector are all here; what is
///         not is anything that knows how to <i>find</i> an Android phone, which is <c>adb</c>, or a
///         console, which is a vendor SDK. Doc 20 puts device discovery behind those tools rather
///         than behind this window, and a panel listing the local machine and saying so is a truer
///         state than one that pretends to scan.
///     </para>
///     <para>
///         ⚠ <b>Deploy is a request rather than a call, for exactly <c>AttachRequested</c>'s
///         reason.</b> It needs a build — which is doc 20's E6 and lives behind
///         <c>build.settings</c> — and what "deploy" means differs per kind of device: this machine
///         is a publish and a launch, a phone is <c>adb install</c>, a console is a vendor SDK. A
///         debugger assembly that picked one would be a panel that could only deploy to that one, so
///         it raises the intent and the application answers it.
///     </para>
///     <para>
///         ⚠ <b>Which devices can be deployed to is asked rather than assumed</b> — see
///         <c>CanDeploy</c>. The button is greyed with the reason for the kinds nothing can
///         reach yet, which is the same rule the greyed menu lines follow and is why a device this
///         editor cannot install to is visibly that rather than a button that fails.
///     </para>
/// </remarks>
public sealed partial class DeviceManagerView;
