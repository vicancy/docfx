// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.SchemaDrivenProcessor.SchemaHandlers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Composition;
    using System.IO;

    using Microsoft.DocAsCode.Build.Common;
    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.DataContracts.Common;
    using Microsoft.DocAsCode.Plugins;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Schema;
    using Newtonsoft.Json.Linq;
    using System.Collections.Concurrent;
    using System.Threading;
    using Newtonsoft.Json.Converters;
    using Newtonsoft.Json.Serialization;
    using Microsoft.DocAsCode.Exceptions;

    public class SchemaValidator
    {
        private const string SupportedMetaSchemaUrl = "https://github.com/dotnet/docfx/schemas/v1.0/schema.json";
        public static void Validate(DSchema schema)
        {
            using (var stream = typeof(SchemaValidator).Assembly.GetManifestResourceStream("Microsoft.DocAsCode.Build.SchemaDrivenProcessor.schemas.v1.0.schema.json"))
            using (var sr = new StreamReader(stream))
            {
                var metaSchema = JSchema.Parse(sr.ReadToEnd());
                var o = JObject.FromObject(schema);
                var isValid = o.IsValid(metaSchema, out IList<string> errors);
                if (!isValid)
                {
                    throw new InvalidSchemaException($"Schema {schema.Title} is not a valid one according to {SupportedMetaSchemaUrl}: \n{errors.ToDelimitedString("\n")}");
                }
            }
        }
    }

    public class DSchema
    {
        private const string SchemaFileEnding = ".schema.json";
        public static readonly ThreadLocal<JsonSerializer> DefaultSerializer = new ThreadLocal<JsonSerializer>(
               () => new JsonSerializer
               {
                   NullValueHandling = NullValueHandling.Ignore,
                   ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
                   ContractResolver = new CamelCasePropertyNamesContractResolver(),
                   Converters =
                   {
                     new StringEnumConverter { CamelCaseText = true },
                   },


               });

        [JsonProperty("$schema")]
        [JsonRequired]
        public string Schema { get; set; }

        [JsonRequired]
        public string Version { get; set; }

        [JsonRequired]
        public string Type { get; set; }

        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }

        public Dictionary<string, PropertySchema> Properties { get; set; }

        public static DSchema Load(TextReader reader, string title)
        {
            using (var json = new JsonTextReader(reader))
            {
                DSchema schema;
                try
                {
                    schema = DefaultSerializer.Value.Deserialize<DSchema>(json);
                }
                catch (Exception e)
                {
                    throw new InvalidSchemaException($"Not a valid schema: {e.Message}", e);
                }

                SchemaValidator.Validate(schema);

                if (string.IsNullOrWhiteSpace(schema.Title))
                {
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        throw new InvalidSchemaException($"Title of schema must be specified.");
                    }
                    schema.Title = title;
                }

                if (schema.Type != "object")
                {
                    throw new InvalidSchemaException("Type for the root schema object must be object");
                }

                return schema;
            }
        }

        public static DSchema Load(string schemaPath)
        {
            if (string.IsNullOrEmpty(schemaPath))
            {
                throw new ArgumentNullException(nameof(schemaPath));
            }
            if (!schemaPath.EndsWith(SchemaFileEnding, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidSchemaException($"Schema path must be end with {SchemaFileEnding}");
            }

            var fileName = Path.GetFileName(schemaPath);
            var name = fileName.Substring(0, fileName.Length - SchemaFileEnding.Length);

            using (var fr = new StreamReader(schemaPath))
            {
                return Load(fr, fileName);
            }
        }
    }

    public class PropertySchema
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public JSchemaType? Type { get; set; }
        public JToken Default { get; set; }
        public Dictionary<string, PropertySchema> Properties { get; set; }
        public PropertySchema Items { get; set; }
        public ReferenceType Reference { get; set; }
        public ContentType ContentType { get; set; }

        public List<string> Tags { get; set; }

        public MergeType MergeType { get; set; }

    }

    public enum ReferenceType
    {
        None,
        File
    }

    public enum ContentType
    {
        Default,
        Uid,
        Xref,
        Href,
        File,
        Markdown
    }

    public enum MergeType
    {
        Merge,
        Key,
        Replace,
        Ignore
    }
}
