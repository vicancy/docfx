// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.SchemaDrivenProcessor
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
    using Microsoft.DocAsCode.Build.SchemaDrivenProcessor.SchemaHandlers;
    using System.Linq;

    [Export(nameof(SchemaDrivenDocumentProcessor), typeof(IDocumentBuildStep))]
    public class BuildSchemaBasedDocument : BuildReferenceDocumentBase, ISupportIncrementalBuildStep
    {
        private const string DocumentTypeKey = "documentType";
        private readonly SchemaProcessor _schemaProcessor = new SchemaProcessor();
        public override string Name => nameof(BuildSchemaBasedDocument);

        public override int BuildOrder => 0;

        protected override void BuildArticle(IHostService host, FileModel model)
        {
            var content = model.Content;
            var context = new ProcessContext
            {
                FileAndType = model.FileAndType,
                LocalPathFromRoot = model.LocalPathFromRoot,
                Host = host,
            };
            DSchema schema = model.Properties.Schema;
            content = _schemaProcessor.Process(content, schema, context);
            model.LinkToUids = model.LinkToUids.Union(context.LinkToUids);
            model.LinkToFiles = model.LinkToFiles.Union(context.LinkToFiles);
            model.FileLinkSources = model.FileLinkSources.ToDictionary(v => v.Key, v => v.Value.ToList())
                .Merge(context.FileLinkSources.Select(i => new KeyValuePair<string, IEnumerable<LinkSourceInfo>>(i.Key, i.Value)))
                .ToImmutableDictionary(v => v.Key, v => v.Value.ToImmutableList());
            model.UidLinkSources = model.UidLinkSources.ToDictionary(v => v.Key, v => v.Value.ToList())
                .Merge(context.UidLinkSources.Select(i => new KeyValuePair<string, IEnumerable<LinkSourceInfo>>(i.Key, i.Value)))
                .ToImmutableDictionary(v => v.Key, v => v.Value.ToImmutableList());
        }

        #region ISupportIncrementalBuildStep Members

        public bool CanIncrementalBuild(FileAndType fileAndType) => true;

        public string GetIncrementalContextHash() => null;

        public IEnumerable<DependencyType> GetDependencyTypesToRegister() => null;

        #endregion
    }
}
