// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Thrown by <see cref="ServiceRegistry.Get{T}" /> when the requested service was never
///     registered — which in practice means a subsystem asked for something the bootstrapper does
///     not construct, or asked before it did.
/// </summary>
public sealed class ServiceNotFoundException : Exception {
    /// <summary>The service type that was asked for, when the throw site knew it.</summary>
    public Type? ServiceType { get; }

    /// <summary>Reports that <paramref name="serviceType" /> is not registered.</summary>
    /// <param name="serviceType">The service type that was asked for.</param>
    public ServiceNotFoundException(Type serviceType)
        : base($"No service is registered as {serviceType}.") =>
        ServiceType = serviceType;

    /// <summary>Creates the exception with a default message.</summary>
    public ServiceNotFoundException()
        : base("The requested service is not registered.") { }

    /// <summary>Creates the exception with an explicit message.</summary>
    /// <param name="message">The message.</param>
    public ServiceNotFoundException(string message) : base(message) { }

    /// <summary>Creates the exception with an explicit message and cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ServiceNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}
