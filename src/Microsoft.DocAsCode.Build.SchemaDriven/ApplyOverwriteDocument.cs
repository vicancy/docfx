// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.SchemaDriven
{
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Composition;
    using System.Linq;

    using Microsoft.DocAsCode.Build.Common;
    using Microsoft.DocAsCode.Build.SchemaDriven.Processors;
    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.Plugins;
    using Microsoft.DocAsCode.Exceptions;
    using Microsoft.DocAsCode.Common.EntityMergers;

    [Export(nameof(SchemaDrivenDocumentProcessor), typeof(IDocumentBuildStep))]
    public class ApplyOverwriteDocument : BaseDocumentBuildStep, ISupportIncrementalBuildStep
    {
        private readonly SchemaProcessor _overwriteProcessor = new SchemaProcessor(
            new FileIncludeInterpreter(),
            new MarkdownWithContentAnchorInterpreter(new MarkdownInterpreter()),
            new FileInterpreter(true, false),
            new HrefInterpreter(true, false),
            new XrefInterpreter()
            );
        private readonly SchemaProcessor _xrefSpecUpdater = new SchemaProcessor(
            new XrefPropertiesInterpreter()
            );

        private readonly Merger _merger = new Merger();

        public override string Name => nameof(ApplyOverwriteDocument);

        public override int BuildOrder => 0x10;

        public override void Postbuild(ImmutableList<FileModel> models, IHostService host)
        {
            foreach (var uid in host.GetAllUids())
            {
                var ms = host.LookupByUid(uid);
                var ods = ms.Where(m => m.Type == DocumentType.Overwrite).ToList();
                var articles = ms.Except(ods).ToList();
                if (articles.Count == 0 || ods.Count == 0)
                {
                    continue;
                }

                if (articles.Count > 1)
                {
                    throw new DocumentException($"{uid} is defined in multiple articles {articles.Select(s => s.LocalPathFromRoot).ToDelimitedString()}");
                }

                var model = articles[0];
                var schema = model.Properties.Schema as DocumentSchema;
                using (new LoggerFileScope(model.LocalPathFromRoot))
                {
                    var uidDefiniton = model.Uids.Where(s => s.Name == uid).ToList();
                    if (uidDefiniton.Count == 0)
                    {
                        throw new DocfxException($"Unable to find UidDefinition for Uid {uid}");
                    }

                    foreach (var ud in uidDefiniton)
                    {
                        var jsonPointer = new JsonPointer(ud.Path).GetParentPointer();
                        var schemaForCurrentUid = jsonPointer.FindSchema(schema);
                        var source = jsonPointer.GetValue(model.Content);

                        foreach (var od in ods)
                        {
                            using (new LoggerFileScope(od.LocalPathFromRoot))
                            {
                                foreach (var fm in ((IEnumerable<OverwriteDocumentModel>)od.Content).Where(s => s.Uid == uid))
                                {
                                    // Suppose that BuildOverwriteWithSchema do the validation of the overwrite object
                                    var overwriteObject = BuildOverwriteWithSchema(od, fm, host, schemaForCurrentUid);
                                    _merger.Merge(ref source, overwriteObject, ud.Name, string.Empty, schemaForCurrentUid);

                                    model.LinkToUids = model.LinkToUids.Union(od.LinkToUids);
                                    model.LinkToFiles = model.LinkToFiles.Union(od.LinkToFiles);
                                    model.FileLinkSources = model.FileLinkSources.Merge(od.FileLinkSources);
                                    model.UidLinkSources = model.UidLinkSources.Merge(od.UidLinkSources);
                                    ((List<XRefSpec>)model.Properties.XRefSpecs).AddRange((List<XRefSpec>)(od.Properties.XRefSpecs));
                                    ((List<XRefSpec>)model.Properties.ExternalXRefSpecs).AddRange((List<XRefSpec>)(od.Properties.ExternalXRefSpecs));
                                }
                            }
                        }
                    }

                    // 1. Validate schema after the merge
                    ((SchemaDrivenDocumentProcessor)host.Processor).SchemaValidator.Validate(model.Content);
                    // 2. Reexport xrefspec after the merge
                    SchemaProcessor.Process(host, model, _xrefSpecUpdater);
                }
            }
        }

        private object BuildOverwriteWithSchema(FileModel model, OverwriteDocumentModel overwrite, IHostService host, BaseSchema schema)
        {
            dynamic overwriteObject = ConvertToObjectHelper.ConvertToDynamic(overwrite.Metadata);
            overwriteObject.uid = overwrite.Uid;
            var overwriteModel = new FileModel(model.FileAndType, overwriteObject, model.OriginalFileAndType);
            var context = new ProcessContext(host, overwriteModel);
            context.Properties.Uids = new List<UidDefinition>();
            context.Properties.UidLinkSources = new Dictionary<string, List<LinkSourceInfo>>();
            context.Properties.FileLinkSources = new Dictionary<string, List<LinkSourceInfo>>();
            context.Properties.Dependency = new HashSet<string>();
            context.Properties.XRefSpecs = new List<XRefSpec>();
            context.Properties.ExternalXRefSpecs = new List<XRefSpec>();
            context.Properties.ContentOriginalFile = context.Model.OriginalFileAndType;
            var status = new AnchorContentStatus
            {
                ContainsAnchor = false,
                Content = overwrite.Conceptual
            };
            context.Properties.AnchorContentStatus = status;
            var transformed = _overwriteProcessor.Process(overwriteObject, schema, context) as IDictionary<string, object>;
            if (!status.ContainsAnchor)
            {
                transformed[AnchorContentStatus.DefaultContentPropertyName] = status.Content;
            }

            // TODO: add SouceDetail back to transformed

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
            return transformed;
        }

        #region ISupportIncrementalBuildStep Members

        public bool CanIncrementalBuild(FileAndType fileAndType) => true;

        public string GetIncrementalContextHash() => null;

        public IEnumerable<DependencyType> GetDependencyTypesToRegister() => null;

        #endregion
    }

    internal sealed class MergeContext : IMergeContext
    {
        private readonly IReadOnlyDictionary<string, object> Data;

        public MergeContext(IMerger merger, IReadOnlyDictionary<string, object> data)
        {
            Merger = merger;
            Data = data;
        }

        public IMerger Merger { get; }

        public object this[string key]
        {
            get
            {
                if (Data == null)
                {
                    return null;
                }
                Data.TryGetValue(key, out object result);
                return result;
            }
        }
    }
}
