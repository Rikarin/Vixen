// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Ui.Testing;

/// <summary>A command that could not be satisfied within its budget.</summary>
/// <remarks>
///     <para>
///         Its own type rather than an assertion-library failure, because this assembly must not
///         choose one. A project asserting with Shouldly, xunit's <c>Assert</c> or nothing at all
///         gets the same exception, and a test runner shows the message either way.
///     </para>
///     <para>
///         ⚠ <b>The message carries the command log and the tree, not just the claim that failed.</b>
///         "Expected 1 element matching '.toast', found 0" is a true statement that tells nobody
///         anything: the question is always whether the selector is wrong, the element never
///         appeared, or something earlier in the test did not do what it looked like it did. So the
///         log answers the third, the tree answers the first, and the count answers the second.
///     </para>
/// </remarks>
public sealed class UiTestException : Exception {
    /// <summary>Creates a failure.</summary>
    /// <param name="message">What went wrong.</param>
    public UiTestException(string message) : base(message) {
    }

    /// <summary>Creates a failure with an inner cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="inner">What caused it.</param>
    public UiTestException(string message, Exception inner) : base(message, inner) {
    }

    /// <summary>Creates a failure with no message. Present because the analyser asks for it.</summary>
    public UiTestException() {
    }

    /// <summary>Builds the message a failed command reports.</summary>
    /// <param name="what">What the command wanted, in the words the test used.</param>
    /// <param name="found">What it got instead.</param>
    /// <param name="frames">How many frames it waited first.</param>
    /// <param name="log">Everything the test had done up to that point.</param>
    /// <param name="tree">The interface as it stood when the budget ran out.</param>
    internal static UiTestException Build(string what, string found, int frames, CommandLog log, string tree) {
        var message = new StringBuilder();
        message.Append(what).AppendLine(".");
        message.Append("  Found: ").AppendLine(found);

        message.Append("  Waited: ")
            .Append(frames)
            .AppendLine(frames == 1 ? " frame" : " frames");

        message.AppendLine().AppendLine("Commands:").AppendLine(Indent(log.ToString()));
        message.AppendLine("Interface:").Append(Indent(tree));
        return new(message.ToString());
    }

    static string Indent(string text) =>
        string.Join(
            Environment.NewLine,
            text.Split(Environment.NewLine).Select(line => line.Length == 0 ? line : "  " + line)
        );
}
