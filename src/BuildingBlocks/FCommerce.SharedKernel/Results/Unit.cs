namespace FCommerce.SharedKernel.Results;

/// <summary>The type with a single value; used where a result carries no data.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value;
}
