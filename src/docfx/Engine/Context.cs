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

        public ConcurrentDictionary<string, XRefSpec> XrefSpecMapping { get; } = new ConcurrentDictionary<string, XRefSpec>();

        /// <summary>
        /// Record file to possible toc mapping
        /// </summary>
        public ConcurrentDictionary<string, ConcurrentBag<PossibleToc>> FilePossibleTocMapping { get; } = new ConcurrentDictionary<string, ConcurrentBag<PossibleToc>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// UID-Path mapping, path is always start with ~
        /// </summary>
        public IReadOnlyDictionary<string, HashSet<string>> PossibleUidMapping { get; }

        /// <summary>
        /// File Source to Target mapping, path is always start with ~
        /// </summary>
        public IReadOnlyDictionary<string, FileAndType> FileMapping { get; }

        public IReadOnlyDictionary<string, FileAndType> Tocs { get; }

        public Context(IReadOnlyDictionary<string, FileAndType> fileMapping, IReadOnlyDictionary<string, FileAndType> tocs, IReadOnlyDictionary<string, HashSet<string>> possibleUidMappings)
        {
            Tocs = tocs;
            FileMapping = fileMapping;
            PossibleUidMapping = possibleUidMappings;
        }

        public void RegisterToc()
        {

        }
    }

    internal class PossibleToc
    {
        public string Uid { get; set; }
        public string TocKey { get; set; }
        public bool ForSure => Uid == null;
    }
}
