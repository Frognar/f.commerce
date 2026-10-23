using FCommerce.Application.EventStore;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Monetary;

namespace FCommerce.Testing;

/// <summary>Known-valid literals for tests.</summary>
public static class Values
{
    public static CultureCode Culture(string code) => CultureCode.Parse(code).ShouldSucceed();

    public static Currency Currency(string code) => SharedKernel.Monetary.Currency.Parse(code).ShouldSucceed();

    public static StreamId Stream(string category, string id) => StreamId.Parse($"{category}-{id}").ShouldSucceed();

    public static StreamId NewStream(string category) => Stream(category, Guid.CreateVersion7().ToString("N"));
}
