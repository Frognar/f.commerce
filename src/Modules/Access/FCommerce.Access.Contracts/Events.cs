using FCommerce.SharedKernel.Messaging;

namespace FCommerce.Access.Contracts;

/// <summary>
/// Someone was invited; <see cref="Token"/> is the one-time link token (only its hash is stored by Access).
/// Published as "access.invitation-issued.v1".
/// </summary>
/// <param name="Audience">"operator" or "customer".</param>
/// <param name="CustomerName">The company the invitation is for (customer audience).</param>
public sealed record InvitationIssued(Guid UserId, string Email, string Name, string Culture, string Token, string Audience, string? CustomerName) : IIntegrationEvent;

/// <summary>A password reset was requested for an active account. Published as "access.password-reset-requested.v1".</summary>
public sealed record PasswordResetRequested(Guid UserId, string Email, string Name, string Culture, string Token) : IIntegrationEvent;
