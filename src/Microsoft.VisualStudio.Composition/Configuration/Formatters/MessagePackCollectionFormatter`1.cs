﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Composition.Formatter
{
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Reflection.PortableExecutable;
    using MessagePack;
    using MessagePack.Formatters;

    internal class MessagePackCollectionFormatter<TCollectionType> : IMessagePackFormatter<IReadOnlyCollection<TCollectionType>>
    {
        public static readonly MessagePackCollectionFormatter<TCollectionType> Instance = new();

        private MessagePackCollectionFormatter()
        {
        }

        /// <inheritdoc/>
        public IReadOnlyCollection<TCollectionType> Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            int count = reader.ReadInt32();

            if (count == 0)
            {
                return Array.Empty<TCollectionType>();
            }

            IMessagePackFormatter<TCollectionType> tCollectionTypeFormatter = options.Resolver.GetFormatterWithVerify<TCollectionType>();

            var collection = new TCollectionType[count];
            for (int i = 0; i < count; i++)
            {
                collection[i] = tCollectionTypeFormatter.Deserialize(ref reader, options);
            }

            return collection;
        }

        /// <inheritdoc/>
        public void Serialize(ref MessagePackWriter writer, IReadOnlyCollection<TCollectionType> value, MessagePackSerializerOptions options)
        {
            writer.Write(value.Count);
            IMessagePackFormatter<TCollectionType> tCollectionTypeFormatter = options.Resolver.GetFormatterWithVerify<TCollectionType>();

            foreach (TCollectionType item in value)
            {
                tCollectionTypeFormatter.Serialize(ref writer, item, options);
            }
        }
    }
}
