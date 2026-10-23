using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;
using FCommerce.Testing;

namespace FCommerce.SharedKernel.Tests;

public sealed class ValidatedTests
{
    private sealed record Person(string Name, int Age, string Email);

    private static readonly Func<string, int, string, Person> CreatePerson = (name, age, email) => new Person(name, age, email);

    private static Validated<string> Name(string value) =>
        string.IsNullOrWhiteSpace(value) ? Error.Validation("name.empty", "Name is required.") with { Field = "name" } : value;

    private static Validated<int> Age(int value) =>
        value > 0 ? value : Error.Validation("age.not-positive", "Age must be positive.") with { Field = "age" };

    private static Validated<string> Email(string value) =>
        value.Contains('@', StringComparison.Ordinal) ? value : Error.Validation("email.invalid", "Invalid e-mail.") with { Field = "email" };

    private static EquatableArray<Error> Errors<T>(Validated<T> validated) => validated.ShouldBeInvalid();

    private static T Value<T>(Validated<T> validated) => validated.ShouldBeValid();

    [Fact]
    public void Apply_builds_the_value_when_every_input_is_valid()
    {
        var person = CreatePerson.Apply(Name("Jan")).Apply(Age(30)).Apply(Email("jan@example.com"));

        Assert.Equal(new Person("Jan", 30, "jan@example.com"), Value(person));
    }

    [Fact]
    public void Apply_collects_the_errors_of_all_invalid_inputs_in_order()
    {
        var person = CreatePerson.Apply(Name("")).Apply(Age(30)).Apply(Email("nope"));

        Assert.Equal(["name.empty", "email.invalid"], Errors(person).Select(e => e.Code));
    }

    [Fact]
    public void Lifting_a_result_places_its_error_under_the_field()
    {
        Result<int, Error> parsed = Error.Validation("quantity.invalid", "x");

        var error = Assert.Single(Errors(parsed.ToValidated("quantity")));

        Assert.Equal("quantity", error.Field);
    }

    [Fact]
    public void Nested_fields_and_list_items_build_a_path()
    {
        static Validated<int> Quantity(int q) => q > 0 ? q : Error.Validation("quantity.invalid", "x") with { Field = "quantity" };

        var lines = new[] { 1, 0, 5, -1 }.Traverse(Quantity).AtField("lines").AtField("order");

        Assert.Equal(["order.lines[1].quantity", "order.lines[3].quantity"], Errors(lines).Select(e => e.Field));
    }

    [Fact]
    public void Traverse_returns_all_values_when_every_item_is_valid()
    {
        Assert.Equal([1, 2], Value(new[] { 1, 2 }.Traverse(Age)));
    }

    [Fact]
    public void To_result_keeps_a_single_error_and_wraps_several()
    {
        var single = CreatePerson.Apply(Name("")).Apply(Age(1)).Apply(Email("a@b")).ToResult().ShouldFail();
        var several = CreatePerson.Apply(Name("")).Apply(Age(0)).Apply(Email("a@b")).ToResult().ShouldFail();

        Assert.Equal("name.empty", single.Code);
        Assert.Equal(Error.ValidationFailedCode, several.Code);
        Assert.Equal(["name.empty", "age.not-positive"], several.Details.Select(e => e.Code));
    }

    [Fact]
    public void Lifting_a_wrapped_result_restores_the_individual_errors()
    {
        var wrapped = CreatePerson.Apply(Name("")).Apply(Age(0)).Apply(Email("a@b")).ToResult();

        Assert.Equal(["customer.name", "customer.age"], Errors(wrapped.ToValidated("customer")).Select(e => e.Field));
    }

    [Fact]
    public void Validated_values_compare_by_value()
    {
        Assert.Equal(Name("Jan"), Name("Jan"));
        Assert.Equal(Name(""), Name(""));
        Assert.NotEqual(Name("Jan"), Name(""));
    }
}
