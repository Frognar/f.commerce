using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Access.Contracts;

/// <summary>Invites a member of the seller's staff (or gives operator rights to an existing account).</summary>
public sealed record InviteOperator(Guid ActorId, string Email, string Name, string Culture, OperatorView Attributes) : ICommand<Result<Guid, Error>>;

public sealed record ChangeOperator(Guid ActorId, Guid OperatorId, OperatorView Attributes) : ICommand<Result<Unit, Error>>;

/// <summary>Removes operator rights and signs the operator out everywhere.</summary>
public sealed record RevokeOperator(Guid ActorId, Guid OperatorId) : ICommand<Result<Unit, Error>>;

public sealed record GetOperators : IQuery<IReadOnlyList<OperatorSummary>>;

public sealed record OperatorSummary(Guid UserId, string Email, string Name, string Status, OperatorView Attributes);
