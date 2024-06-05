﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
namespace Microsoft.VisualStudio.Composition.Formatter
{
    using System.Collections.Immutable;
    using System.Reflection;
    using MessagePack;
    using MessagePack.Formatters;

    internal class StrongAssemblyIdentityFormatter : BaseMessagePackFormatter<StrongAssemblyIdentity?>
    {
        public static readonly StrongAssemblyIdentityFormatter Instance = new();

        private StrongAssemblyIdentityFormatter()
            : base(expectedArrayElementCount: 3)
        {
        }

        /// <inheritdoc/>
        protected override StrongAssemblyIdentity? DeserializeData(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            Guid mvid = options.Resolver.GetFormatterWithVerify<Guid>().Deserialize(ref reader, options);
            string fullName = reader.ReadString()!;

            var assemblyName = new AssemblyName(fullName);
            assemblyName.CodeBase = reader.ReadString()!;
            return new StrongAssemblyIdentity(assemblyName, mvid);
        }

        /// <inheritdoc/>
        protected override void SerializeData(ref MessagePackWriter writer, StrongAssemblyIdentity? value, MessagePackSerializerOptions options)
        {
            options.Resolver.GetFormatterWithVerify<Guid>().Serialize(ref writer, value!.Mvid, options);
            writer.Write(value.Name.FullName);
            writer.Write(value.Name.CodeBase!.ToString());
        }
    }
}