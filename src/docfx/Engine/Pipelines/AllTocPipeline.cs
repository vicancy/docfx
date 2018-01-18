// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.DocAsCode.Build.ConceptualDocuments;
using Microsoft.DocAsCode.Build.Engine;
using Microsoft.DocAsCode.Build.ManagedReference;
using Microsoft.DocAsCode.Build.TableOfContents;
using Microsoft.DocAsCode.Common;
using Microsoft.DocAsCode.DataContracts.Common;
using Microsoft.DocAsCode.Exceptions;
using Microsoft.DocAsCode.Plugins;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static Microsoft.DocAsCode.Utility;

namespace Microsoft.DocAsCode
{
    internal class InvertedTocMap: Dictionary<string, HashSet<string>> {
        public InvertedTocMap(IEqualityComparer<string> comparer) : base(comparer) { }
    }

    internal class AllTocPipeline
    {
        private readonly Config _config;

        public BuildPipeline Pipeline { get; }

        /// <summary>
        /// FileAndType for AllToc is virtual one
        /// </summary>
        /// <param name="controller"></param>
        /// <param name="file"></param>
        /// <param name="config"></param>
        /// <param name="processor"></param>
        public AllTocPipeline(BuildController controller, Config config)
        {
            _config = config;
            Pipeline = new BuildPipeline(controller, new string[]
                    {
                    },new Type[] { typeof(InvertedTocMap) }, BuildDocument);
        }

        private InvertedTocMap CalculateInvertedTocMap(TocMap[] tocmaps)
        {
            var itm = new InvertedTocMap(FilePathComparer.OSPlatformSensitiveStringComparer);
            foreach(var t in tocmaps)
            {
                foreach(var i in t.LinkedTopics)
                {
                    if (itm.TryGetValue(i, out var val))
                    {
                        val.Add(t.Toc.Key);
                    }
                    else
                    {
                        itm[i] = new HashSet<string> { t.Toc.Key };
                    }
                }
            }

            return itm;
        }

        private async Task BuildDocument(BuildPipeline p, Context context)
        {
            var tocFiles = context.Tocs.Values;
            using (new LoggerFileScope("AllTocBuild"))
            {
                var tocmaps = await p.Require<TocMap>(context, tocFiles.ToArray());
                var itm = CalculateInvertedTocMap(tocmaps);
                await p.Report(itm);
            }
        }
    }
}
