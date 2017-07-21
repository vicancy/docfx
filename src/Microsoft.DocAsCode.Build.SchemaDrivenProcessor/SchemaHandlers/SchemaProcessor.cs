// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.SchemaDrivenProcessor.SchemaHandlers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using Newtonsoft.Json.Schema;

    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.Plugins;

    public class SchemaProcessor
    {
        private IList<IInterpreter> _interpreters;

        public SchemaProcessor()
        {
            _interpreters = new List<IInterpreter>
                            {
                                 new FileIncludeInterpretor(),
                                 new MarkdownInterpretor(),
                                 new UidInterpretor(),
                                 new FileInterpretor(),
                            };
        }

        public void RegisterInterpreter(IInterpreter interpreter)
        {
            _interpreters.Add(interpreter);
        }

        public object Process(object raw, DSchema schema, ProcessContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.FileAndType == null)
            {
                throw new ArgumentNullException(nameof(context) + "." + nameof(context.FileAndType));
            }

            context.Interpreters = _interpreters.OrderBy(s => s.Order).ToList();
            var interpreter = PropertySchemaInterpreter.CreateInterpreter(raw, schema, string.Empty);
            return interpreter.Interpret(context);
        }
    }

    public class PropertySchemaInterpreter
    {
        public static PropertySchemaInterpreter CreateInterpreter(object value, PropertySchema schema, string path)
        {
            var currentPath = path + "/";
            if (value is Dictionary<object, object> dict)
            {
                return new DictionaryInterpreter(dict, schema, currentPath);
            }

            if (value is IDictionary<string, object> idict)
            {
                return new IDictionaryInterpreter(idict, schema, currentPath);
            }

            if (value is List<object> array)
            {
                return new ArrayInterpreter(array, schema, currentPath);
            }
            else
            {
                return new PrimativeInterpreter(value, schema, path);
            }
        }

        public virtual object Interpret(ProcessContext context)
        {
            return null;
        }

        public static object Interpret(object value, PropertySchema schema, string path, ProcessContext context)
        {
            var val = value;
            foreach (var i in context.Interpreters.Where(s => s.CanInterpret(schema)))
            {
                val = i.Interpret(schema, val, context, path);
            }

            return val;
        }
    }

    public class DictionaryInterpreter : PropertySchemaInterpreter
    {
        private Dictionary<object, object> _value;
        private PropertySchema _schema;
        private string _path;
        public DictionaryInterpreter(Dictionary<object, object> value, PropertySchema schema, string path)
        {
            _value = value;
            _schema = schema;
            _path = path;
        }

        public override object Interpret(ProcessContext context)
        {
            if (_schema.Properties != null)
            {
                foreach (var keyRaw in _value.Keys.ToList())
                {
                    var key = keyRaw as string;
                    if (key == null)
                    {
                        throw new NotSupportedException("Only support string as key");
                    }

                    if (_schema.Properties.TryGetValue(key, out var propertySchema))
                    {
                        var val = _value[keyRaw];
                        var interpreter = PropertySchemaInterpreter.CreateInterpreter(val, propertySchema, $"{_path}/{key}");
                        var obj = interpreter.Interpret(context);
                        if (!ReferenceEquals(obj, val))
                        {
                            _value[keyRaw] = obj;
                        }
                    }
                }
            }

            return _value;
        }
    }
    public class IDictionaryInterpreter : PropertySchemaInterpreter
    {
        private IDictionary<string, object> _value;
        private PropertySchema _schema;
        private string _path;
        public IDictionaryInterpreter(IDictionary<string, object> value, PropertySchema schema, string path)
        {
            _value = value;
            _schema = schema;
            _path = path;
        }

        public override object Interpret(ProcessContext context)
        {
            if (_schema.Properties != null)
            {
                foreach (var key in _value.Keys.ToList())
                {
                    if (_schema.Properties.TryGetValue(key, out var propertySchema))
                    {
                        var val = _value[key];
                        var interpreter = PropertySchemaInterpreter.CreateInterpreter(val, propertySchema, $"{_path}/{key}");
                        var obj = interpreter.Interpret(context);
                        if (!ReferenceEquals(obj, val))
                        {
                            _value[key] = obj;
                        }
                    }
                }
            }

            return _value;
        }
    }

    public class ArrayInterpreter : PropertySchemaInterpreter
    {
        private List<object> _value;
        private PropertySchema _schema;
        private string _path;
        public ArrayInterpreter(List<object> value, PropertySchema schema, string path)
        {
            _value = value;
            _schema = schema;
            _path = path;
        }

        public override object Interpret(ProcessContext context)
        {
            if (_schema.Items != null)
            {
                for (var i = 0; i < _value.Count; i++)
                {
                    var val = _value[i];
                    var interpreter = PropertySchemaInterpreter.CreateInterpreter(val, _schema.Items, $"{_path}/[{i}]");
                    var obj = interpreter.Interpret(context);
                    if (!ReferenceEquals(obj, val))
                    {
                        _value[i] = obj;
                    }
                }
            }

            return _value;
        }
    }

    public class PrimativeInterpreter : PropertySchemaInterpreter
    {
        private object _value;
        private PropertySchema _schema;
        private string _path;
        public PrimativeInterpreter(object value, PropertySchema schema, string path)
        {
            _value = value;
            _schema = schema;
            _path = path;
        }

        public override object Interpret(ProcessContext context)
        {
            return Interpret(_value, _schema, _path, context);
        }
    }

    public class ProcessContext
    {
        public IEnumerable<IInterpreter> Interpreters { get; set; }
        public IHostService Host { get; set; }
        public bool EnableContentPlaceholder { get; set; }
        public string PlaceholderContent { get; set; }
        public bool ContainsPlaceholder { get; set; }

        public FileAndType FileAndType { get; set; }
        public string LocalPathFromRoot { get; set; }
        public HashSet<string> LinkToFiles { get; set; } = new HashSet<string>(FilePathComparer.OSPlatformSensitiveStringComparer);
        public HashSet<string> LinkToUids { get; set; } = new HashSet<string>();
        public List<UidDefinition> Uids { get; set; } = new List<UidDefinition>();
        public Dictionary<string, List<LinkSourceInfo>> UidLinkSources { get; set; } = new Dictionary<string, List<LinkSourceInfo>>();
        public Dictionary<string, List<LinkSourceInfo>> FileLinkSources { get; set; } = new Dictionary<string, List<LinkSourceInfo>>();
        public Stack<string> ObjectPath { get; set; }
    }

    public interface ITagInterpretor
    {
        object Interpret(string tag, object value);
    }

    public interface IInterpreter
    {
        int Order { get; }
        bool CanInterpret(PropertySchema schema);
        object Interpret(PropertySchema schema, object value, ProcessContext context, string path);
    }

    public class MarkdownInterpretor : IInterpreter
    {
        public int Order => 2;
        public bool CanInterpret(PropertySchema schema)
        {
            return schema.ContentType == ContentType.Markdown && schema.Type == JSchemaType.String;
        }

        public object Interpret(PropertySchema schema, object value, ProcessContext context, string path)
        {
            if (schema.ContentType == ContentType.Markdown)
            {
                var val = value as string;
                if (val == null)
                {
                    throw new InvalidDataException($"{value.GetType()} is not supported for {nameof(MarkdownInterpretor)}.");
                }

                return MarkupCore(val, context);
            }

            return value;
        }

        private static string MarkupCore(string content, ProcessContext context)
        {
            var host = context.Host;
            var mr = host.Markup(content, context.FileAndType);
            context.LinkToUids.UnionWith(mr.LinkToUids);
            context.LinkToFiles.UnionWith(mr.LinkToFiles);
            context.FileLinkSources = context.FileLinkSources.Merge(mr.FileLinkSources.Select(s => new KeyValuePair<string, IEnumerable<LinkSourceInfo>>(s.Key, s.Value)));
            context.UidLinkSources = context.UidLinkSources.Merge(mr.UidLinkSources.Select(s => new KeyValuePair<string, IEnumerable<LinkSourceInfo>>(s.Key, s.Value)));
            return mr.Html;
        }
    }

    public class UidInterpretor : IInterpreter
    {
        public int Order => 2;
        public bool CanInterpret(PropertySchema schema)
        {
            return schema.ContentType == ContentType.Uid && schema.Type == JSchemaType.String;
        }

        public object Interpret(PropertySchema schema, object value, ProcessContext context, string path)
        {
            if (schema.ContentType == ContentType.Uid)
            {
                var val = value as string;
                if (val == null)
                {
                    throw new InvalidDataException($"{value.GetType()} is not supported for {nameof(UidInterpretor)}.");
                }

                context.Uids.Add(new UidDefinition(val, context.LocalPathFromRoot, path: path));
            }

            return value;
        }
    }

    public class FileInterpretor : IInterpreter
    {
        public int Order => 2;
        public bool CanInterpret(PropertySchema schema)
        {
            return schema.ContentType == ContentType.File && schema.Type == JSchemaType.String;
        }

        public object Interpret(PropertySchema schema, object value, ProcessContext context, string path)
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

                var currentFile = (RelativePath)context.FileAndType.File;
                relPath = currentFile + relPath;

                context.LinkToFiles.Add(relPath.GetPathFromWorkingFolder());
            }

            return value;
        }
    }

    public class FileIncludeInterpretor : IInterpreter
    {
        public int Order => 0;
        public bool CanInterpret(PropertySchema schema)
        {
            return schema.Type == JSchemaType.String && schema.Reference != ReferenceType.None;
        }

        public object Interpret(PropertySchema schema, object value, ProcessContext context, string path)
        {
            if (schema.Reference == ReferenceType.File)
            {
                var val = value as string;
                if (val == null)
                {
                    throw new InvalidDataException($"{value.GetType()} is not supported for {nameof(FileIncludeInterpretor)}.");
                }
                var relPath = RelativePath.TryParse(val);
                if (relPath != null)
                {
                    var currentFile = (RelativePath)context.FileAndType.File;
                    val = currentFile + relPath;
                }
                return EnvironmentContext.FileAbstractLayer.ReadAllText(path);
            }

            return value;
        }
    }
}
