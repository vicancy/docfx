// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Docs.BuildCore
{
    using System;
    using System.IO;
    using Newtonsoft.Json.Linq;
    using YamlDotNet.Core;
    using YamlDotNet.RepresentationModel;

    static class YamlUtil
    {
        public static string GetMime(string yaml)
        {
            var i = 0;
            while (yaml[i] == '#') i++;
            if (i == 0) return null;
            while (yaml[i] == ' ') i++;

            if (yaml[i++] != 'Y' ||
                yaml[i++] != 'a' ||
                yaml[i++] != 'm' ||
                yaml[i++] != 'l' ||
                yaml[i++] != 'M' ||
                yaml[i++] != 'i' ||
                yaml[i++] != 'm' ||
                yaml[i++] != 'e' ||
                yaml[i++] != ':')
                return null;

            while (yaml[i] == ' ') i++;

            var j = i;
            while (j < yaml.Length && yaml[j] != '\r' && yaml[j] != '\n') j++;

            return yaml.Substring(i, j - i);
        }

        public static JToken Parse(string yaml)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1) throw new NotSupportedException("Does not support mutiple YAML documents");
            return ToJson(stream.Documents[0].RootNode);

            JToken ToJson(YamlNode node)
            {
                if (node is YamlScalarNode scalar)
                {
                    if (scalar.Style == ScalarStyle.Plain)
                    {
                        // TODO: Just for backward compatibility, should be an empty string
                        if (scalar.Value == "") return null;
                        if (long.TryParse(scalar.Value, out var n)) return new JValue(n);
                        if (double.TryParse(scalar.Value, out var d)) return new JValue(d);
                        if (bool.TryParse(scalar.Value, out var b)) return new JValue(b);
                    }
                    return new JValue(scalar.Value);
                }
                if (node is YamlMappingNode map)
                {
                    var obj = new JObject();
                    foreach (var pair in map) obj[pair.Key.ToString()] = ToJson(pair.Value);
                    return obj;
                }
                if (node is YamlSequenceNode seq)
                {
                    var arr = new JArray();
                    foreach (var item in seq) arr.Add(ToJson(item));
                    return arr;
                }
                throw new NotSupportedException($"Unknown yaml node type {node.GetType()}");
            }
        }
    }
}
