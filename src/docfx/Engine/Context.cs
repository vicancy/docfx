// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.DocAsCode.Build.ConceptualDocuments;
using Microsoft.DocAsCode.Build.Engine;
using Microsoft.DocAsCode.Build.ManagedReference;
using Microsoft.DocAsCode.Build.TableOfContents;
using Microsoft.DocAsCode.Common;
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

namespace Microsoft.DocAsCode
{
    internal class Context
    {
        public ConcurrentBag<ManifestItem> ManifestItems { get; } = new ConcurrentBag<ManifestItem>();

        /// <summary>
        /// File to BuildStep mapping
        /// </summary>
        public ConcurrentDictionary<string, IBuildStep> FileStepMapping { get; } = new ConcurrentDictionary<string, IBuildStep>();
         /// <summary>
        /// UID-Path mapping, path is always start with ~
        /// </summary>
        public Dictionary<string, List<string>> PossibleUidMapping { get; } = new Dictionary<string, List<string>>();

        /// <summary>
        /// File Source to Target mapping, path is always start with ~
        /// </summary>
        public ConcurrentDictionary<string, FileAndType> FileMapping { get; } = new ConcurrentDictionary<string, FileAndType>();

        public ConcurrentDictionary<string, FileAndType> Tocs { get; } = new ConcurrentDictionary<string, FileAndType>();

        /// <summary>
        /// Save the files that are unable to be resolved by any processor
        /// </summary>
        public ConcurrentBag<FileAndType> UnhandledItems { get; } = new ConcurrentBag<FileAndType>();

        public ConcurrentDictionary<string, XRefSpec[]> XrefSpecMapping { get; } = new ConcurrentDictionary<string, ImmutableArray<XRefSpec>>();

        /// <summary>
        /// Record file to possible toc mapping
        /// </summary>
        public ConcurrentDictionary<string, ConcurrentBag<PossibleToc>> FilePossibleTocMapping { get; } = new ConcurrentDictionary<string, ConcurrentBag<PossibleToc>>();
    }

    internal class PossibleToc
    {
        public string Uid { get; set; }
        public string TocKey { get; set; }
        public bool ForSure => Uid == null;
    }
}
