﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Composition
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Reflection;
    using MessagePack;
    using MessagePack.Formatters;
    using Microsoft.VisualStudio.Composition.Formatter;
    using Microsoft.VisualStudio.Composition.Reflection;

    public class RuntimeComposition : IEquatable<RuntimeComposition>
    {
        private readonly ImmutableHashSet<RuntimePart> parts;
        private readonly IReadOnlyDictionary<TypeRef, RuntimePart> partsByType;
        private readonly IReadOnlyDictionary<string, IReadOnlyCollection<RuntimeExport>> exportsByContractName;
        private readonly IReadOnlyDictionary<TypeRef, RuntimeExport> metadataViewsAndProviders;

        private RuntimeComposition(IEnumerable<RuntimePart> parts, IReadOnlyDictionary<TypeRef, RuntimeExport> metadataViewsAndProviders, Resolver resolver)
        {
            Requires.NotNull(parts, nameof(parts));
            Requires.NotNull(metadataViewsAndProviders, nameof(metadataViewsAndProviders));
            Requires.NotNull(resolver, nameof(resolver));

            var invalidArgExceptionMsg = "Invalid arguments passed to generate runtime composition.";
            var invalidArgExceptionData = "InvalidCompositionException";
            if (!parts.Any())
            {
                var exception = new ArgumentException(invalidArgExceptionMsg, nameof(parts));
                exception.Data.Add(invalidArgExceptionData, true);

                throw exception;
            }

            if (!metadataViewsAndProviders.Any())
            {
                var exception = new ArgumentException(invalidArgExceptionMsg, nameof(metadataViewsAndProviders));
                exception.Data.Add(invalidArgExceptionData, true);

                throw exception;
            }

            this.parts = ImmutableHashSet.CreateRange(parts);
            this.metadataViewsAndProviders = metadataViewsAndProviders;
            this.Resolver = resolver;

            this.partsByType = this.parts.ToDictionary(p => p.TypeRef, this.parts.Count);

            var exports =
                from part in this.parts
                from export in part.Exports
                group export by export.ContractName into exportsByContract
                select exportsByContract;
            this.exportsByContractName = exports.ToDictionary(
                e => e.Key,
                e => (IReadOnlyCollection<RuntimeExport>)e.ToImmutableArray());
        }

        public IReadOnlyCollection<RuntimePart> Parts
        {
            get { return this.parts; }
        }

        public IReadOnlyDictionary<TypeRef, RuntimeExport> MetadataViewsAndProviders
        {
            get { return this.metadataViewsAndProviders; }
        }

        internal Resolver Resolver { get; }

        public static RuntimeComposition CreateRuntimeComposition(CompositionConfiguration configuration)
        {
            Requires.NotNull(configuration, nameof(configuration));

            // PERF/memory tip: We could create all RuntimeExports first, and then reuse them at each import site.
            var parts = configuration.Parts.Select(part => CreateRuntimePart(part, configuration));
            var metadataViewsAndProviders = ImmutableDictionary.CreateRange(
                from viewAndProvider in configuration.MetadataViewsAndProviders
                let viewTypeRef = TypeRef.Get(viewAndProvider.Key, configuration.Resolver)
                let runtimeExport = CreateRuntimeExport(viewAndProvider.Value, configuration.Resolver)
                select new KeyValuePair<TypeRef, RuntimeExport>(viewTypeRef, runtimeExport));
            return new RuntimeComposition(parts, metadataViewsAndProviders, configuration.Resolver);
        }

        public static RuntimeComposition CreateRuntimeComposition(IEnumerable<RuntimePart> parts, IReadOnlyDictionary<TypeRef, RuntimeExport> metadataViewsAndProviders, Resolver resolver)
        {
            return new RuntimeComposition(parts, metadataViewsAndProviders, resolver);
        }

        public IExportProviderFactory CreateExportProviderFactory()
        {
            return new RuntimeExportProviderFactory(this);
        }

        public IReadOnlyCollection<RuntimeExport> GetExports(string contractName)
        {
            IReadOnlyCollection<RuntimeExport>? exports;
            if (this.exportsByContractName.TryGetValue(contractName, out exports))
            {
                return exports;
            }

            return ImmutableList<RuntimeExport>.Empty;
        }

        public RuntimePart GetPart(RuntimeExport export)
        {
            Requires.NotNull(export, nameof(export));

            return this.partsByType[export.DeclaringTypeRef];
        }

        public RuntimePart GetPart(TypeRef partType)
        {
            Requires.NotNull(partType, nameof(partType));

            return this.partsByType[partType];
        }

        public override bool Equals(object? obj)
        {
            return this.Equals(obj as RuntimeComposition);
        }

        public override int GetHashCode()
        {
            int hashCode = this.parts.Count;
            foreach (var part in this.parts)
            {
                hashCode += part.GetHashCode();
            }

            return hashCode;
        }

        public bool Equals(RuntimeComposition? other)
        {
            if (other == null)
            {
                return false;
            }

            return this.parts.SetEquals(other.parts)
                && ByValueEquality.Dictionary<TypeRef, RuntimeExport>().Equals(this.metadataViewsAndProviders, other.metadataViewsAndProviders);
        }

        internal static string GetDiagnosticLocation(RuntimeImport import)
        {
            Requires.NotNull(import, nameof(import));

            return string.Format(
                CultureInfo.CurrentCulture,
                "{0}.{1}",
                import.DeclaringTypeRef?.Resolve().FullName,
                import.ImportingMember == null ? ("ctor(" + import.ImportingParameter!.Name + ")") : import.ImportingMember.Name);
        }

        internal static string GetDiagnosticLocation(RuntimeExport export)
        {
            Requires.NotNull(export, nameof(export));

            if (export.Member != null)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}.{1}",
                    export.DeclaringTypeRef.Resolve().FullName,
                    export.Member.Name);
            }
            else
            {
                return export.DeclaringTypeRef.Resolve().FullName ?? "<no name>";
            }
        }

        private static RuntimePart CreateRuntimePart(ComposedPart part, CompositionConfiguration configuration)
        {
            Requires.NotNull(part, nameof(part));

            var runtimePart = new RuntimePart(
                part.Definition.TypeRef,
                part.Definition.ImportingConstructorOrFactoryRef,
                part.GetImportingConstructorImports().Select(kvp => CreateRuntimeImport(kvp.Key, kvp.Value, part.Resolver)).ToImmutableArray(),
                part.Definition.ImportingMembers.Select(idb => CreateRuntimeImport(idb, part.SatisfyingExports[idb], part.Resolver)).ToImmutableArray(),
                part.Definition.ExportDefinitions.Select(ed => CreateRuntimeExport(ed.Value, part.Definition.TypeRef, ed.Key, part.Resolver)).ToImmutableArray(),
                part.Definition.OnImportsSatisfiedMethodRefs,
                part.Definition.IsShared ? configuration.GetEffectiveSharingBoundary(part.Definition) : null);
            return runtimePart;
        }

        private static RuntimeImport CreateRuntimeImport(ImportDefinitionBinding importDefinitionBinding, IReadOnlyList<ExportDefinitionBinding> satisfyingExports, Resolver resolver)
        {
            Requires.NotNull(importDefinitionBinding, nameof(importDefinitionBinding));
            Requires.NotNull(satisfyingExports, nameof(satisfyingExports));

            var runtimeExports = satisfyingExports.Select(export => CreateRuntimeExport(export, resolver)).ToImmutableArray();
            if (importDefinitionBinding.ImportingMemberRef != null)
            {
                return new RuntimeImport(
                    importDefinitionBinding.ImportingMemberRef,
                    importDefinitionBinding.ImportingSiteTypeRef,
                    importDefinitionBinding.ImportingSiteTypeWithoutCollectionRef,
                    importDefinitionBinding.ImportDefinition.Cardinality,
                    runtimeExports,
                    PartCreationPolicyConstraint.IsNonSharedInstanceRequired(importDefinitionBinding.ImportDefinition),
                    importDefinitionBinding.IsExportFactory,
                    importDefinitionBinding.ImportDefinition.Metadata,
                    importDefinitionBinding.ImportDefinition.ExportFactorySharingBoundaries);
            }
            else
            {
                return new RuntimeImport(
                    importDefinitionBinding.ImportingParameterRef!,
                    importDefinitionBinding.ImportingSiteTypeRef,
                    importDefinitionBinding.ImportingSiteTypeWithoutCollectionRef,
                    importDefinitionBinding.ImportDefinition.Cardinality,
                    runtimeExports,
                    PartCreationPolicyConstraint.IsNonSharedInstanceRequired(importDefinitionBinding.ImportDefinition),
                    importDefinitionBinding.IsExportFactory,
                    importDefinitionBinding.ImportDefinition.Metadata,
                    importDefinitionBinding.ImportDefinition.ExportFactorySharingBoundaries);
            }
        }

        private static RuntimeExport CreateRuntimeExport(ExportDefinition exportDefinition, TypeRef partTypeRef, MemberRef? exportingMemberRef, Resolver resolver)
        {
            Requires.NotNull(exportDefinition, nameof(exportDefinition));

            return new RuntimeExport(
                exportDefinition.ContractName,
                partTypeRef,
                exportingMemberRef,
                exportDefinition.Metadata);
        }

        private static RuntimeExport CreateRuntimeExport(ExportDefinitionBinding exportDefinitionBinding, Resolver resolver)
        {
            Requires.NotNull(exportDefinitionBinding, nameof(exportDefinitionBinding));
            Requires.NotNull(resolver, nameof(resolver));

            var partDefinitionTypeRef = exportDefinitionBinding.PartDefinition.TypeRef;
            return CreateRuntimeExport(
                exportDefinitionBinding.ExportDefinition,
                partDefinitionTypeRef,
                exportDefinitionBinding.ExportingMemberRef,
                resolver);
        }

        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        [MessagePackFormatter(typeof(RuntimePartFormatter))]
        public class RuntimePart : IEquatable<RuntimePart>
        {
            public RuntimePart(
                TypeRef type,
                MethodRef? importingConstructor,
                IReadOnlyList<RuntimeImport> importingConstructorArguments,
                IReadOnlyList<RuntimeImport> importingMembers,
                IReadOnlyList<RuntimeExport> exports,
                IReadOnlyList<MethodRef> onImportsSatisfiedMethods,
                string? sharingBoundary)
            {
                this.TypeRef = type;
                this.ImportingConstructorOrFactoryMethodRef = importingConstructor;
                this.ImportingConstructorArguments = importingConstructorArguments;
                this.ImportingMembers = importingMembers;
                this.Exports = exports;
                this.OnImportsSatisfiedMethodRefs = onImportsSatisfiedMethods;
                this.SharingBoundary = sharingBoundary;
            }

            public TypeRef TypeRef { get; private set; }

            public MethodRef? ImportingConstructorOrFactoryMethodRef { get; private set; }

            public MethodBase? ImportingConstructorOrFactoryMethod => this.ImportingConstructorOrFactoryMethodRef?.MethodBase;

            public IReadOnlyList<RuntimeImport> ImportingConstructorArguments { get; private set; }

            public IReadOnlyList<RuntimeImport> ImportingMembers { get; private set; }

            public IReadOnlyList<RuntimeExport> Exports { get; set; }

            public string? SharingBoundary { get; private set; }

            public bool IsShared => this.SharingBoundary != null;

            public bool IsInstantiable => this.ImportingConstructorOrFactoryMethodRef != null;

            public IReadOnlyList<MethodRef> OnImportsSatisfiedMethodRefs { get; }

            private string? DebuggerDisplay => this.TypeRef.FullName;

            public override bool Equals(object? obj) => this.Equals(obj as RuntimePart);

            public override int GetHashCode() => this.TypeRef.GetHashCode();

            public bool Equals(RuntimePart? other)
            {
                if (other == null)
                {
                    return false;
                }

                bool result = EqualityComparer<TypeRef>.Default.Equals(this.TypeRef, other.TypeRef)
                    && EqualityComparer<MethodRef?>.Default.Equals(this.ImportingConstructorOrFactoryMethodRef, other.ImportingConstructorOrFactoryMethodRef)
                    && this.ImportingConstructorArguments.SequenceEqual(other.ImportingConstructorArguments)
                    && ByValueEquality.EquivalentIgnoreOrder<RuntimeImport>().Equals(this.ImportingMembers, other.ImportingMembers)
                    && ByValueEquality.EquivalentIgnoreOrder<RuntimeExport>().Equals(this.Exports, other.Exports)
                    && this.OnImportsSatisfiedMethodRefs.SequenceEqual(other.OnImportsSatisfiedMethodRefs, EqualityComparer<MethodRef?>.Default)
                    && this.SharingBoundary == other.SharingBoundary;
                return result;
            }
        }

        [DebuggerDisplay("{" + nameof(ImportingSiteElementType) + "}")]
        public class RuntimeImport : IEquatable<RuntimeImport>
        {
            private NullableBool isLazy;
            private Type? importingSiteElementType;
            private Func<AssemblyName, Func<object?>, object, object>? lazyFactory;
            private ParameterInfo? importingParameter;
            private MemberInfo? importingMember;
            private volatile bool isMetadataTypeInitialized;
            private Type? metadataType;

            private RuntimeImport(TypeRef importingSiteTypeRef, TypeRef importingSiteTypeWithoutCollectionRef, ImportCardinality cardinality, IReadOnlyList<RuntimeExport> satisfyingExports, bool isNonSharedInstanceRequired, bool isExportFactory, IReadOnlyDictionary<string, object?> metadata, IReadOnlyCollection<string> exportFactorySharingBoundaries)
            {
                Requires.NotNull(importingSiteTypeRef, nameof(importingSiteTypeRef));
                Requires.NotNull(importingSiteTypeWithoutCollectionRef, nameof(importingSiteTypeWithoutCollectionRef));
                Requires.NotNull(satisfyingExports, nameof(satisfyingExports));

                this.Cardinality = cardinality;
                this.SatisfyingExports = satisfyingExports;
                this.IsNonSharedInstanceRequired = isNonSharedInstanceRequired;
                this.IsExportFactory = isExportFactory;
                this.Metadata = metadata;
                this.ImportingSiteTypeRef = importingSiteTypeRef;
                this.ImportingSiteTypeWithoutCollectionRef = importingSiteTypeWithoutCollectionRef;
                this.ExportFactorySharingBoundaries = exportFactorySharingBoundaries;
            }

            public RuntimeImport(MemberRef? importingMemberRef, TypeRef importingSiteTypeRef, TypeRef importingSiteTypeWithoutCollectionRef, ImportCardinality cardinality, IReadOnlyList<RuntimeExport> satisfyingExports, bool isNonSharedInstanceRequired, bool isExportFactory, IReadOnlyDictionary<string, object?> metadata, IReadOnlyCollection<string> exportFactorySharingBoundaries)
                : this(importingSiteTypeRef, importingSiteTypeWithoutCollectionRef, cardinality, satisfyingExports, isNonSharedInstanceRequired, isExportFactory, metadata, exportFactorySharingBoundaries)
            {
                this.ImportingMemberRef = importingMemberRef;
            }

            public RuntimeImport(ParameterRef importingParameterRef, TypeRef importingSiteTypeRef, TypeRef importingSiteTypeWithoutCollectionRef, ImportCardinality cardinality, IReadOnlyList<RuntimeExport> satisfyingExports, bool isNonSharedInstanceRequired, bool isExportFactory, IReadOnlyDictionary<string, object?> metadata, IReadOnlyCollection<string> exportFactorySharingBoundaries)
                : this(importingSiteTypeRef, importingSiteTypeWithoutCollectionRef, cardinality, satisfyingExports, isNonSharedInstanceRequired, isExportFactory, metadata, exportFactorySharingBoundaries)
            {
                Requires.NotNull(importingParameterRef, nameof(importingParameterRef));
                this.ImportingParameterRef = importingParameterRef;
            }

            /// <summary>
            /// Gets the importing member. May be empty if the import site is an importing constructor parameter.
            /// </summary>
            public MemberRef? ImportingMemberRef { get; private set; }

            /// <summary>
            /// Gets the importing parameter. May be empty if the import site is an importing field or property.
            /// </summary>
            public ParameterRef? ImportingParameterRef { get; private set; }

            public TypeRef ImportingSiteTypeRef { get; private set; }

            public ImportCardinality Cardinality { get; private set; }

            public IReadOnlyCollection<RuntimeExport> SatisfyingExports { get; private set; }

            public bool IsExportFactory { get; private set; }

            public bool IsNonSharedInstanceRequired { get; private set; }

            public IReadOnlyDictionary<string, object?> Metadata { get; private set; }

            public Type? ExportFactory
            {
                get
                {
                    return this.IsExportFactory
                        ? this.ImportingSiteTypeWithoutCollection
                        : null;
                }
            }

            /// <summary>
            /// Gets the sharing boundaries created when the export factory is used.
            /// </summary>
            public IReadOnlyCollection<string> ExportFactorySharingBoundaries { get; private set; }

            public MemberInfo? ImportingMember => this.importingMember ?? (this.importingMember = this.ImportingMemberRef?.MemberInfo);

            public ParameterInfo? ImportingParameter => this.importingParameter ?? (this.importingParameter = this.ImportingParameterRef?.ParameterInfo);

            public bool IsLazy
            {
                get
                {
                    if (!this.isLazy.HasValue)
                    {
                        this.isLazy = this.ImportingSiteTypeWithoutCollectionRef.IsAnyLazyType();
                    }

                    return this.isLazy.Value;
                }
            }

            public Type ImportingSiteType => this.ImportingSiteTypeRef.ResolvedType;

            public Type ImportingSiteTypeWithoutCollection => this.ImportingSiteTypeWithoutCollectionRef.ResolvedType;

            public TypeRef ImportingSiteTypeWithoutCollectionRef { get; private set; }

            /// <summary>
            /// Gets the type of the member, with the ImportMany collection and Lazy/ExportFactory stripped off, when present.
            /// </summary>
            public Type ImportingSiteElementType
            {
                get
                {
                    if (this.importingSiteElementType == null)
                    {
                        this.importingSiteElementType = PartDiscovery.GetTypeIdentityFromImportingType(this.ImportingSiteType, this.Cardinality == ImportCardinality.ZeroOrMore);
                    }

                    return this.importingSiteElementType;
                }
            }

            public Type? MetadataType
            {
                get
                {
                    if (!this.isMetadataTypeInitialized)
                    {
                        this.metadataType = this.IsLazy && this.ImportingSiteTypeWithoutCollection.GenericTypeArguments.Length == 2
                            ? this.ImportingSiteTypeWithoutCollection.GenericTypeArguments[1]
                            : null;
                        this.isMetadataTypeInitialized = true;
                    }

                    return this.metadataType;
                }
            }

            public TypeRef DeclaringTypeRef
            {
                get
                {
                    return this.ImportingMemberRef?.DeclaringType ?? this.ImportingParameterRef?.DeclaringType ?? throw Assumes.NotReachable();
                }
            }

            internal Func<AssemblyName, Func<object?>, object, object>? LazyFactory
            {
                get
                {
                    if (this.lazyFactory == null && this.IsLazy)
                    {
                        Type[] lazyTypeArgs = this.ImportingSiteTypeWithoutCollection.GenericTypeArguments;
                        this.lazyFactory = LazyServices.CreateStronglyTypedLazyFactory(this.ImportingSiteElementType, lazyTypeArgs.Length > 1 ? lazyTypeArgs[1] : null);
                    }

                    return this.lazyFactory;
                }
            }

            public override int GetHashCode() => this.ImportingMemberRef?.GetHashCode() ?? 0;

            public override bool Equals(object? obj) => this.Equals(obj as RuntimeImport);

            public bool Equals(RuntimeImport? other)
            {
                if (other == null)
                {
                    return false;
                }

                bool result = EqualityComparer<TypeRef>.Default.Equals(this.ImportingSiteTypeRef, other.ImportingSiteTypeRef)
                    && this.Cardinality == other.Cardinality
                    && ByValueEquality.EquivalentIgnoreOrder<RuntimeExport>().Equals(this.SatisfyingExports, other.SatisfyingExports)
                    && this.IsNonSharedInstanceRequired == other.IsNonSharedInstanceRequired
                    && ByValueEquality.Metadata.Equals(this.Metadata, other.Metadata)
                    && ByValueEquality.EquivalentIgnoreOrder<string>().Equals(this.ExportFactorySharingBoundaries, other.ExportFactorySharingBoundaries)
                    && EqualityComparer<MemberRef?>.Default.Equals(this.ImportingMemberRef, other.ImportingMemberRef)
                    && EqualityComparer<ParameterRef?>.Default.Equals(this.ImportingParameterRef, other.ImportingParameterRef);
                return result;
            }
        }

        [MessagePackObject]
        public class RuntimeExport : IEquatable<RuntimeExport>
        {
            [IgnoreMember]
            private MemberInfo? member;

            [IgnoreMember]
            private TypeRef? exportedValueTypeRef;

            public RuntimeExport(string contractName, TypeRef declaringTypeRef, MemberRef? memberRef, IReadOnlyDictionary<string, object?> metadata)
            {
                Requires.NotNullOrEmpty(contractName, nameof(contractName));
                Requires.NotNull(declaringTypeRef, nameof(declaringTypeRef));
                Requires.NotNull(metadata, nameof(metadata));

                this.ContractName = contractName;
                this.DeclaringTypeRef = declaringTypeRef;
                this.MemberRef = memberRef;
                this.Metadata = metadata;
            }

            [SerializationConstructor]
            public RuntimeExport(string contractName, TypeRef declaringTypeRef, MemberRef? memberRef, TypeRef? exportedValueTypeRef, IReadOnlyDictionary<string, object?> metadata)
            {
                Requires.NotNullOrEmpty(contractName, nameof(contractName));
                Requires.NotNull(declaringTypeRef, nameof(declaringTypeRef));
                Requires.NotNull(metadata, nameof(metadata));

                this.ContractName = contractName;
                this.DeclaringTypeRef = declaringTypeRef;
                this.MemberRef = memberRef;
                this.exportedValueTypeRef = exportedValueTypeRef;
                this.Metadata = metadata;
            }

            [Key(0)]
            public string ContractName { get; private set; }

            [Key(1)]
            public TypeRef DeclaringTypeRef { get; private set; }

            [Key(2)]
            public MemberRef? MemberRef { get; private set; }

            [Key(3)]
            public TypeRef ExportedValueTypeRef
            {
                get
                {
                    if (this.MemberRef == null)
                    {
                        return this.DeclaringTypeRef;
                    }

                    if (this.exportedValueTypeRef == null)
                    {
                        this.exportedValueTypeRef = ReflectionHelpers.GetExportedValueTypeRef(this.DeclaringTypeRef, this.MemberRef);
                    }

                    return this.exportedValueTypeRef;
                }
            }

            [IgnoreMember]
            public Type ExportedValueType
            {
                get { return this.ExportedValueTypeRef.ResolvedType; }
            }

            [Key(4)]
            public IReadOnlyDictionary<string, object?> Metadata { get; private set; }

            [IgnoreMember]
            public MemberInfo? Member => this.member ?? (this.member = this.MemberRef?.MemberInfo);

            public override int GetHashCode() => this.ContractName.GetHashCode() + this.DeclaringTypeRef.GetHashCode();

            public override bool Equals(object? obj) => this.Equals(obj as RuntimeExport);

            public bool Equals(RuntimeExport? other)
            {
                if (other is null)
                {
                    return false;
                }

                bool result = this.ContractName == other.ContractName
                    && EqualityComparer<TypeRef>.Default.Equals(this.DeclaringTypeRef, other.DeclaringTypeRef)
                    && EqualityComparer<MemberRef?>.Default.Equals(this.MemberRef, other.MemberRef)
                    && EqualityComparer<TypeRef>.Default.Equals(this.ExportedValueTypeRef, other.ExportedValueTypeRef)
                    && ByValueEquality.Metadata.Equals(this.Metadata, other.Metadata);
                return result;
            }
        }

        internal class RuntimeCompositionFormatter(Resolver compositionResolver) : IMessagePackFormatter<RuntimeComposition?>
        {
            /// <inheritdoc/>
            public RuntimeComposition? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                if (reader.TryReadNil())
                {
                    return null;
                }

                try
                {
                    options.Security.DepthStep(ref reader);
                    var actualCount = reader.ReadArrayHeader();
                    if (actualCount != 2)
                    {
                        throw new MessagePackSerializationException($"Invalid array count for type {nameof(RuntimeComposition)}. Expected: {2}, Actual: {actualCount}");
                    }

                    IReadOnlyCollection<RuntimePart> parts = options.Resolver.GetFormatterWithVerify<IReadOnlyCollection<RuntimePart>>().Deserialize(ref reader, options);

                    IReadOnlyDictionary<TypeRef, RuntimeExport> metadataViewsAndProviders = options.Resolver.GetFormatterWithVerify<IReadOnlyDictionary<TypeRef, RuntimeExport>>().Deserialize(ref reader, options);

                    return RuntimeComposition.CreateRuntimeComposition(parts, metadataViewsAndProviders, compositionResolver);
                }
                finally
                {
                    reader.Depth--;
                }
            }

            /// <inheritdoc/>
            public void Serialize(ref MessagePackWriter writer, RuntimeComposition? value, MessagePackSerializerOptions options)
            {
                if (value is null)
                {
                    writer.WriteNil();
                    return;
                }

                writer.WriteArrayHeader(2);

                options.Resolver.GetFormatterWithVerify<IReadOnlyCollection<RuntimePart>>().Serialize(ref writer, value.Parts, options);
                options.Resolver.GetFormatterWithVerify<IReadOnlyDictionary<TypeRef, RuntimeExport>>().Serialize(ref writer, value.MetadataViewsAndProviders, options);
            }
        }

        internal class RuntimeImportFormatter(Resolver compositionResolver) : IMessagePackFormatter<RuntimeImport?>
        {
            private readonly MetadataDictionaryFormatter metadataDictionaryFormatter = new(compositionResolver);

            private enum RuntimeImportFlags : byte
            {
                None = 0x00,
                IsNonSharedInstanceRequired = 0x01,
                IsExportFactory = 0x02,
                CardinalityExactlyOne = 0x04,
                CardinalityOneOrZero = 0x08,
                IsParameter = 0x10,
            }

            /// <inheritdoc/>
            public RuntimeImport? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                if (reader.TryReadNil())
                {
                    return null;
                }

                try
                {
                    var actualCount = reader.ReadArrayHeader();
                    if (actualCount != 7)
                    {
                        throw new MessagePackSerializationException($"Invalid array count for type {nameof(RuntimeImport)}. Expected: {7}, Actual: {actualCount}");
                    }

                    options.Security.DepthStep(ref reader);

                    var flags = (RuntimeImportFlags)reader.ReadByte();
                    ImportCardinality cardinality =
                      (flags & RuntimeImportFlags.CardinalityOneOrZero) == RuntimeImportFlags.CardinalityOneOrZero ? ImportCardinality.OneOrZero :
                      (flags & RuntimeImportFlags.CardinalityExactlyOne) == RuntimeImportFlags.CardinalityExactlyOne ? ImportCardinality.ExactlyOne :
                      ImportCardinality.ZeroOrMore;
                    bool isExportFactory = (flags & RuntimeImportFlags.IsExportFactory) == RuntimeImportFlags.IsExportFactory;

                    var importingMember = default(MemberRef);
                    var importingParameter = default(ParameterRef);
                    if ((flags & RuntimeImportFlags.IsParameter) == RuntimeImportFlags.IsParameter)
                    {
                        importingParameter = options.Resolver.GetFormatterWithVerify<ParameterRef?>().Deserialize(ref reader, options);
                    }
                    else
                    {
                        importingMember = options.Resolver.GetFormatterWithVerify<MemberRef?>().Deserialize(ref reader, options);
                    }

                    IMessagePackFormatter<TypeRef> typeRefFormatter = options.Resolver.GetFormatterWithVerify<TypeRef>();
                    TypeRef importingSiteTypeRef = typeRefFormatter.Deserialize(ref reader, options);

                    TypeRef importingSiteTypeWithoutCollectionRef;
                    if (cardinality == ImportCardinality.ZeroOrMore)
                    {
                        importingSiteTypeWithoutCollectionRef = typeRefFormatter.Deserialize(ref reader, options);
                    }
                    else
                    {
                        reader.Skip();
                        importingSiteTypeWithoutCollectionRef = importingSiteTypeRef;
                    }

                    IReadOnlyList<RuntimeExport> satisfyingExports = options.Resolver.GetFormatterWithVerify<IReadOnlyList<RuntimeExport>>().Deserialize(ref reader, options);
                    IReadOnlyDictionary<string, object?> metadata = this.metadataDictionaryFormatter.Deserialize(ref reader, options);
                    IReadOnlyCollection<string> exportFactorySharingBoundaries;
                    if (isExportFactory)
                    {
                        exportFactorySharingBoundaries = options.Resolver.GetFormatterWithVerify<IReadOnlyCollection<string>>().Deserialize(ref reader, options);
                    }
                    else
                    {
                        reader.Skip();
                        exportFactorySharingBoundaries = ImmutableList<string>.Empty;
                    }

                    return importingMember is null
                                ? new RuntimeImport(
                                    importingParameterRef: importingParameter ?? throw new MessagePackSerializationException($"Unexpected null for the type {nameof(ParameterRef)}"), importingSiteTypeRef, importingSiteTypeWithoutCollectionRef, cardinality, satisfyingExports: satisfyingExports.ToList(), (flags & RuntimeImportFlags.IsNonSharedInstanceRequired) == RuntimeImportFlags.IsNonSharedInstanceRequired, isExportFactory, metadata, exportFactorySharingBoundaries)
                                : new RuntimeImport(importingMember, importingSiteTypeRef, importingSiteTypeWithoutCollectionRef, cardinality, satisfyingExports.ToList(), (flags & RuntimeImportFlags.IsNonSharedInstanceRequired) == RuntimeImportFlags.IsNonSharedInstanceRequired, isExportFactory, metadata, exportFactorySharingBoundaries);
                }
                finally
                {
                    reader.Depth--;
                }
            }

            /// <inheritdoc/>
            public void Serialize(ref MessagePackWriter writer, RuntimeImport? value, MessagePackSerializerOptions options)
            {
                if (value is null)
                {
                    writer.WriteNil();
                    return;
                }

                writer.WriteArrayHeader(7);

                RuntimeImportFlags flags = RuntimeImportFlags.None;
                flags |= value.ImportingMemberRef == null ? RuntimeImportFlags.IsParameter : 0;
                flags |= value.IsNonSharedInstanceRequired ? RuntimeImportFlags.IsNonSharedInstanceRequired : 0;
                flags |= value.IsExportFactory ? RuntimeImportFlags.IsExportFactory : 0;
                flags |=
                    value.Cardinality == ImportCardinality.ExactlyOne ? RuntimeImportFlags.CardinalityExactlyOne :
                    value.Cardinality == ImportCardinality.OneOrZero ? RuntimeImportFlags.CardinalityOneOrZero : 0;

                writer.Write((byte)flags);

                if (value.ImportingMemberRef is null)
                {
                    options.Resolver.GetFormatterWithVerify<ParameterRef?>().Serialize(ref writer, value.ImportingParameterRef, options);
                }
                else
                {
                    options.Resolver.GetFormatterWithVerify<MemberRef?>().Serialize(ref writer, value.ImportingMemberRef, options);
                }

                IMessagePackFormatter<TypeRef> typeRefFormatter = options.Resolver.GetFormatterWithVerify<TypeRef>();
                typeRefFormatter.Serialize(ref writer, value.ImportingSiteTypeRef, options);

                if (value.Cardinality == ImportCardinality.ZeroOrMore)
                {
                    typeRefFormatter.Serialize(ref writer, value.ImportingSiteTypeWithoutCollectionRef, options);
                }
                else
                {
                    if (value.ImportingSiteTypeWithoutCollectionRef != value.ImportingSiteTypeRef)
                    {
                        throw new ArgumentException($"{nameof(value.ImportingSiteTypeWithoutCollectionRef)} and {nameof(value.ImportingSiteTypeRef)} must be equal when {nameof(value.Cardinality)} is not {nameof(ImportCardinality.ZeroOrMore)}.", nameof(value));
                    }
                    else
                    {
                        writer.WriteNil();
                    }
                }

                options.Resolver.GetFormatterWithVerify<IReadOnlyCollection<RuntimeExport>>().Serialize(ref writer, value.SatisfyingExports, options);

                this.metadataDictionaryFormatter.Serialize(ref writer, value.Metadata, options);
                if (value.IsExportFactory)
                {
                    options.Resolver.GetFormatterWithVerify<IReadOnlyCollection<string>>().Serialize(ref writer, value.ExportFactorySharingBoundaries, options);
                }
                else
                {
                    writer.WriteNil();
                }
            }
        }

        private class RuntimePartFormatter : IMessagePackFormatter<RuntimePart?>
        {
            public static readonly RuntimePartFormatter Instance = new();

            private RuntimePartFormatter()
            {
            }

            /// <inheritdoc/>
            public RuntimePart? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                if (reader.TryReadNil())
                {
                    return null;
                }

                options.Security.DepthStep(ref reader);
                try
                {
                    int actualCount = reader.ReadArrayHeader();
                    if (actualCount != 7)
                    {
                        throw new MessagePackSerializationException($"Invalid array count for type {nameof(RuntimePart)}. Expected: {9}, Actual: {actualCount}");
                    }

                    var importingCtor = default(MethodRef);
                    IReadOnlyList<RuntimeComposition.RuntimeImport> importingCtorArguments = ImmutableList<RuntimeComposition.RuntimeImport>.Empty;

                    TypeRef typeRef = options.Resolver.GetFormatterWithVerify<TypeRef>().Deserialize(ref reader, options);

                    IMessagePackFormatter<IReadOnlyList<RuntimeExport>> runtimeExportFormatter = options.Resolver.GetFormatterWithVerify<IReadOnlyList<RuntimeExport>>();
                    IReadOnlyList<RuntimeExport> exports = runtimeExportFormatter.Deserialize(ref reader, options);

                    IMessagePackFormatter<IReadOnlyList<RuntimeImport>> runtimeImportFormatter = options.Resolver.GetFormatterWithVerify<IReadOnlyList<RuntimeImport>>();

                    if (reader.TryReadNil())
                    {
                        reader.Skip();
                    }
                    else
                    {
                        importingCtor = options.Resolver.GetFormatterWithVerify<MethodRef?>().Deserialize(ref reader, options);
                        importingCtorArguments = runtimeImportFormatter.Deserialize(ref reader, options);
                    }

                    IReadOnlyList<RuntimeImport> importingMembers = runtimeImportFormatter.Deserialize(ref reader, options);
                    IReadOnlyList<MethodRef> onImportsSatisfiedMethods = options.Resolver.GetFormatterWithVerify<IReadOnlyList<MethodRef>>().Deserialize(ref reader, options);

                    string? sharingBoundary = options.Resolver.GetFormatterWithVerify<string?>().Deserialize(ref reader, options);

                    return new RuntimePart(typeRef, importingCtor, importingCtorArguments, importingMembers, exports, onImportsSatisfiedMethods, sharingBoundary);
                }
                finally
                {
                    reader.Depth--;
                }
            }

            /// <inheritdoc/>
            public void Serialize(ref MessagePackWriter writer, RuntimePart? value, MessagePackSerializerOptions options)
            {
                if (value is null)
                {
                    writer.WriteNil();
                    return;
                }

                writer.WriteArrayHeader(7);

                options.Resolver.GetFormatterWithVerify<TypeRef>().Serialize(ref writer, value.TypeRef, options);
                options.Resolver.GetFormatterWithVerify<IReadOnlyCollection<RuntimeExport>>().Serialize(ref writer, value.Exports, options);

                IMessagePackFormatter<IReadOnlyCollection<RuntimeImport>> runtimeImportFormatter = options.Resolver.GetFormatterWithVerify<IReadOnlyCollection<RuntimeImport>>();

                if (value.ImportingConstructorOrFactoryMethodRef is null)
                {
                    writer.WriteNil(); // no Importing Constructor Or Factory MethodRef arguments
                    writer.WriteNil(); // no importing constructor arguments
                }
                else
                {
                    options.Resolver.GetFormatterWithVerify<MethodRef?>().Serialize(ref writer, value.ImportingConstructorOrFactoryMethodRef, options);
                    runtimeImportFormatter.Serialize(ref writer, value.ImportingConstructorArguments, options);
                }

                runtimeImportFormatter.Serialize(ref writer, value.ImportingMembers, options);
                options.Resolver.GetFormatterWithVerify<IReadOnlyCollection<MethodRef>>().Serialize(ref writer, value.OnImportsSatisfiedMethodRefs, options);

                options.Resolver.GetFormatterWithVerify<string?>().Serialize(ref writer, value.SharingBoundary, options);
            }
        }
    }
}
