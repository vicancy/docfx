// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.DocAsCode.Build.ConceptualDocuments;
using Microsoft.DocAsCode.Build.Engine;
using Microsoft.DocAsCode.Build.ManagedReference;
using Microsoft.DocAsCode.Build.TableOfContents;
using Microsoft.DocAsCode.Common;
using Microsoft.DocAsCode.DataContracts.Common;
using Microsoft.DocAsCode.DataContracts.ManagedReference;
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
    internal class ConceptualPipeline
    {
        private readonly Config _config;
        private readonly FileAndType _file;
        private readonly IDocumentProcessor _processor;
        public BuildPipeline Pipeline { get; }
        public ConceptualPipeline(BuildController controller, FileAndType file, Config config, IDocumentProcessor processor)
        {
            _config = config;
            _file = file;
            _processor = processor;
            Pipeline = new BuildPipeline(controller, new[]
                    {
                        Steps.ArticleLoaded, Steps.XrefmapExported, Steps.Saved
                    },new Type[] { }, BuildDocument);
        }

        private async Task BuildDocument(BuildPipeline p, Context context)
        {
            using (new LoggerFileScope(_file.File))
            {
                var destFile = ((RelativePath)_file.DestFile).RemoveWorkingFolder();

                using (new LoggerPhaseScope("BuildOutAllTocs"))
                {
                    // Wait for all toc's AST loaded
                    await p.Require(Steps.TocLoaded, context, context.Tocs.Values.ToArray());
                }

                var mta = Utility.ApplyFileMetadata(_file.File, _config.Metadata, _config.FileMetadata);
                var fileModel = _processor.Load(_file, mta);
                Logger.LogDiagnostic($"Processor {_processor.Name}: Building...");
                BuildPhaseUtility.RunBuildSteps(
                    _processor.BuildSteps,
                    buildStep =>
                    {
                        buildStep.Build(fileModel, _config.HostService);
                    });

                await p.Report(Steps.ArticleLoaded);

                if (fileModel.Properties.XrefSpec != null)
                {
                    var xrefspecs = ImmutableArray.Create(fileModel.Properties.XrefSpec);
                    await p.Report(xrefspecs);
                }

                await p.Report(Steps.XrefmapExported);

                var linkToFiles = fileModel.LinkToFiles;
                var linkToUids = fileModel.LinkToUids;

                // wait for the dependent uids to complete
                using (new LoggerPhaseScope($"Dedendencies({linkToUids.Count}).ExportXrefMap", LogLevel.Info))
                {
                    var xrefs = await p.Require<XRefSpec[]>(context, linkToUids.SelectMany(s => GetUids(s, context)).ToArray());
                    _config.DBC.XRefSpecMap = new ConcurrentDictionary<string, XRefSpec>(xrefs.SelectMany(s => s).Select(s => new KeyValuePair<string, XRefSpec>(s.Uid, s)));
                }

                _processor.Save(fileModel);

                // apply template
                using (new LoggerPhaseScope("ApplyTemplate"))
                {
                    var manifest = _config.TemplateProcessor.ProcessOne(fileModel, "ManagedReference", _config.ApplyTemplateSettings);
                    context.ManifestItems.Add(manifest);
                }

                await p.Report(Steps.Saved);
            }
        }

        private IEnumerable<FileAndType> GetUids(string uid, Context context)
        {
            if (context.PossibleUidMapping.TryGetValue(uid, out var value))
            {
                foreach (var v in value)
                {
                    yield return context.FileMapping[v];
                }
            }
        }

        private sealed class FileInfos
        {
            public string Key { get; set; }
            public RelativePath File { get; set; }
            public FileInfos(string key, RelativePath file)
            {
                Key = key;
                File = file;
            }
        }
    }
}
