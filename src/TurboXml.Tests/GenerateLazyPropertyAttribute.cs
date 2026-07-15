using System;

namespace Devolutions.Roslyn.Attributes;

[AttributeUsage(AttributeTargets.Field)]
public sealed class GenerateLazyPropertyAttribute : Attribute
{
    public string? PropertyName { get; set; }

    public string[]? AdditionalAttributes { get; set; }
}
