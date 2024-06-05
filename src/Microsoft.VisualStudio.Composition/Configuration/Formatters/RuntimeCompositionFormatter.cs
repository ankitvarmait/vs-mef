﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
namespace Microsoft.VisualStudio.Composition.Formatter
{
    using System.Collections.Immutable;
    using MessagePack;
    using MessagePack.Formatters;
    using Microsoft.VisualStudio.Composition.Reflection;
    using static Microsoft.VisualStudio.Composition.RuntimeComposition;

    internal class RuntimeCompositionFormatter : BaseMessagePackFormatter<RuntimeComposition>
    {
        public static readonly RuntimeCompositionFormatter Instance = new();

        private RuntimeCompositionFormatter()
              : base(expectedArrayElementCount: 2)
        {
        }

        /// <inheritdoc/>
        protected override RuntimeComposition DeserializeData(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            IReadOnlyCollection<RuntimePart> parts = options.Resolver.GetFormatterWithVerify<IReadOnlyCollection<RuntimePart>>().Deserialize(ref reader, options);

            IReadOnlyDictionary<TypeRef, RuntimeExport> metadataViewsAndProviders = options.Resolver.GetFormatterWithVerify<IReadOnlyDictionary<TypeRef, RuntimeExport>>().Deserialize(ref reader, options);

            return RuntimeComposition.CreateRuntimeComposition(parts, metadataViewsAndProviders, options.CompositionResolver());
        }

        /// <inheritdoc/>
        protected override void SerializeData(ref MessagePackWriter writer, RuntimeComposition value, MessagePackSerializerOptions options)
        {
            options.Resolver.GetFormatterWithVerify<IReadOnlyCollection<RuntimePart>>().Serialize(ref writer, value.Parts, options);
            options.Resolver.GetFormatterWithVerify<IReadOnlyDictionary<TypeRef, RuntimeExport>>().Serialize(ref writer, value.MetadataViewsAndProviders, options);
        }
    }
}