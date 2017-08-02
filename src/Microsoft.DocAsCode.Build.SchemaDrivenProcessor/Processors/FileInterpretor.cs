// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.SchemaDrivenProcessor.Processors
{
    using System.IO;

    using Newtonsoft.Json.Schema;

    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.Plugins;

    public class FileInterpretor : IInterpreter
    {
        public int Order => 2;
        public bool CanInterpret(BaseSchema schema)
        {
            return schema.ContentType == ContentType.File && schema.Type == JSchemaType.String;
        }

        public object Interpret(BaseSchema schema, object value, IProcessContext context, string path)
        {
            if (schema.ContentType == ContentType.File)
            {
                var val = value as string;
                if (val == null)
                {
                    throw new InvalidDataException($"{value.GetType()} is not supported for {nameof(FileInterpretor)}.");
                }

                var relPath = RelativePath.TryParse(val);
                if (relPath == null)
                {
                    throw new DocumentException($"Only Relative path is supported. Value: \"{relPath}\" is not supported.");
                }

                var currentFile = (RelativePath)context.Model.FileAndType.File;
                relPath = currentFile + relPath;

                context.Properties.LinkToFiles.Add(relPath.GetPathFromWorkingFolder());
            }

            return value;
        }
    }
}
