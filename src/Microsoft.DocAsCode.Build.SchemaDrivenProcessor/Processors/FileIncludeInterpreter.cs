// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.SchemaDrivenProcessor.Processors
{
    using System.IO;

    using Newtonsoft.Json.Schema;

    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.Plugins;

    public class FileIncludeInterpreter : IInterpreter
    {
        public int Order => 0;
        public bool CanInterpret(BaseSchema schema)
        {
            return schema.Type == JSchemaType.String && schema.Reference != ReferenceType.None;
        }

        public object Interpret(BaseSchema schema, object value, IProcessContext context, string path)
        {
            if (schema.Reference == ReferenceType.File)
            {
                var val = value as string;
                if (val == null)
                {
                    throw new InvalidDataException($"{value.GetType()} is not supported for {nameof(FileIncludeInterpreter)}.");
                }
                var relPath = RelativePath.TryParse(val);
                if (relPath != null)
                {
                    var currentFile = (RelativePath)context.Model.FileAndType.File;
                    val = currentFile + relPath;
                }
                return EnvironmentContext.FileAbstractLayer.ReadAllText(path);
            }

            return value;
        }
    }
}
