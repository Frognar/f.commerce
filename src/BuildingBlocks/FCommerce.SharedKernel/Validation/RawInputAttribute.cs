namespace FCommerce.SharedKernel.Validation;

/// <summary>
/// Marks a type that carries input before it is parsed (an <c>Unvalidated*</c> record, a command of raw form values):
/// its constructor may take primitives, because nothing trusts it until a <c>Parse</c>/<c>Validate</c> function turns
/// it into domain types. Every other domain type is valid by construction (enforced by the architecture tests).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RawInputAttribute : Attribute;
