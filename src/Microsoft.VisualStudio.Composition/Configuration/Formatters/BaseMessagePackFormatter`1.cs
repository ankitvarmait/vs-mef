﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Composition.Formatter
{
    using System.Collections.Immutable;
    using System.Reflection.Emit;
    using MessagePack;
    using MessagePack.Formatters;

    /// <summary>
    /// Provides a base implementation for MessagePack formatters.
    /// </summary>
    /// <typeparam name="TRequest">The type of the request.</typeparam>
    internal abstract class BaseMessagePackFormatter<TRequest> : IMessagePackFormatter<TRequest?>
           where TRequest : class?
    {
        protected BaseMessagePackFormatter(int expectedArrayElementCount, bool enableDefaultCheckArrayHeader = true)
        {
            this.EnableDefaultCheckArrayHeader = enableDefaultCheckArrayHeader;
            this.ExpectedArrayElementCount = expectedArrayElementCount;
        }

        /// <inheritdoc/>
        public TRequest? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil())
            {
                return null;
            }

            options.Security.DepthStep(ref reader);
            TRequest? response;
            try
            {
                this.CheckArrayHeaderCount(ref reader, this.ExpectedArrayElementCount);
                response = this.DeserializeData(ref reader, options);
            }
            finally
            {
                reader.Depth--;
            }

            return response;
        }

        /// <inheritdoc/>
        public void Serialize(ref MessagePackWriter writer, TRequest? value, MessagePackSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNil();
                return;
            }

            WriteSerializedData(ref writer);

            void WriteSerializedData(ref MessagePackWriter writerRef)
            {
                if (this.EnableDefaultCheckArrayHeader)
                {
                    writerRef.WriteArrayHeader(this.ExpectedArrayElementCount);
                }

                this.SerializeData(ref writerRef, value, options);
            }
        }

        protected virtual void CheckArrayHeaderCount(ref MessagePackReader reader, int expectedCount)
        {
            if (this.EnableDefaultCheckArrayHeader)
            {
                var actualCount = reader.ReadArrayHeader();
                if (actualCount != expectedCount)
                {
                    throw new MessagePackSerializationException($"Invalid array count for type {typeof(TRequest).Name}. Expected: {expectedCount}, Actual: {actualCount}");
                }
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether to enable default check array header.
        /// </summary>
        protected virtual bool EnableDefaultCheckArrayHeader { get; set; }

        /// <summary>
        /// Gets the number of elements in the array.
        /// </summary>
        protected virtual int ExpectedArrayElementCount { get; private set; }

        /// <summary>
        /// Deserializes the request from the MessagePackReader.
        /// </summary>
        /// <param name="reader">The MessagePackReader to read from.</param>
        /// <param name="options">The serializer options.</param>
        /// <returns>The deserialized request.</returns>
        protected abstract TRequest? DeserializeData(ref MessagePackReader reader, MessagePackSerializerOptions options);

        /// <summary>
        /// Serializes the request to the MessagePackWriter.
        /// </summary>
        /// <param name="writer">The MessagePackWriter to write to.</param>
        /// <param name="value">The value to serialize.</param>
        /// <param name="options">The serializer options.</param>
        protected abstract void SerializeData(ref MessagePackWriter writer, TRequest value, MessagePackSerializerOptions options);
    }
}