// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.SchemaDriven.Processors
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.Plugins;

    public class SchemaProcessor
    {
        private readonly IList<IInterpreter> _interpreters;

        public static void Process(IHostService host, FileModel model, SchemaProcessor processor)
        {
            var content = model.Content;
            var context = new ProcessContext(host, model);
            context.Properties.Uids = new List<UidDefinition>();
            context.Properties.UidLinkSources = new Dictionary<string, List<LinkSourceInfo>>();
            context.Properties.FileLinkSources = new Dictionary<string, List<LinkSourceInfo>>();
            context.Properties.Dependency = new HashSet<string>();
            context.Properties.XRefSpecs = new List<XRefSpec>();
            context.Properties.ExternalXRefSpecs = new List<XRefSpec>();
            context.Properties.ContentOriginalFile = context.Model.OriginalFileAndType;
            DocumentSchema schema = model.Properties.Schema;
            content = processor.Process(content, schema, context);
            model.LinkToUids = model.LinkToUids.Union(((Dictionary<string, List<LinkSourceInfo>>)context.Properties.UidLinkSources).Keys);
            model.LinkToFiles = model.LinkToFiles.Union(((Dictionary<string, List<LinkSourceInfo>>)context.Properties.FileLinkSources).Keys);
            model.FileLinkSources = model.FileLinkSources.Merge((Dictionary<string, List<LinkSourceInfo>>)context.Properties.FileLinkSources);
            model.UidLinkSources = model.UidLinkSources.Merge((Dictionary<string, List<LinkSourceInfo>>)context.Properties.UidLinkSources);
            model.Uids = model.Uids.AddRange(context.Properties.Uids);
            model.Properties.XRefSpecs = context.Properties.XRefSpecs;
            model.Properties.ExternalXRefSpecs = context.Properties.ExternalXRefSpecs;

            foreach (var d in context.Properties.Dependency)
            {
                host.ReportDependencyTo(model, d, DependencyTypeName.Include);
            }
        }

        public SchemaProcessor(params IInterpreter[] interpreters)
        {
            _interpreters = interpreters;
        }

        public object Process(object raw, BaseSchema schema, IProcessContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.Model == null)
            {
                throw new ArgumentNullException(nameof(context) + "." + nameof(context.Model));
            }

            return InterpretCore(raw, schema, string.Empty, context);
        }

        private object InterpretCore(object value, BaseSchema schema, string path, IProcessContext context)
        {
            if (!DictionaryInterpret<object>(value, schema, path, context))
            {
                if (!DictionaryInterpret<string>(value, schema, path, context))
                {
                    if (value is IList<object> array)
                    {
                        for (var i = 0; i < array.Count; i++)
                        {
                            var val = array[i];
                            var obj = InterpretCore(val, schema?.Items, $"{path}/{i}", context);
                            if (!ReferenceEquals(obj, val))
                            {
                                array[i] = obj;
                            }
                        }
                    }
                }
            }

            return Interpret(value, schema, path, context);
        }

        private bool DictionaryInterpret<TKey>(object value, BaseSchema schema, string path, IProcessContext context)
        {
            if (value is IDictionary<TKey, object> dict)
            {
                foreach (var keyRaw in dict.Keys.ToList())
                {
                    if (keyRaw is string key)
                    {
                        BaseSchema baseSchema = null;
                        schema?.Properties?.TryGetValue(key, out baseSchema);
                        var val = dict[keyRaw];
                        var obj = InterpretCore(val, baseSchema, $"{path}/{key}", context);
                        if (!ReferenceEquals(obj, val))
                        {
                            dict[keyRaw] = obj;
                        }
                    }
                    else
                    {
                        throw new NotSupportedException("Only support string as key");
                    }
                }
                return true;
            }

            return false;
        }

        private object Interpret(object value, BaseSchema schema, string path, IProcessContext context)
        {
            var val = value;
            foreach (var i in _interpreters.Where(s => s.CanInterpret(schema)))
            {
                val = i.Interpret(schema, val, context, path);
            }

            return val;
        }
    }
}
