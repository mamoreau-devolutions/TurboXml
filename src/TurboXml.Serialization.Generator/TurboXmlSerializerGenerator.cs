using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TurboXml.Serialization.Generator;

[Generator]
public sealed class TurboXmlSerializerGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor ContextMustBePartial = new(
        "TXS001",
        "TurboXml serializer context must be partial",
        "The serializer context '{0}' must be declared partial",
        "TurboXml.Serialization",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor UnsupportedProperty = new(
        "TXS002",
        "Unsupported TurboXml serialization property",
        "Property '{0}' on '{1}' is unsupported: {2}",
        "TurboXml.Serialization",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor InvalidFieldBackedPropertyConfiguration = new(
        "TXS003",
        "Invalid TurboXml field-backed property configuration",
        "Field-backed property configuration on serializer context '{0}' is invalid: {1}",
        "TurboXml.Serialization",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor UnusableFieldBackedProperty = new(
        "TXS004",
        "Unusable TurboXml field-backed property",
        "Field '{0}' on '{1}' cannot be used as a field-backed generated property: {2}",
        "TurboXml.Serialization",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contexts = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (generatorContext, _) => GetContext(generatorContext))
            .Where(static candidate => candidate is not null)
            .Collect();

        context.RegisterSourceOutput(contexts, static (productionContext, candidates) =>
        {
            foreach (var candidate in candidates)
            {
                if (candidate is not null)
                {
                    GenerateContext(productionContext, candidate);
                }
            }
        });
    }

    private static ContextCandidate? GetContext(GeneratorSyntaxContext context)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol)
        {
            return null;
        }

        var serializableAttributes = symbol.GetAttributes()
            .Where(static attribute => attribute.AttributeClass?.ToDisplayString() == "TurboXml.Serialization.TurboXmlSerializableAttribute")
            .ToImmutableArray();
        if (serializableAttributes.IsDefaultOrEmpty)
        {
            return null;
        }

        var fieldBackedPropertyAttributes = symbol.GetAttributes()
            .Where(static attribute => attribute.AttributeClass?.ToDisplayString() == "TurboXml.Serialization.TurboXmlFieldBackedPropertyAttribute")
            .ToImmutableArray();
        var invalidFieldMarkerAttributeMetadataNames = fieldBackedPropertyAttributes
            .Where(attribute => attribute.ConstructorArguments.Length != 1
                || attribute.ConstructorArguments[0].Value is not string metadataName
                || context.SemanticModel.Compilation.GetTypeByMetadataName(metadataName) is not INamedTypeSymbol markerAttribute
                || !IsAttributeType(markerAttribute))
            .Select(attribute => attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string metadataName
                ? metadataName
                : string.Empty)
            .ToImmutableArray();
        return new ContextCandidate(
            symbol,
            declaration.Modifiers.Any(SyntaxKind.PartialKeyword),
            serializableAttributes,
            fieldBackedPropertyAttributes,
            invalidFieldMarkerAttributeMetadataNames);
    }

    private static void GenerateContext(SourceProductionContext context, ContextCandidate candidate)
    {
        if (!candidate.IsPartial)
        {
            context.ReportDiagnostic(Diagnostic.Create(ContextMustBePartial, candidate.Symbol.Locations.FirstOrDefault(), candidate.Symbol.Name));
            return;
        }

        var skipElements = GetSkipElements(candidate.Symbol);
        var fieldBackedPropertyConfigurations = GetFieldBackedPropertyConfigurations(context, candidate);
        if (fieldBackedPropertyConfigurations is null)
        {
            return;
        }

        var pendingModels = new Queue<INamedTypeSymbol>();
        var discoveredModels = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var attribute in candidate.SerializableAttributes)
        {
            if (attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is INamedTypeSymbol model
                && discoveredModels.Add(model))
            {
                pendingModels.Enqueue(model);
            }
        }

        var models = new List<ModelInfo>();
        while (pendingModels.Count > 0)
        {
            var model = pendingModels.Dequeue();
            var modelInfo = CreateModelInfo(context, model, skipElements, fieldBackedPropertyConfigurations);
            if (modelInfo is null)
            {
                return;
            }

            modelInfo.Index = models.Count;
            models.Add(modelInfo);
            foreach (var member in modelInfo.Members)
            {
                if (member.ModelType is not null && discoveredModels.Add(member.ModelType))
                {
                    pendingModels.Enqueue(member.ModelType);
                }
            }
        }

        if (models.Count == 0)
        {
            return;
        }

        AssignIdentifiers(models);
        var modelIndexes = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
        foreach (var model in models)
        {
            modelIndexes.Add(model.Symbol, model.Index);
        }

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("#nullable enable");
        if (!candidate.Symbol.ContainingNamespace.IsGlobalNamespace)
        {
            source.Append("namespace ").Append(candidate.Symbol.ContainingNamespace.ToDisplayString()).AppendLine(";");
            source.AppendLine();
        }

        source.Append("partial class ").Append(candidate.Symbol.Name).AppendLine();
        source.AppendLine("{");
        foreach (var model in models)
        {
            EmitTypeInfo(source, model);
        }

        EmitHandler(source, models, modelIndexes);
        source.AppendLine("}");

        context.AddSource($"{candidate.Symbol.Name}.TurboXmlSerializer.g.cs", source.ToString());
    }

    private static Dictionary<INamedTypeSymbol, ImmutableArray<string>> GetSkipElements(INamedTypeSymbol context)
    {
        var result = new Dictionary<INamedTypeSymbol, ImmutableArray<string>>(SymbolEqualityComparer.Default);
        foreach (var attribute in context.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "TurboXml.Serialization.TurboXmlSkipUnknownElementAttribute"
                || attribute.ConstructorArguments.Length != 2
                || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol model
                || attribute.ConstructorArguments[1].Value is not string elementName)
            {
                continue;
            }

            if (result.TryGetValue(model, out var names))
            {
                result[model] = names.Add(elementName);
            }
            else
            {
                result.Add(model, ImmutableArray.Create(elementName));
            }
        }

        return result;
    }

    private static Dictionary<string, FieldBackedPropertyConfiguration>? GetFieldBackedPropertyConfigurations(
        SourceProductionContext context,
        ContextCandidate candidate)
    {
        var configurations = new Dictionary<string, FieldBackedPropertyConfiguration>(StringComparer.Ordinal);
        foreach (var attribute in candidate.FieldBackedPropertyAttributes)
        {
            if (attribute.ConstructorArguments.Length != 1
                || attribute.ConstructorArguments[0].Value is not string markerAttributeMetadataName
                || string.IsNullOrWhiteSpace(markerAttributeMetadataName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidFieldBackedPropertyConfiguration,
                    candidate.Symbol.Locations.FirstOrDefault(),
                    candidate.Symbol.Name,
                    "the field marker attribute metadata name must be non-empty"));
                return null;
            }

            if (candidate.InvalidFieldMarkerAttributeMetadataNames.Contains(markerAttributeMetadataName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidFieldBackedPropertyConfiguration,
                    candidate.Symbol.Locations.FirstOrDefault(),
                    candidate.Symbol.Name,
                    $"the marker attribute '{markerAttributeMetadataName}' does not resolve to an attribute type"));
                return null;
            }

            string? propertyNameArgument = null;
            string? xmlAttributeStringsArgument = null;
            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.Key == "PropertyNameArgument")
                {
                    if (namedArgument.Value.Value is not string configuredPropertyNameArgument
                        || string.IsNullOrWhiteSpace(configuredPropertyNameArgument))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            InvalidFieldBackedPropertyConfiguration,
                            candidate.Symbol.Locations.FirstOrDefault(),
                            candidate.Symbol.Name,
                            "the property-name argument convention must be a non-empty string"));
                        return null;
                    }

                    propertyNameArgument = configuredPropertyNameArgument;
                }
                else if (namedArgument.Key == "XmlAttributeStringsArgument")
                {
                    if (namedArgument.Value.Value is not string configuredXmlAttributeStringsArgument
                        || string.IsNullOrWhiteSpace(configuredXmlAttributeStringsArgument))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            InvalidFieldBackedPropertyConfiguration,
                            candidate.Symbol.Locations.FirstOrDefault(),
                            candidate.Symbol.Name,
                            "the XML attribute strings argument convention must be a non-empty string"));
                        return null;
                    }

                    xmlAttributeStringsArgument = configuredXmlAttributeStringsArgument;
                }
            }

            if (configurations.ContainsKey(markerAttributeMetadataName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidFieldBackedPropertyConfiguration,
                    candidate.Symbol.Locations.FirstOrDefault(),
                    candidate.Symbol.Name,
                    $"the marker attribute '{markerAttributeMetadataName}' is configured more than once"));
                return null;
            }

            configurations.Add(
                markerAttributeMetadataName,
                new FieldBackedPropertyConfiguration(propertyNameArgument, xmlAttributeStringsArgument));
        }

        return configurations;
    }

    private static ModelInfo? CreateModelInfo(
        SourceProductionContext context,
        INamedTypeSymbol model,
        Dictionary<INamedTypeSymbol, ImmutableArray<string>> skipElements,
        Dictionary<string, FieldBackedPropertyConfiguration> fieldBackedPropertyConfigurations)
    {
        if (model.TypeKind != TypeKind.Class
            || model.IsAbstract
            || model.Constructors.All(static constructor => constructor.Parameters.Length != 0 || constructor.DeclaredAccessibility != Accessibility.Public))
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedProperty, model.Locations.FirstOrDefault(), model.Name, model.Name, "a public parameterless constructor is required"));
            return null;
        }

        var members = new List<MemberInfo>();
        string? unknownPropertyName = null;
        foreach (var member in GetSerializableMembers(context, model, fieldBackedPropertyConfigurations))
        {
            if (HasAttribute(member.AttributeSource, "System.Xml.Serialization.XmlIgnoreAttribute"))
            {
                continue;
            }

            if (HasAttribute(member.AttributeSource, "System.Xml.Serialization.XmlAnyElementAttribute"))
            {
                if (member.Type is IArrayTypeSymbol { ElementType: INamedTypeSymbol elementType }
                    && elementType.ToDisplayString() == "System.Xml.XmlElement")
                {
                    unknownPropertyName = member.PropertyName;
                    continue;
                }

                ReportUnsupportedMember(context, member, model, "XmlAnyElement must target XmlElement[]");
                continue;
            }

            var xmlAttribute = GetAttribute(member.AttributeSource, "System.Xml.Serialization.XmlAttributeAttribute");
            var xmlElement = GetAttribute(member.AttributeSource, "System.Xml.Serialization.XmlElementAttribute");
            var xmlArray = GetAttribute(member.AttributeSource, "System.Xml.Serialization.XmlArrayAttribute");
            var xmlArrayItem = GetAttribute(member.AttributeSource, "System.Xml.Serialization.XmlArrayItemAttribute");
            var hasXmlElement = xmlElement is not null || member.XmlElementNameOverride is not null;
            if (xmlAttribute is not null && (hasXmlElement || xmlArray is not null))
            {
                ReportUnsupportedMember(context, member, model, "a property cannot combine XmlAttribute with an XML element or array attribute");
                continue;
            }

            var collection = GetCollectionInfo(member.Type);
            if (collection is not null)
            {
                if (xmlAttribute is not null)
                {
                    ReportUnsupportedMember(context, member, model, "collections cannot be XML attributes");
                    continue;
                }

                if (xmlArray is not null && hasXmlElement)
                {
                    ReportUnsupportedMember(context, member, model, "a collection cannot combine XmlArray with XmlElement");
                    continue;
                }

                var collectionModelType = collection.ElementType as INamedTypeSymbol;
                if (!TryGetScalarKind(collection.ElementType, out var collectionScalarKind)
                    && collectionModelType is null)
                {
                    ReportUnsupportedMember(context, member, model, "collection elements must be supported scalars, enums, or model classes");
                    continue;
                }

                var collectionStyle = hasXmlElement ? CollectionStyle.Flat : CollectionStyle.Wrapped;
                var elementName = collectionStyle == CollectionStyle.Wrapped
                    ? xmlArray is null ? member.PropertyName : GetXmlName(xmlArray, member.PropertyName)
                    : GetXmlElementName(xmlElement, member.PropertyName, member.XmlElementNameOverride);
                var itemElementName = collectionStyle == CollectionStyle.Wrapped
                    ? xmlArrayItem is null ? GetDefaultElementName(collection.ElementType) : GetXmlName(xmlArrayItem, GetDefaultElementName(collection.ElementType))
                    : elementName;
                var kind = collectionScalarKind == ScalarKind.None ? MemberKind.ModelCollection : MemberKind.ScalarCollection;
                members.Add(new MemberInfo(
                    member.PropertyName,
                    null,
                    elementName,
                    kind,
                    collectionScalarKind,
                    collection.ElementType,
                    collectionScalarKind == ScalarKind.None ? collectionModelType : null,
                    collectionStyle,
                    itemElementName,
                    collection.AssignmentKind));
                continue;
            }

            if (xmlArray is not null || xmlArrayItem is not null)
            {
                ReportUnsupportedMember(context, member, model, "XmlArray and XmlArrayItem require an array, List<T>, or a supported list interface");
                continue;
            }

            if (TryGetScalarKind(member.Type, out var scalarKind))
            {
                var attributeName = xmlAttribute is null ? null : GetXmlName(xmlAttribute, member.PropertyName);
                var elementName = xmlAttribute is not null
                    ? null
                    : GetXmlElementName(xmlElement, member.PropertyName, member.XmlElementNameOverride);
                members.Add(new MemberInfo(
                    member.PropertyName,
                    attributeName,
                    elementName,
                    MemberKind.Scalar,
                    scalarKind,
                    member.Type,
                    null,
                    CollectionStyle.None,
                    null,
                    CollectionAssignmentKind.None));
                continue;
            }

            if (xmlAttribute is not null)
            {
                ReportUnsupportedMember(context, member, model, "nested models cannot be XML attributes");
                continue;
            }

            if (member.Type is not INamedTypeSymbol nestedModelType)
            {
                ReportUnsupportedMember(context, member, model, "nested model types must be named classes");
                continue;
            }

            members.Add(new MemberInfo(
                member.PropertyName,
                null,
                GetXmlElementName(xmlElement, member.PropertyName, member.XmlElementNameOverride),
                MemberKind.Model,
                ScalarKind.None,
                member.Type,
                nestedModelType,
                CollectionStyle.None,
                null,
                CollectionAssignmentKind.None));
        }

        var rootAttribute = GetAttribute(model, "System.Xml.Serialization.XmlRootAttribute");
        var typeAttribute = GetAttribute(model, "System.Xml.Serialization.XmlTypeAttribute");
        var rootName = GetExplicitXmlRootName(rootAttribute)
            ?? GetXmlTypeName(typeAttribute)
            ?? model.Name;
        skipElements.TryGetValue(model, out var skips);
        return new ModelInfo(model, rootName, members, unknownPropertyName, skips);
    }

    private static void AssignIdentifiers(List<ModelInfo> models)
    {
        var identifiers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var identifier = model.Symbol.Name + "TypeInfo";
            if (identifiers.TryGetValue(identifier, out var count))
            {
                count++;
                identifiers[identifier] = count;
                model.Identifier = identifier + count;
            }
            else
            {
                identifiers.Add(identifier, 0);
                model.Identifier = identifier;
            }
        }
    }

    private static void EmitTypeInfo(StringBuilder source, ModelInfo model)
    {
        var typeName = FullyQualified(model.Symbol);
        source.Append("    public global::TurboXml.Serialization.TurboXmlTypeInfo<").Append(typeName).Append("> ").Append(model.Identifier).AppendLine(" { get; } = new(");
        source.Append("        Deserialize").Append(model.Identifier).AppendLine(",");
        source.Append("        Deserialize").Append(model.Identifier).AppendLine("FromStream,");
        source.Append("        Deserialize").Append(model.Identifier).AppendLine("Array,");
        source.Append("        Deserialize").Append(model.Identifier).AppendLine("ArrayFromStream);");
        source.AppendLine();
        source.Append("    private static ").Append(typeName).Append(" Deserialize").Append(model.Identifier).AppendLine("(string xml)");
        source.AppendLine("    {");
        source.Append("        var handler = new TurboXmlGeneratedHandler(").Append(model.Index).AppendLine(", null);");
        source.AppendLine("        global::TurboXml.XmlParser.Parse(xml, ref handler);");
        source.Append("        return (").Append(typeName).AppendLine(")handler.GetResult();");
        source.AppendLine("    }");
        source.AppendLine();
        source.Append("    private static ").Append(typeName).Append(" Deserialize").Append(model.Identifier).AppendLine("FromStream(global::System.IO.Stream stream)");
        source.AppendLine("    {");
        source.Append("        var handler = new TurboXmlGeneratedHandler(").Append(model.Index).AppendLine(", null);");
        source.AppendLine("        global::TurboXml.XmlParser.Parse(stream, ref handler);");
        source.Append("        return (").Append(typeName).AppendLine(")handler.GetResult();");
        source.AppendLine("    }");
        source.AppendLine();
        source.Append("    private static ").Append(typeName).Append("[] Deserialize").Append(model.Identifier).AppendLine("Array(string xml, string itemElementName)");
        source.AppendLine("    {");
        source.Append("        var handler = new TurboXmlGeneratedHandler(").Append(model.Index).AppendLine(", itemElementName);");
        source.AppendLine("        global::TurboXml.XmlParser.Parse(xml, ref handler);");
        source.Append("        return handler.GetArrayResult<").Append(typeName).AppendLine(">();");
        source.AppendLine("    }");
        source.AppendLine();
        source.Append("    private static ").Append(typeName).Append("[] Deserialize").Append(model.Identifier).AppendLine("ArrayFromStream(global::System.IO.Stream stream, string itemElementName)");
        source.AppendLine("    {");
        source.Append("        var handler = new TurboXmlGeneratedHandler(").Append(model.Index).AppendLine(", itemElementName);");
        source.AppendLine("        global::TurboXml.XmlParser.Parse(stream, ref handler);");
        source.Append("        return handler.GetArrayResult<").Append(typeName).AppendLine(">();");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitHandler(
        StringBuilder source,
        List<ModelInfo> models,
        Dictionary<INamedTypeSymbol, int> modelIndexes)
    {
        source.AppendLine("    private struct TurboXmlGeneratedHandler : global::TurboXml.IXmlReadHandler");
        source.AppendLine("    {");
        source.AppendLine("        private sealed class Frame");
        source.AppendLine("        {");
        source.AppendLine("            public object Model = null!;");
        source.AppendLine("            public int ModelIndex;");
        source.AppendLine("            public int ElementDepth;");
        source.AppendLine("            public Frame? Parent;");
        source.AppendLine("            public int ParentMember;");
        source.AppendLine("            public int CurrentMember = -1;");
        source.AppendLine("            public int CurrentMemberDepth;");
        source.AppendLine("            public bool CurrentMemberIsCollection;");
        source.AppendLine("            public int CollectionMember = -1;");
        source.AppendLine("            public int CollectionDepth;");
        source.AppendLine("            public object?[]? Collections;");
        source.AppendLine("            public global::System.Xml.XmlDocument? UnknownDocument;");
        source.AppendLine("            public global::System.Collections.Generic.List<global::System.Xml.XmlElement>? UnknownStack;");
        source.AppendLine("            public global::System.Collections.Generic.List<global::System.Xml.XmlElement>? UnknownElements;");
        source.AppendLine("            public int SkipDepth;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private readonly int _rootModelIndex;");
        source.AppendLine("        private readonly string? _itemElementName;");
        source.AppendLine("        private int _depth;");
        source.AppendLine("        private global::System.Collections.Generic.List<Frame>? _frames;");
        source.AppendLine("        private object? _result;");
        source.AppendLine("        private global::System.Collections.Generic.List<object>? _results;");
        source.AppendLine();
        source.AppendLine("        public TurboXmlGeneratedHandler(int rootModelIndex, string? itemElementName)");
        source.AppendLine("        {");
        source.AppendLine("            _rootModelIndex = rootModelIndex;");
        source.AppendLine("            _itemElementName = itemElementName;");
        source.AppendLine("            _depth = 0;");
        source.AppendLine("            _frames = null;");
        source.AppendLine("            _result = null;");
        source.AppendLine("            _results = null;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        public void OnBeginTag(global::System.ReadOnlySpan<char> name, int line, int column)");
        source.AppendLine("        {");
        source.AppendLine("            _depth++;");
        source.AppendLine("            if (_frames is { Count: > 0 })");
        source.AppendLine("            {");
        source.AppendLine("                var frame = GetCurrentFrame();");
        source.AppendLine("                if (IsCapturingUnknown(frame)) { AddUnknownElement(frame, name); return; }");
        source.AppendLine("                if (frame.SkipDepth != 0) return;");
        source.AppendLine("                if (_depth == frame.ElementDepth + 1) { DispatchDirectElement(frame, name); return; }");
        source.AppendLine("                if (frame.CollectionDepth != 0 && _depth == frame.CollectionDepth + 1) { DispatchCollectionElement(frame, name); return; }");
        source.AppendLine("                return;");
        source.AppendLine("            }");
        source.AppendLine("            if (_depth == 1)");
        source.AppendLine("            {");
        source.AppendLine("                if (_itemElementName is null)");
        source.AppendLine("                {");
        source.AppendLine("                    if (!IsRootName(_rootModelIndex, name)) throw new global::System.Xml.XmlException(\"Unexpected root element.\");");
        source.AppendLine("                    StartModel(_rootModelIndex, null, -1);");
        source.AppendLine("                }");
        source.AppendLine("                return;");
        source.AppendLine("            }");
        source.AppendLine("            if (_itemElementName is not null && _depth == 2 && name.SequenceEqual(_itemElementName.AsSpan())) StartModel(_rootModelIndex, null, -1);");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        public void OnAttribute(global::System.ReadOnlySpan<char> name, global::System.ReadOnlySpan<char> value, int nameLine, int nameColumn, int valueLine, int valueColumn)");
        source.AppendLine("        {");
        source.AppendLine("            if (_frames is not { Count: > 0 }) return;");
        source.AppendLine("            var frame = GetCurrentFrame();");
        source.AppendLine("            if (IsCapturingUnknown(frame)) { AddUnknownAttribute(frame, name, value); return; }");
        source.AppendLine("            if (frame.SkipDepth != 0 || _depth != frame.ElementDepth) return;");
        source.AppendLine("            DispatchAttribute(frame, name, value);");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        public void OnText(global::System.ReadOnlySpan<char> text, int line, int column)");
        source.AppendLine("        {");
        source.AppendLine("            if (_frames is not { Count: > 0 }) return;");
        source.AppendLine("            var frame = GetCurrentFrame();");
        source.AppendLine("            if (IsCapturingUnknown(frame)) { AddUnknownText(frame, text); return; }");
        source.AppendLine("            if (frame.SkipDepth == 0 && frame.CurrentMember >= 0 && frame.CurrentMemberDepth == _depth)");
        source.AppendLine("            {");
        source.AppendLine("                if (frame.CurrentMemberIsCollection) AddScalarCollectionMember(frame, frame.CurrentMember, text);");
        source.AppendLine("                else SetScalarMember(frame, frame.CurrentMember, text);");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        public void OnCData(global::System.ReadOnlySpan<char> cdata, int line, int column)");
        source.AppendLine("        {");
        source.AppendLine("            if (_frames is { Count: > 0 } && IsCapturingUnknown(GetCurrentFrame())) AddUnknownCData(GetCurrentFrame(), cdata);");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        public void OnComment(global::System.ReadOnlySpan<char> comment, int line, int column)");
        source.AppendLine("        {");
        source.AppendLine("            if (_frames is { Count: > 0 } && IsCapturingUnknown(GetCurrentFrame())) AddUnknownComment(GetCurrentFrame(), comment);");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        public void OnXmlDeclaration(global::System.ReadOnlySpan<char> version, global::System.ReadOnlySpan<char> encoding, global::System.ReadOnlySpan<char> standalone, int line, int column) { }");
        source.AppendLine("        public void OnError(string message, int line, int column) => throw new global::System.Xml.XmlException(message, null, line + 1, column + 1);");
        source.AppendLine();
        source.AppendLine("        public void OnEndTag(global::System.ReadOnlySpan<char> name, int line, int column) => EndElement();");
        source.AppendLine("        public void OnEndTagEmpty() => EndElement();");
        source.AppendLine();
        source.AppendLine("        public object GetResult() => _result ?? throw new global::System.Xml.XmlException(\"No root element was found.\");");
        source.AppendLine("        public T[] GetArrayResult<T>()");
        source.AppendLine("        {");
        source.AppendLine("            if (_results is not { Count: > 0 }) return [];");
        source.AppendLine("            var result = new T[_results.Count];");
        source.AppendLine("            for (var index = 0; index < result.Length; index++) result[index] = (T)_results[index];");
        source.AppendLine("            return result;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private Frame GetCurrentFrame() => _frames![_frames.Count - 1];");
        source.AppendLine("        private static bool IsCapturingUnknown(Frame frame) => frame.UnknownStack is not null;");
        source.AppendLine();
        source.AppendLine("        private void EndElement()");
        source.AppendLine("        {");
        source.AppendLine("            if (_frames is not { Count: > 0 }) { _depth--; return; }");
        source.AppendLine("            var frame = GetCurrentFrame();");
        source.AppendLine("            if (IsCapturingUnknown(frame)) { EndUnknownElement(frame); _depth--; return; }");
        source.AppendLine("            if (frame.SkipDepth != 0) { if (_depth == frame.SkipDepth) frame.SkipDepth = 0; _depth--; return; }");
        source.AppendLine("            if (frame.CurrentMemberDepth == _depth) { frame.CurrentMember = -1; frame.CurrentMemberDepth = 0; frame.CurrentMemberIsCollection = false; }");
        source.AppendLine("            if (frame.CollectionDepth == _depth) { frame.CollectionMember = -1; frame.CollectionDepth = 0; }");
        source.AppendLine("            if (_depth == frame.ElementDepth) FinishModel(frame);");
        source.AppendLine("            _depth--;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private void StartModel(int modelIndex, Frame? parent, int parentMember)");
        source.AppendLine("        {");
        source.AppendLine("            var frame = new Frame { ModelIndex = modelIndex, ElementDepth = _depth, Parent = parent, ParentMember = parentMember };");
        source.AppendLine("            switch (modelIndex)");
        source.AppendLine("            {");
        foreach (var model in models)
        {
            source.Append("                case ").Append(model.Index).Append(": frame.Model = new ").Append(FullyQualified(model.Symbol)).AppendLine("(); break;");
        }

        source.AppendLine("                default: throw new global::System.InvalidOperationException(\"Unknown generated model.\");");
        source.AppendLine("            }");
        source.AppendLine("            (_frames ??= new global::System.Collections.Generic.List<Frame>()).Add(frame);");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private void FinishModel(Frame frame)");
        source.AppendLine("        {");
        source.AppendLine("            AssignCollections(frame);");
        source.AppendLine("            AssignUnknownElements(frame);");
        source.AppendLine("            var completed = frame.Model;");
        source.AppendLine("            _frames!.RemoveAt(_frames.Count - 1);");
        source.AppendLine("            if (frame.Parent is null)");
        source.AppendLine("            {");
        source.AppendLine("                if (_itemElementName is null) _result = completed;");
        source.AppendLine("                else (_results ??= new global::System.Collections.Generic.List<object>()).Add(completed);");
        source.AppendLine("                return;");
        source.AppendLine("            }");
        source.AppendLine("            AttachCompletedModel(frame.Parent, frame.ParentMember, completed);");
        source.AppendLine("        }");
        source.AppendLine();
        EmitRootNameCheck(source, models);
        EmitSkipCheck(source, models);
        EmitDirectElementDispatch(source, models, modelIndexes);
        EmitCollectionElementDispatch(source, models, modelIndexes);
        EmitAttributeDispatch(source, models);
        EmitScalarMemberSetter(source, models);
        EmitCollectionSupport(source, models);
        EmitCompletedModelAttachment(source, models, modelIndexes);
        EmitUnknownCapture(source, models);
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitRootNameCheck(StringBuilder source, List<ModelInfo> models)
    {
        source.AppendLine("        private static bool IsRootName(int modelIndex, global::System.ReadOnlySpan<char> name)");
        source.AppendLine("        {");
        source.AppendLine("            switch (modelIndex)");
        source.AppendLine("            {");
        foreach (var model in models)
        {
            source.Append("                case ").Append(model.Index).Append(": return name.SequenceEqual(").Append(Literal(model.RootName)).AppendLine(".AsSpan());");
        }

        source.AppendLine("                default: return false;");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
    }

    private static void EmitSkipCheck(StringBuilder source, List<ModelInfo> models)
    {
        source.AppendLine("        private bool TryStartSkip(Frame frame, global::System.ReadOnlySpan<char> name)");
        source.AppendLine("        {");
        source.AppendLine("            switch (frame.ModelIndex)");
        source.AppendLine("            {");
        foreach (var model in models)
        {
            source.Append("                case ").Append(model.Index).AppendLine(":");
            foreach (var skip in model.SkipElements)
            {
                source.Append("                    if (name.SequenceEqual(").Append(Literal(skip)).AppendLine(".AsSpan())) { frame.SkipDepth = _depth; return true; }");
            }

            source.AppendLine("                    return false;");
        }

        source.AppendLine("                default: return false;");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
    }

    private static void EmitDirectElementDispatch(
        StringBuilder source,
        List<ModelInfo> models,
        Dictionary<INamedTypeSymbol, int> modelIndexes)
    {
        source.AppendLine("        private void DispatchDirectElement(Frame frame, global::System.ReadOnlySpan<char> name)");
        source.AppendLine("        {");
        source.AppendLine("            if (TryStartSkip(frame, name)) return;");
        source.AppendLine("            switch (frame.ModelIndex)");
        source.AppendLine("            {");
        foreach (var model in models)
        {
            source.Append("                case ").Append(model.Index).AppendLine(":");
            for (var index = 0; index < model.Members.Count; index++)
            {
                var member = model.Members[index];
                if (member.ElementName is null)
                {
                    continue;
                }

                source.Append("                    if (name.SequenceEqual(").Append(Literal(member.ElementName)).AppendLine(".AsSpan()))");
                source.AppendLine("                    {");
                switch (member.Kind)
                {
                    case MemberKind.Scalar:
                        source.Append("                        BeginScalarMember(frame, ").Append(index).AppendLine(", false);");
                        break;
                    case MemberKind.Model:
                        source.Append("                        StartModel(").Append(modelIndexes[member.ModelType!]).Append(", frame, ").Append(index).AppendLine(");");
                        break;
                    case MemberKind.ScalarCollection when member.CollectionStyle == CollectionStyle.Wrapped:
                    case MemberKind.ModelCollection when member.CollectionStyle == CollectionStyle.Wrapped:
                        source.Append("                        EnsureCollection(frame, ").Append(index).AppendLine(");");
                        source.Append("                        frame.CollectionMember = ").Append(index).AppendLine(";");
                        source.AppendLine("                        frame.CollectionDepth = _depth;");
                        break;
                    case MemberKind.ScalarCollection:
                        source.Append("                        EnsureCollection(frame, ").Append(index).AppendLine(");");
                        source.Append("                        BeginScalarMember(frame, ").Append(index).AppendLine(", true);");
                        break;
                    case MemberKind.ModelCollection:
                        source.Append("                        EnsureCollection(frame, ").Append(index).AppendLine(");");
                        source.Append("                        StartModel(").Append(modelIndexes[member.ModelType!]).Append(", frame, ").Append(index).AppendLine(");");
                        break;
                }

                source.AppendLine("                        return;");
                source.AppendLine("                    }");
            }

            source.AppendLine("                    CaptureUnknown(frame, name);");
            source.AppendLine("                    return;");
        }

        source.AppendLine("                default: throw new global::System.InvalidOperationException(\"Unknown generated model.\");");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
    }

    private static void EmitCollectionElementDispatch(
        StringBuilder source,
        List<ModelInfo> models,
        Dictionary<INamedTypeSymbol, int> modelIndexes)
    {
        source.AppendLine("        private void DispatchCollectionElement(Frame frame, global::System.ReadOnlySpan<char> name)");
        source.AppendLine("        {");
        source.AppendLine("            if (TryStartSkip(frame, name)) return;");
        source.AppendLine("            switch (frame.ModelIndex)");
        source.AppendLine("            {");
        foreach (var model in models)
        {
            source.Append("                case ").Append(model.Index).AppendLine(":");
            for (var index = 0; index < model.Members.Count; index++)
            {
                var member = model.Members[index];
                if (!member.IsCollection || member.CollectionStyle != CollectionStyle.Wrapped)
                {
                    continue;
                }

                source.Append("                    if (frame.CollectionMember == ").Append(index).Append(" && name.SequenceEqual(").Append(Literal(member.ItemElementName!)).AppendLine(".AsSpan()))");
                source.AppendLine("                    {");
                if (member.Kind == MemberKind.ScalarCollection)
                {
                    source.Append("                        BeginScalarMember(frame, ").Append(index).AppendLine(", true);");
                }
                else
                {
                    source.Append("                        StartModel(").Append(modelIndexes[member.ModelType!]).Append(", frame, ").Append(index).AppendLine(");");
                }

                source.AppendLine("                        return;");
                source.AppendLine("                    }");
            }

            source.AppendLine("                    CaptureUnknown(frame, name);");
            source.AppendLine("                    return;");
        }

        source.AppendLine("                default: throw new global::System.InvalidOperationException(\"Unknown generated model.\");");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
    }

    private static void EmitAttributeDispatch(StringBuilder source, List<ModelInfo> models)
    {
        source.AppendLine("        private void DispatchAttribute(Frame frame, global::System.ReadOnlySpan<char> name, global::System.ReadOnlySpan<char> value)");
        source.AppendLine("        {");
        source.AppendLine("            switch (frame.ModelIndex)");
        source.AppendLine("            {");
        foreach (var model in models)
        {
            source.Append("                case ").Append(model.Index).AppendLine(":");
            for (var index = 0; index < model.Members.Count; index++)
            {
                var member = model.Members[index];
                if (member.AttributeName is not null)
                {
                    source.Append("                    if (name.SequenceEqual(").Append(Literal(member.AttributeName)).Append(".AsSpan())) { SetScalarMember(frame, ").Append(index).AppendLine(", value); return; }");
                }
            }

            source.AppendLine("                    return;");
        }

        source.AppendLine("                default: throw new global::System.InvalidOperationException(\"Unknown generated model.\");");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
    }

    private static void EmitScalarMemberSetter(StringBuilder source, List<ModelInfo> models)
    {
        source.AppendLine("        private void BeginScalarMember(Frame frame, int member, bool isCollection)");
        source.AppendLine("        {");
        source.AppendLine("            frame.CurrentMember = member;");
        source.AppendLine("            frame.CurrentMemberDepth = _depth;");
        source.AppendLine("            frame.CurrentMemberIsCollection = isCollection;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private void SetScalarMember(Frame frame, int member, global::System.ReadOnlySpan<char> value)");
        source.AppendLine("        {");
        source.AppendLine("            switch (frame.ModelIndex)");
        source.AppendLine("            {");
        foreach (var model in models)
        {
            source.Append("                case ").Append(model.Index).AppendLine(":");
            source.AppendLine("                    switch (member)");
            source.AppendLine("                    {");
            for (var index = 0; index < model.Members.Count; index++)
            {
                var member = model.Members[index];
                if (member.Kind == MemberKind.Scalar)
                {
                    source.Append("                        case ").Append(index).Append(": { ((").Append(FullyQualified(model.Symbol)).Append(")frame.Model).").Append(GetMemberAccessName(member.PropertyName)).Append(" = ").Append(GetParseExpression(member.ScalarKind, member.ValueType)).AppendLine("; return; }");
                }
            }

            source.AppendLine("                        default: throw new global::System.InvalidOperationException(\"Unknown generated scalar member.\");");
            source.AppendLine("                    }");
        }

        source.AppendLine("                default: throw new global::System.InvalidOperationException(\"Unknown generated model.\");");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
    }

    private static void EmitCollectionSupport(StringBuilder source, List<ModelInfo> models)
    {
        source.AppendLine("        private void EnsureCollection(Frame frame, int member)");
        source.AppendLine("        {");
        source.AppendLine("            switch (frame.ModelIndex)");
        source.AppendLine("            {");
        foreach (var model in models)
        {
            source.Append("                case ").Append(model.Index).AppendLine(":");
            source.Append("                    frame.Collections ??= new object?[").Append(model.Members.Count).AppendLine("];");
            source.AppendLine("                    switch (member)");
            source.AppendLine("                    {");
            for (var index = 0; index < model.Members.Count; index++)
            {
                var member = model.Members[index];
                if (member.IsCollection)
                {
                    source.Append("                        case ").Append(index).Append(": if (frame.Collections[").Append(index).Append("] is null) frame.Collections[").Append(index).Append("] = new global::System.Collections.Generic.List<").Append(FullyQualified(member.ValueType)).AppendLine(">(); return;");
                }
            }

            source.AppendLine("                        default: throw new global::System.InvalidOperationException(\"Unknown generated collection member.\");");
            source.AppendLine("                    }");
        }

        source.AppendLine("                default: throw new global::System.InvalidOperationException(\"Unknown generated model.\");");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private void AddScalarCollectionMember(Frame frame, int member, global::System.ReadOnlySpan<char> value)");
        source.AppendLine("        {");
        source.AppendLine("            EnsureCollection(frame, member);");
        source.AppendLine("            switch (frame.ModelIndex)");
        source.AppendLine("            {");
        foreach (var model in models)
        {
            source.Append("                case ").Append(model.Index).AppendLine(":");
            source.AppendLine("                    switch (member)");
            source.AppendLine("                    {");
            for (var index = 0; index < model.Members.Count; index++)
            {
                var member = model.Members[index];
                if (member.Kind == MemberKind.ScalarCollection)
                {
                    source.Append("                        case ").Append(index).Append(": { ((global::System.Collections.Generic.List<").Append(FullyQualified(member.ValueType)).Append(">)frame.Collections![").Append(index).Append("]!).Add(").Append(GetParseExpression(member.ScalarKind, member.ValueType)).AppendLine("); return; }");
                }
            }

            source.AppendLine("                        default: throw new global::System.InvalidOperationException(\"Unknown generated scalar collection member.\");");
            source.AppendLine("                    }");
        }

        source.AppendLine("                default: throw new global::System.InvalidOperationException(\"Unknown generated model.\");");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private void AssignCollections(Frame frame)");
        source.AppendLine("        {");
        source.AppendLine("            if (frame.Collections is null) return;");
        source.AppendLine("            switch (frame.ModelIndex)");
        source.AppendLine("            {");
        foreach (var model in models)
        {
            source.Append("                case ").Append(model.Index).AppendLine(":");
            for (var index = 0; index < model.Members.Count; index++)
            {
                var member = model.Members[index];
                if (!member.IsCollection)
                {
                    continue;
                }

                var list = "((global::System.Collections.Generic.List<" + FullyQualified(member.ValueType) + ">)frame.Collections[" + index + "]!)";
                source.Append("                    if (frame.Collections[").Append(index).Append("] is not null) ((").Append(FullyQualified(model.Symbol)).Append(")frame.Model).").Append(GetMemberAccessName(member.PropertyName)).Append(" = ").Append(member.CollectionAssignmentKind == CollectionAssignmentKind.Array ? list + ".ToArray()" : list).AppendLine(";");
            }

            source.AppendLine("                    return;");
        }

        source.AppendLine("                default: throw new global::System.InvalidOperationException(\"Unknown generated model.\");");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
    }

    private static void EmitCompletedModelAttachment(
        StringBuilder source,
        List<ModelInfo> models,
        Dictionary<INamedTypeSymbol, int> modelIndexes)
    {
        source.AppendLine("        private void AttachCompletedModel(Frame parent, int member, object completed)");
        source.AppendLine("        {");
        source.AppendLine("            switch (parent.ModelIndex)");
        source.AppendLine("            {");
        foreach (var model in models)
        {
            source.Append("                case ").Append(model.Index).AppendLine(":");
            source.AppendLine("                    switch (member)");
            source.AppendLine("                    {");
            for (var index = 0; index < model.Members.Count; index++)
            {
                var member = model.Members[index];
                if (member.Kind == MemberKind.Model)
                {
                    source.Append("                        case ").Append(index).Append(": ((").Append(FullyQualified(model.Symbol)).Append(")parent.Model).").Append(GetMemberAccessName(member.PropertyName)).Append(" = (").Append(FullyQualified(member.ValueType)).AppendLine(")completed; return;");
                }
                else if (member.Kind == MemberKind.ModelCollection)
                {
                    source.Append("                        case ").Append(index).Append(": EnsureCollection(parent, ").Append(index).Append("); ((global::System.Collections.Generic.List<").Append(FullyQualified(member.ValueType)).Append(">)parent.Collections![").Append(index).Append("]!).Add((").Append(FullyQualified(member.ValueType)).AppendLine(")completed); return;");
                }
            }

            source.AppendLine("                        default: throw new global::System.InvalidOperationException(\"Unknown generated model member.\");");
            source.AppendLine("                    }");
        }

        source.AppendLine("                default: throw new global::System.InvalidOperationException(\"Unknown generated model.\");");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
    }

    private static void EmitUnknownCapture(StringBuilder source, List<ModelInfo> models)
    {
        source.AppendLine("        private void CaptureUnknown(Frame frame, global::System.ReadOnlySpan<char> name)");
        source.AppendLine("        {");
        source.AppendLine("            switch (frame.ModelIndex)");
        source.AppendLine("            {");
        foreach (var model in models.Where(static model => model.UnknownPropertyName is not null))
        {
            source.Append("                case ").Append(model.Index).AppendLine(":");
            source.AppendLine("                {");
            source.AppendLine("                    frame.UnknownDocument = new global::System.Xml.XmlDocument();");
            source.AppendLine("                    var element = frame.UnknownDocument.CreateElement(name.ToString());");
            source.AppendLine("                    frame.UnknownDocument.AppendChild(element);");
            source.AppendLine("                    frame.UnknownStack = new global::System.Collections.Generic.List<global::System.Xml.XmlElement> { element };");
            source.AppendLine("                    return;");
            source.AppendLine("                }");
        }

        source.AppendLine("                default: return;");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private static void AddUnknownElement(Frame frame, global::System.ReadOnlySpan<char> name)");
        source.AppendLine("        {");
        source.AppendLine("            var element = frame.UnknownDocument!.CreateElement(name.ToString());");
        source.AppendLine("            frame.UnknownStack![frame.UnknownStack.Count - 1].AppendChild(element);");
        source.AppendLine("            frame.UnknownStack.Add(element);");
        source.AppendLine("        }");
        source.AppendLine("        private static void AddUnknownAttribute(Frame frame, global::System.ReadOnlySpan<char> name, global::System.ReadOnlySpan<char> value) => frame.UnknownStack![frame.UnknownStack.Count - 1].SetAttribute(name.ToString(), value.ToString());");
        source.AppendLine("        private static void AddUnknownText(Frame frame, global::System.ReadOnlySpan<char> value) => frame.UnknownStack![frame.UnknownStack.Count - 1].AppendChild(frame.UnknownDocument!.CreateTextNode(value.ToString()));");
        source.AppendLine("        private static void AddUnknownCData(Frame frame, global::System.ReadOnlySpan<char> value) => frame.UnknownStack![frame.UnknownStack.Count - 1].AppendChild(frame.UnknownDocument!.CreateCDataSection(value.ToString()));");
        source.AppendLine("        private static void AddUnknownComment(Frame frame, global::System.ReadOnlySpan<char> value) => frame.UnknownStack![frame.UnknownStack.Count - 1].AppendChild(frame.UnknownDocument!.CreateComment(value.ToString()));");
        source.AppendLine("        private static void EndUnknownElement(Frame frame)");
        source.AppendLine("        {");
        source.AppendLine("            var index = frame.UnknownStack!.Count - 1;");
        source.AppendLine("            var element = frame.UnknownStack[index];");
        source.AppendLine("            frame.UnknownStack.RemoveAt(index);");
        source.AppendLine("            if (frame.UnknownStack.Count != 0) return;");
        source.AppendLine("            (frame.UnknownElements ??= new global::System.Collections.Generic.List<global::System.Xml.XmlElement>()).Add(element);");
        source.AppendLine("            frame.UnknownDocument = null;");
        source.AppendLine("            frame.UnknownStack = null;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private static void AssignUnknownElements(Frame frame)");
        source.AppendLine("        {");
        source.AppendLine("            if (frame.UnknownElements is not { Count: > 0 }) return;");
        source.AppendLine("            switch (frame.ModelIndex)");
        source.AppendLine("            {");
        foreach (var model in models.Where(static model => model.UnknownPropertyName is not null))
        {
            source.Append("                case ").Append(model.Index).Append(": ((").Append(FullyQualified(model.Symbol)).Append(")frame.Model).").Append(GetMemberAccessName(model.UnknownPropertyName!)).AppendLine(" = frame.UnknownElements.ToArray(); return;");
        }

        source.AppendLine("                default: return;");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
    }

    private static string GetParseExpression(ScalarKind kind, ITypeSymbol type)
    {
        var targetType = FullyQualified(GetNonNullableType(type));
        return kind switch
        {
            ScalarKind.String => "value.ToString()",
            ScalarKind.Boolean => "global::System.Xml.XmlConvert.ToBoolean(value.ToString())",
            ScalarKind.UInt16 => "ushort.Parse(value, global::System.Globalization.CultureInfo.InvariantCulture)",
            ScalarKind.Int32 => "int.Parse(value, global::System.Globalization.CultureInfo.InvariantCulture)",
            ScalarKind.Int64 => "long.Parse(value, global::System.Globalization.CultureInfo.InvariantCulture)",
            ScalarKind.Double => "double.Parse(value, global::System.Globalization.CultureInfo.InvariantCulture)",
            ScalarKind.Decimal => "decimal.Parse(value, global::System.Globalization.CultureInfo.InvariantCulture)",
            ScalarKind.Guid => "global::System.Guid.Parse(value)",
            ScalarKind.DateTime => "global::System.Xml.XmlConvert.ToDateTime(value.ToString(), global::System.Xml.XmlDateTimeSerializationMode.RoundtripKind)",
            ScalarKind.DateTimeOffset => "global::System.Xml.XmlConvert.ToDateTimeOffset(value.ToString())",
            ScalarKind.ByteArray => "global::System.Convert.FromBase64String(value.ToString())",
            ScalarKind.Enum => "global::System.Enum.TryParse<" + targetType + ">(value, false, out var parsed) ? parsed : throw new global::System.FormatException(\"Invalid enum value.\")",
            _ => throw new InvalidOperationException()
        };
    }

    private static bool TryGetScalarKind(ITypeSymbol type, out ScalarKind kind)
    {
        var nullable = GetNonNullableType(type);

        if (nullable.TypeKind == TypeKind.Enum)
        {
            kind = ScalarKind.Enum;
            return true;
        }

        if (nullable is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
        {
            kind = ScalarKind.ByteArray;
            return true;
        }

        kind = nullable.SpecialType switch
        {
            SpecialType.System_String => ScalarKind.String,
            SpecialType.System_Boolean => ScalarKind.Boolean,
            SpecialType.System_UInt16 => ScalarKind.UInt16,
            SpecialType.System_Int32 => ScalarKind.Int32,
            SpecialType.System_Int64 => ScalarKind.Int64,
            SpecialType.System_Double => ScalarKind.Double,
            SpecialType.System_Decimal => ScalarKind.Decimal,
            _ when nullable.ToDisplayString() == "System.Guid" => ScalarKind.Guid,
            _ when nullable.ToDisplayString() == "System.DateTime" => ScalarKind.DateTime,
            _ when nullable.ToDisplayString() == "System.DateTimeOffset" => ScalarKind.DateTimeOffset,
            _ => ScalarKind.None
        };
        return kind != ScalarKind.None;
    }

    private static CollectionInfo? GetCollectionInfo(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            if (array.ElementType.SpecialType == SpecialType.System_Byte)
            {
                return null;
            }

            return new CollectionInfo(array.ElementType, CollectionAssignmentKind.Array);
        }

        if (type is not INamedTypeSymbol namedType)
        {
            return null;
        }

        var originalDefinition = namedType.OriginalDefinition.ToDisplayString();
        if (originalDefinition == "System.Collections.Generic.List<T>")
        {
            return new CollectionInfo(namedType.TypeArguments[0], CollectionAssignmentKind.List);
        }

        if (namedType.TypeKind == TypeKind.Interface && IsListAssignableInterface(originalDefinition))
        {
            return new CollectionInfo(namedType.TypeArguments[0], CollectionAssignmentKind.List);
        }

        return null;
    }

    private static bool IsListAssignableInterface(string name)
    {
        return name == "System.Collections.Generic.IEnumerable<T>"
            || name == "System.Collections.Generic.ICollection<T>"
            || name == "System.Collections.Generic.IList<T>"
            || name == "System.Collections.Generic.IReadOnlyCollection<T>"
            || name == "System.Collections.Generic.IReadOnlyList<T>";
    }

    private static string GetDefaultElementName(ITypeSymbol type)
    {
        var nonNullable = GetNonNullableType(type);
        return nonNullable.SpecialType switch
        {
            SpecialType.System_String => "string",
            SpecialType.System_Boolean => "boolean",
            SpecialType.System_UInt16 => "unsignedShort",
            SpecialType.System_Int32 => "int",
            SpecialType.System_Int64 => "long",
            SpecialType.System_Double => "double",
            SpecialType.System_Decimal => "decimal",
            _ => nonNullable.Name
        };
    }

    private static ITypeSymbol GetNonNullableType(ITypeSymbol type)
    {
        return type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && type is INamedTypeSymbol namedNullable
            ? namedNullable.TypeArguments[0]
            : type;
    }

    private static IEnumerable<SerializableMember> GetSerializableMembers(
        SourceProductionContext context,
        INamedTypeSymbol model,
        Dictionary<string, FieldBackedPropertyConfiguration> fieldBackedPropertyConfigurations)
    {
        for (var current = model; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (!property.IsStatic
                    && property.SetMethod is not null
                    && property.SetMethod.DeclaredAccessibility == Accessibility.Public)
                {
                    yield return new SerializableMember(property, property.Type, property.Name, false);
                }
            }

            if (fieldBackedPropertyConfigurations.Count == 0)
            {
                continue;
            }

            foreach (var field in current.GetMembers().OfType<IFieldSymbol>())
            {
                if (!field.Locations.Any(static location => location.IsInSource))
                {
                    continue;
                }

                var markerAttributes = field.GetAttributes()
                    .Where(attribute => attribute.AttributeClass is not null
                        && fieldBackedPropertyConfigurations.ContainsKey(attribute.AttributeClass.ToDisplayString()))
                    .ToArray();
                if (markerAttributes.Length == 0)
                {
                    continue;
                }

                if (field.IsStatic || field.IsConst)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnusableFieldBackedProperty,
                        field.Locations.FirstOrDefault(),
                        field.Name,
                        model.Name,
                        "the backing field must be an instance field"));
                    continue;
                }

                if (markerAttributes.Length != 1)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnusableFieldBackedProperty,
                        field.Locations.FirstOrDefault(),
                        field.Name,
                        model.Name,
                        "the backing field has more than one configured marker attribute"));
                    continue;
                }

                var markerAttribute = markerAttributes[0];
                var configuration = fieldBackedPropertyConfigurations[markerAttribute.AttributeClass!.ToDisplayString()];
                if (!TryGetFieldBackedPropertyName(field, markerAttribute, configuration, out var propertyName, out var error))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnusableFieldBackedProperty,
                        field.Locations.FirstOrDefault(),
                        field.Name,
                        model.Name,
                        error));
                    continue;
                }

                if (!TryGetFieldBackedXmlElementName(markerAttribute, configuration, out var xmlElementNameOverride, out error))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnusableFieldBackedProperty,
                        field.Locations.FirstOrDefault(),
                        field.Name,
                        model.Name,
                        error));
                    continue;
                }

                yield return new SerializableMember(field, field.Type, propertyName, true, xmlElementNameOverride);
            }
        }
    }

    private static bool TryGetFieldBackedPropertyName(
        IFieldSymbol field,
        AttributeData markerAttribute,
        FieldBackedPropertyConfiguration configuration,
        out string propertyName,
        out string error)
    {
        if (configuration.PropertyNameArgument is not null)
        {
            foreach (var namedArgument in markerAttribute.NamedArguments)
            {
                if (namedArgument.Key != configuration.PropertyNameArgument)
                {
                    continue;
                }

                if (namedArgument.Value.Value is not string configuredName
                    || string.IsNullOrWhiteSpace(configuredName))
                {
                    propertyName = string.Empty;
                    error = $"the '{configuration.PropertyNameArgument}' marker argument must be a non-empty string";
                    return false;
                }

                return TryValidateGeneratedPropertyName(configuredName, out propertyName, out error);
            }
        }

        var firstNameCharacter = field.Name.StartsWith("@", StringComparison.Ordinal) ? 1 : 0;
        if (firstNameCharacter == field.Name.Length)
        {
            propertyName = string.Empty;
            error = "the field name must contain a character";
            return false;
        }

        var derivedName = char.ToUpperInvariant(field.Name[firstNameCharacter]) + field.Name.Substring(firstNameCharacter + 1);
        return TryValidateGeneratedPropertyName(derivedName, out propertyName, out error);
    }

    private static bool TryGetFieldBackedXmlElementName(
        AttributeData markerAttribute,
        FieldBackedPropertyConfiguration configuration,
        out string? xmlElementName,
        out string error)
    {
        xmlElementName = null;
        error = string.Empty;
        if (configuration.XmlAttributeStringsArgument is null)
        {
            return true;
        }

        foreach (var namedArgument in markerAttribute.NamedArguments)
        {
            if (namedArgument.Key != configuration.XmlAttributeStringsArgument)
            {
                continue;
            }

            if (namedArgument.Value.Kind != TypedConstantKind.Array)
            {
                if (namedArgument.Value.Value is not string xmlAttributeString)
                {
                    error = $"the '{configuration.XmlAttributeStringsArgument}' marker argument must be a string or string collection";
                    return false;
                }

                if (!TryApplyXmlAttributeString(xmlAttributeString, configuration.XmlAttributeStringsArgument, ref xmlElementName, out error))
                {
                    return false;
                }

                continue;
            }

            if (namedArgument.Value.Values.IsDefault)
            {
                error = $"the '{configuration.XmlAttributeStringsArgument}' marker argument must be a string or string collection";
                return false;
            }

            foreach (var xmlAttributeStringValue in namedArgument.Value.Values)
            {
                if (xmlAttributeStringValue.Value is not string additionalAttributeText
                    || !TryApplyXmlAttributeString(additionalAttributeText, configuration.XmlAttributeStringsArgument, ref xmlElementName, out error))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryApplyXmlAttributeString(
        string value,
        string argumentName,
        ref string? xmlElementName,
        out string error)
    {
        var text = value.Trim();
        if (!text.StartsWith("Xml", StringComparison.Ordinal))
        {
            error = string.Empty;
            return true;
        }

        if (!TryParseXmlElementOverride(text, out var parsedXmlElementName))
        {
            error = $"the '{argumentName}' marker argument supports only XmlElement(\"name\") declarations";
            return false;
        }

        if (xmlElementName is not null)
        {
            error = $"the '{argumentName}' marker argument can specify XmlElement only once";
            return false;
        }

        xmlElementName = parsedXmlElementName;
        error = string.Empty;
        return true;
    }

    private static bool TryParseXmlElementOverride(string text, out string xmlElementName)
    {
        const string prefix = "XmlElement(\"";
        const string suffix = "\")";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)
            || !text.EndsWith(suffix, StringComparison.Ordinal)
            || text.Length <= prefix.Length + suffix.Length)
        {
            xmlElementName = string.Empty;
            return false;
        }

        xmlElementName = text.Substring(prefix.Length, text.Length - prefix.Length - suffix.Length);
        return !string.IsNullOrWhiteSpace(xmlElementName)
            && xmlElementName.IndexOf('"') < 0
            && xmlElementName.IndexOf('\\') < 0;
    }

    private static bool TryValidateGeneratedPropertyName(string name, out string propertyName, out string error)
    {
        if (!SyntaxFacts.IsValidIdentifier(name))
        {
            propertyName = string.Empty;
            error = $"'{name}' is not a valid generated property name";
            return false;
        }

        propertyName = name;
        error = string.Empty;
        return true;
    }

    private static bool IsAttributeType(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Attribute")
            {
                return true;
            }
        }

        return false;
    }

    private static void ReportUnsupportedMember(SourceProductionContext context, SerializableMember member, INamedTypeSymbol model, string reason)
    {
        context.ReportDiagnostic(Diagnostic.Create(
            member.IsFieldBacked ? UnusableFieldBackedProperty : UnsupportedProperty,
            member.AttributeSource.Locations.FirstOrDefault(),
            member.AttributeSource.Name,
            model.Name,
            reason));
    }

    private static bool HasAttribute(ISymbol symbol, string name) => GetAttribute(symbol, name) is not null;

    private static AttributeData? GetAttribute(ISymbol symbol, string name) => symbol.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == name);

    private static string GetXmlName(AttributeData attribute, string fallback)
    {
        if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string name && name.Length > 0)
        {
            return name;
        }

        foreach (var pair in attribute.NamedArguments)
        {
            if (pair.Key == "ElementName" && pair.Value.Value is string elementName && elementName.Length > 0)
            {
                return elementName;
            }

            if (pair.Key == "AttributeName" && pair.Value.Value is string attributeName && attributeName.Length > 0)
            {
                return attributeName;
            }
        }

        return fallback;
    }

    private static string GetXmlElementName(AttributeData? attribute, string fallback, string? xmlElementNameOverride)
    {
        return xmlElementNameOverride ?? (attribute is null ? fallback : GetXmlName(attribute, fallback));
    }

    private static string? GetExplicitXmlRootName(AttributeData? attribute) => GetExplicitXmlName(attribute, "ElementName");

    private static string? GetXmlTypeName(AttributeData? attribute) => GetExplicitXmlName(attribute, "TypeName");

    private static string? GetExplicitXmlName(AttributeData? attribute, string namedArgument)
    {
        if (attribute is null)
        {
            return null;
        }

        if (attribute.ConstructorArguments.Length > 0
            && attribute.ConstructorArguments[0].Value is string name
            && name.Length > 0)
        {
            return name;
        }

        foreach (var pair in attribute.NamedArguments)
        {
            if (pair.Key == namedArgument
                && pair.Value.Value is string namedXmlName
                && namedXmlName.Length > 0)
            {
                return namedXmlName;
            }
        }

        return null;
    }

    private static string FullyQualified(ITypeSymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string GetMemberAccessName(string name)
    {
        return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None
            || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
            ? "@" + name
            : name;
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);

    private sealed class ContextCandidate
    {
        public ContextCandidate(
            INamedTypeSymbol symbol,
            bool isPartial,
            ImmutableArray<AttributeData> serializableAttributes,
            ImmutableArray<AttributeData> fieldBackedPropertyAttributes,
            ImmutableArray<string> invalidFieldMarkerAttributeMetadataNames)
        {
            Symbol = symbol;
            IsPartial = isPartial;
            SerializableAttributes = serializableAttributes;
            FieldBackedPropertyAttributes = fieldBackedPropertyAttributes;
            InvalidFieldMarkerAttributeMetadataNames = invalidFieldMarkerAttributeMetadataNames;
        }

        public INamedTypeSymbol Symbol { get; }

        public bool IsPartial { get; }

        public ImmutableArray<AttributeData> SerializableAttributes { get; }

        public ImmutableArray<AttributeData> FieldBackedPropertyAttributes { get; }

        public ImmutableArray<string> InvalidFieldMarkerAttributeMetadataNames { get; }
    }

    private sealed class ModelInfo
    {
        public ModelInfo(INamedTypeSymbol symbol, string rootName, List<MemberInfo> members, string? unknownPropertyName, ImmutableArray<string> skipElements)
        {
            Symbol = symbol;
            RootName = rootName;
            Members = members;
            UnknownPropertyName = unknownPropertyName;
            SkipElements = skipElements.IsDefault ? ImmutableArray<string>.Empty : skipElements;
            Identifier = string.Empty;
        }

        public INamedTypeSymbol Symbol { get; }

        public string RootName { get; }

        public List<MemberInfo> Members { get; }

        public string? UnknownPropertyName { get; }

        public ImmutableArray<string> SkipElements { get; }

        public int Index { get; set; }

        public string Identifier { get; set; }
    }

    private sealed class MemberInfo
    {
        public MemberInfo(
            string propertyName,
            string? attributeName,
            string? elementName,
            MemberKind kind,
            ScalarKind scalarKind,
            ITypeSymbol valueType,
            INamedTypeSymbol? modelType,
            CollectionStyle collectionStyle,
            string? itemElementName,
            CollectionAssignmentKind collectionAssignmentKind)
        {
            PropertyName = propertyName;
            AttributeName = attributeName;
            ElementName = elementName;
            Kind = kind;
            ScalarKind = scalarKind;
            ValueType = valueType;
            ModelType = modelType;
            CollectionStyle = collectionStyle;
            ItemElementName = itemElementName;
            CollectionAssignmentKind = collectionAssignmentKind;
        }

        public string PropertyName { get; }

        public string? AttributeName { get; }

        public string? ElementName { get; }

        public MemberKind Kind { get; }

        public ScalarKind ScalarKind { get; }

        public ITypeSymbol ValueType { get; }

        public INamedTypeSymbol? ModelType { get; }

        public CollectionStyle CollectionStyle { get; }

        public string? ItemElementName { get; }

        public CollectionAssignmentKind CollectionAssignmentKind { get; }

        public bool IsCollection => Kind == MemberKind.ScalarCollection || Kind == MemberKind.ModelCollection;
    }

    private sealed class SerializableMember
    {
        public SerializableMember(
            ISymbol attributeSource,
            ITypeSymbol type,
            string propertyName,
            bool isFieldBacked,
            string? xmlElementNameOverride = null)
        {
            AttributeSource = attributeSource;
            Type = type;
            PropertyName = propertyName;
            IsFieldBacked = isFieldBacked;
            XmlElementNameOverride = xmlElementNameOverride;
        }

        public ISymbol AttributeSource { get; }

        public ITypeSymbol Type { get; }

        public string PropertyName { get; }

        public bool IsFieldBacked { get; }

        public string? XmlElementNameOverride { get; }
    }

    private sealed class FieldBackedPropertyConfiguration
    {
        public FieldBackedPropertyConfiguration(string? propertyNameArgument, string? xmlAttributeStringsArgument)
        {
            PropertyNameArgument = propertyNameArgument;
            XmlAttributeStringsArgument = xmlAttributeStringsArgument;
        }

        public string? PropertyNameArgument { get; }

        public string? XmlAttributeStringsArgument { get; }
    }

    private sealed class CollectionInfo
    {
        public CollectionInfo(ITypeSymbol elementType, CollectionAssignmentKind assignmentKind)
        {
            ElementType = elementType;
            AssignmentKind = assignmentKind;
        }

        public ITypeSymbol ElementType { get; }

        public CollectionAssignmentKind AssignmentKind { get; }
    }

    private enum MemberKind
    {
        Scalar,
        Model,
        ScalarCollection,
        ModelCollection
    }

    private enum CollectionStyle
    {
        None,
        Wrapped,
        Flat
    }

    private enum CollectionAssignmentKind
    {
        None,
        Array,
        List
    }

    private enum ScalarKind
    {
        None,
        String,
        Boolean,
        UInt16,
        Int32,
        Int64,
        Double,
        Decimal,
        Guid,
        DateTime,
        DateTimeOffset,
        ByteArray,
        Enum
    }
}
