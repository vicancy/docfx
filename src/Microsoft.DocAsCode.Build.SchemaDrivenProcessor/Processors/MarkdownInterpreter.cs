// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.SchemaDrivenProcessor.Processors
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using Newtonsoft.Json.Schema;

    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.Plugins;

    public class MarkdownInterpreter : IInterpreter
    {
        public int Order => 2;
        public bool CanInterpret(BaseSchema schema)
        {
            return schema.ContentType == ContentType.Markdown && schema.Type == JSchemaType.String;
        }

        public object Interpret(BaseSchema schema, object value, IProcessContext context, string path)
        {
            if (schema.ContentType == ContentType.Markdown)
            {
                var val = value as string;
                if (val == null)
                {
                    throw new InvalidDataException($"{value.GetType()} is not supported for {nameof(MarkdownInterpreter)}.");
                }

                return MarkupCore(val, context);
            }

            return value;
        }

        private static string MarkupCore(string content, IProcessContext context)
        {
            var host = context.Host;
            var mr = host.Markup(content, context.Model.FileAndType);
            ((Dictionary<string, List<LinkSourceInfo>>)context.Properties.FileLinkSources).Merge(mr.FileLinkSources);
            ((Dictionary<string, List<LinkSourceInfo>>)context.Properties.UidLinkSources).Merge(mr.UidLinkSources);
            return mr.Html;
        }
    }

}
