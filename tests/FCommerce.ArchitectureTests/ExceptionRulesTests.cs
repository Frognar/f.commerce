using System.Reflection.Emit;

namespace FCommerce.ArchitectureTests;

/// <summary>
/// The functional core reports every failure as a Result value: no method in SharedKernel or any *.Domain
/// may throw. (BannedApiAnalyzers additionally rejects the common exception types at compile time.)
/// </summary>
public sealed class ExceptionRulesTests
{
    [Fact]
    public void Functional_core_never_throws()
    {
        var violations = Solution.Pure
            .SelectMany(a => IlScanner.MethodsUsing(a, OpCodes.Throw, OpCodes.Rethrow))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Scanner_detects_throwing_code()
    {
        // Guards the guard: this test assembly throws directly and inside a lambda closure.
        var throwing = IlScanner.MethodsUsing(typeof(ExceptionRulesTests).Assembly, OpCodes.Throw).ToList();

        Assert.Contains(throwing, m => m.EndsWith($"{nameof(Thrower)}::{nameof(Thrower.Throw)}", StringComparison.Ordinal));
        Assert.Contains(throwing, m => m.Contains("<>c", StringComparison.Ordinal) && m.Contains(nameof(Thrower.ThrowLater), StringComparison.Ordinal));
    }

    internal static class Thrower
    {
        public static void Throw() => throw new InvalidOperationException("sample");

        public static Func<int> ThrowLater() => () => throw new InvalidOperationException("sample");
    }
}
