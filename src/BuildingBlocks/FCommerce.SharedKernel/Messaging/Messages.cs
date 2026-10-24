namespace FCommerce.SharedKernel.Messaging;

/// <summary>A request to change state, handled by exactly one module.</summary>
public interface ICommand<TResult>
    where TResult : notnull;

/// <summary>A read-only request; must not change state.</summary>
public interface IQuery<TResult>
    where TResult : notnull;

/// <summary>
/// A fact published by a module for other modules. Part of the module's public contract:
/// once released, change it only additively or publish a new version.
/// </summary>
public interface IIntegrationEvent;
