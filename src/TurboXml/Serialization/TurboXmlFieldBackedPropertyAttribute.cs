using System;

namespace TurboXml.Serialization;

/// <summary>
/// Configures a serializer context to treat fields marked with an external attribute as generated public properties.
/// </summary>
/// <remarks>
/// <para>
/// The configured marker attribute is identified by its metadata name, for example
/// <c>MyCompany.CodeGeneration.GenerateLazyPropertyAttribute</c>. This lets TurboXml discover the
/// source-visible field before another source generator emits its public property.
/// </para>
/// <para>
/// When <see cref="PropertyNameArgument"/> is configured and that named argument is present on the marker
/// attribute, its value is used as the generated property's name. Otherwise, TurboXml removes an optional
/// leading <c>@</c> from the field name and uppercases its first character.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class TurboXmlFieldBackedPropertyAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TurboXmlFieldBackedPropertyAttribute"/> class.
    /// </summary>
    /// <param name="fieldMarkerAttributeMetadataName">The metadata name of the attribute that marks a backing field.</param>
    /// <exception cref="ArgumentException"><paramref name="fieldMarkerAttributeMetadataName"/> is null, empty, or whitespace.</exception>
    public TurboXmlFieldBackedPropertyAttribute(string fieldMarkerAttributeMetadataName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldMarkerAttributeMetadataName);
        FieldMarkerAttributeMetadataName = fieldMarkerAttributeMetadataName;
    }

    /// <summary>
    /// Gets the metadata name of the attribute that marks a backing field.
    /// </summary>
    public string FieldMarkerAttributeMetadataName { get; }

    /// <summary>
    /// Gets or sets the optional named argument on the marker attribute that supplies the generated property name.
    /// </summary>
    /// <remarks>
    /// When this is <see langword="null"/> or the marker attribute does not provide the named argument,
    /// the field naming convention described by this attribute is used.
    /// </remarks>
    public string? PropertyNameArgument { get; set; }

    /// <summary>
    /// Gets or sets the optional named argument on the marker attribute that supplies restricted XML member overrides.
    /// </summary>
    /// <remarks>
    /// The named argument must be a collection of strings in the form <c>XmlElement("name")</c>. TurboXml
    /// uses the configured XML element name without evaluating source text or using reflection.
    /// </remarks>
    public string? XmlAttributeStringsArgument { get; set; }
}
