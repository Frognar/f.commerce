using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Access.Contracts;

/// <summary>What a session needs to know about its user (re-read on every request; a changed stamp ends the session).</summary>
/// <param name="Memberships">Customers the user may act for, ordered by customer code.</param>
public sealed record SessionView(Guid UserId, string Email, string Name, string Culture, OperatorView? Operator, IReadOnlyList<MembershipView> Memberships);

/// <summary>Operator attributes, as primitives (templates: "platform-administrator", "salesperson").</summary>
public sealed record OperatorView(bool CanManageSettings, bool CanManageOperators, bool CanDecideRegistrations, string CustomerScope);

/// <summary>Signs in with e-mail and password. Failures are deliberately generic ("access.invalid-credentials" or "access.locked-out").</summary>
public sealed record SignIn(string Email, string Password) : ICommand<Result<SignedIn, Error>>;

/// <param name="SecurityStamp">Stored in the session; the session ends when the account's stamp changes.</param>
public sealed record SignedIn(Guid UserId, string SecurityStamp, string Culture);

/// <summary>The session's account, if the account is active and its stamp unchanged.</summary>
public sealed record GetSession(Guid UserId, string SecurityStamp) : IQuery<Result<SessionView, Error>>;

/// <summary>Who an invitation link is for (to greet the person before they choose a password).</summary>
public sealed record GetInvitation(string Token) : IQuery<Result<InvitationView, Error>>;

public sealed record InvitationView(string Email, string Name);

/// <summary>Sets the first password through an invitation link and activates the account.</summary>
public sealed record AcceptInvitation(string Token, string Password) : ICommand<Result<SignedIn, Error>>;

/// <summary>Sends a reset link if an active account has this e-mail; always succeeds, so accounts cannot be probed.</summary>
public sealed record RequestPasswordReset(string Email) : ICommand<Result<Unit, Error>>;

public sealed record ResetPassword(string Token, string Password) : ICommand<Result<Unit, Error>>;

/// <summary>Changes the signed-in user's password; other sessions of the account end.</summary>
public sealed record ChangePassword(Guid UserId, string CurrentPassword, string NewPassword) : ICommand<Result<SignedIn, Error>>;

public sealed record ChangeProfile(Guid UserId, string Name, string Culture) : ICommand<Result<Unit, Error>>;
