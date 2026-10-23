using FCommerce.SharedKernel.Collections;

namespace FCommerce.Authorization.Core;

/// <summary>Outcome of evaluating the policies for a request. Denials carry machine-readable reasons.</summary>
public abstract record Decision
{
    private Decision()
    {
    }

    public static Decision Permit { get; } = new Permitted();

    public static Decision Deny(params ReadOnlySpan<string> reasons) => new Denied([.. reasons]);

    public bool IsPermitted => this is Permitted;

    public sealed record Permitted : Decision;

    public sealed record Denied(EquatableArray<string> Reasons) : Decision;
}

/// <summary>What one policy says about a request.</summary>
public abstract record PolicyResult
{
    private PolicyResult()
    {
    }

    public static PolicyResult Permit { get; } = new PermitResult();

    public static PolicyResult NotApplicable { get; } = new NotApplicableResult();

    public static PolicyResult Deny(string reason) => new DenyResult(reason);

    /// <summary>Permit when the condition holds, otherwise deny with the reason.</summary>
    public static PolicyResult PermitIf(bool condition, string reason) => condition ? Permit : Deny(reason);

    public sealed record PermitResult : PolicyResult;

    public sealed record DenyResult(string Reason) : PolicyResult;

    public sealed record NotApplicableResult : PolicyResult;
}

/// <summary>A policy: a pure function of the request.</summary>
public delegate PolicyResult Policy(AccessRequest request);

public static class PolicyCombining
{
    /// <summary>Deny-overrides with default deny: any deny wins; otherwise at least one permit is needed.</summary>
    public static Decision DenyOverrides(IEnumerable<Policy> policies, AccessRequest request)
    {
        var results = policies.Select(policy => policy(request)).ToList();
        var denials = results.OfType<PolicyResult.DenyResult>().Select(d => d.Reason).Distinct().ToArray();
        return denials.Length > 0
            ? Decision.Deny(denials)
            : results.Any(r => r is PolicyResult.PermitResult)
                ? Decision.Permit
                : Decision.Deny("no-applicable-policy");
    }
}
