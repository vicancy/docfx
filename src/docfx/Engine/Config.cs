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
    internal class Config
    {
        public string pageUrlExtension { get; set; }

        public string pageFileExtension { get; set; }

        public ImmutableDictionary<string, object> Metadata { get; set; }
        public FileMetadata FileMetadata { get; set; }

        public DocumentBuildContext DBC { get; set; }

        public TemplateProcessor TemplateProcessor { get; set; }

        public ApplyTemplateSettings ApplyTemplateSettings { get; set; }

        public IHostService HostService { get; set; }

    }
}
