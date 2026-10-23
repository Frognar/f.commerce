using System.Reflection;
using FCommerce.Application.EventStore;

namespace FCommerce.ArchitectureTests;

/// <summary>Stream categories are constants, so they are validated here instead of at runtime.</summary>
public sealed class StreamTypeRulesTests
{
    [Fact]
    public void Stream_categories_are_valid_and_unique()
    {
        var categories = Solution.All
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsInterface: false, IsAbstract: false } && t.IsAssignableTo(typeof(IStreamType)))
            .Select(t => (Type: t.FullName, Category: CategoryOf(t)))
            .ToList();

        var invalid = categories.Where(c => StreamId.ValidateCategory(c.Category).Match(_ => false, _ => true));
        var duplicated = categories.GroupBy(c => c.Category).Where(g => g.Count() > 1).Select(g => g.Key);

        Assert.Empty(invalid);
        Assert.Empty(duplicated);
    }

    private static string CategoryOf(Type streamType) =>
        (string)typeof(StreamTypeRulesTests)
            .GetMethod(nameof(Read), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(streamType)
            .Invoke(null, null)!;

    private static string Read<T>()
        where T : IStreamType =>
        T.Category;
}
