// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.SchemaDrivenProcessor.Processors
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using Newtonsoft.Json.Schema;

    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.Plugins;

    public class UidInterpretor : IInterpreter
    {
        public int Order => 2;
        public bool CanInterpret(BaseSchema schema)
        {
            return schema.ContentType == ContentType.Uid && schema.Type == JSchemaType.String;
        }

        public object Interpret(BaseSchema schema, object value, IProcessContext context, string path)
        {
            if (schema.ContentType == ContentType.Uid)
            {
                var val = value as string;
                if (val == null)
                {
                    throw new InvalidDataException($"{value.GetType()} is not supported for {nameof(UidInterpretor)}.");
                }

                context.Properties.Uids.Add(new UidDefinition(val, context.Model.LocalPathFromRoot, path: path));
            }

            return value;
        }
    }
}
