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
    internal class TocMap {
        public FileAndType Toc { get; set; }
        public HashSet<string> LinkedTopics { get; set; }
    }
    internal class TocPipeline
    {
        private readonly Config _config;
        private readonly FileAndType _file;
        private readonly IDocumentProcessor _processor;

        public BuildPipeline Pipeline { get; }

        public TocPipeline(BuildController controller, FileAndType file, Config config, IDocumentProcessor processor)
        {
            _config = config;
            _file = file;
            _processor = processor;
            Pipeline = new BuildPipeline(controller, new[]
                    {
                        Steps.TocLoaded, Steps.Saved
                    },new Type[] { typeof(TocMap)}, BuildDocument);
        }

        private async Task BuildDocument(BuildPipeline p, Context context)
        {
            using (new LoggerFileScope(_file.File))
            {
                Logger.LogDiagnostic($"Processor {_processor.Name}: Building...");
                var mta = Utility.ApplyFileMetadata(_file.File, _config.Metadata, _config.FileMetadata);
                var fileModel = _processor.Load(_file, mta);
                var model = (TocItemViewModel)fileModel.Content;

                // get links and uids mapping
                var dep = new Dependencies
                {
                    SourceFile = _file,
                };

                // Use uid-possible path mapping for tocmap
                var paths = new HashSet<string>(FilePathComparer.OSPlatformSensitiveStringComparer);
                GoThrough(model, _file, dep, context, paths);

                var tocmap = new TocMap
                {
                    Toc = _file,
                    LinkedTopics = paths,
                };

                await p.Report(tocmap);

                // TODO: include
                // TODO: xref dependencies
                BuildPhaseUtility.RunBuildSteps(
                    _processor.BuildSteps,
                    buildStep =>
                    {
                        Logger.LogDiagnostic($"Processor {_processor.Name}, step {buildStep.Name}: Building...");
                        using (new LoggerPhaseScope(buildStep.Name, LogLevel.Diagnostic))
                        {
                            buildStep.Build(fileModel, _config.HostService);
                        }
                    });
                await p.Report(Steps.TocLoaded);

                var linkToFiles = fileModel.LinkToFiles;
                var linkToUids = fileModel.LinkToUids;
                // wait for the dependent uids to complete
                using (new LoggerPhaseScope($"Dedendencies({linkToUids.Count}).ExportXrefMap"))
                {
                    var xrefs = await p.Require<XRefSpec[]>(context, linkToUids.SelectMany(s => GetUids(s, context)).ToArray());
                   
                    ResolveUid(model, xrefs.SelectMany(s => s).ToDictionary(s => s.Uid, s => s));
                }

                // update href
                var result = _processor.Save(fileModel);
                if (result != null)
                {
                    // apply template
                    var manifest = _config.TemplateProcessor.ProcessOne(fileModel, result.DocumentType, _config.ApplyTemplateSettings);
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

        private void ResolveUid(TocItemViewModel item, Dictionary<string, XRefSpec> xrefs)
        {
            var hrefType = Utility.GetHrefType(item.TopicHref);
            if (hrefType == HrefType.RelativeFile)
            {
                var path = UriUtility.GetPath(item.TopicHref);
                var relativePath = ((RelativePath)path).UrlDecode();
            }

            if (item.TopicUid != null)
            {
                if (!xrefs.TryGetValue(item.TopicUid, out var xref))
                {
                    return;
                }
                if (xref != null)
                {
                    item.Href = item.TopicHref = xref.Href;
                    if (string.IsNullOrEmpty(item.Name))
                    {
                        item.Name = xref.Name;
                    }

                    if (string.IsNullOrEmpty(item.NameForCSharp) && xref.TryGetValue("name.csharp", out string nameForCSharp))
                    {
                        item.NameForCSharp = nameForCSharp;
                    }
                    if (string.IsNullOrEmpty(item.NameForVB) && xref.TryGetValue("name.vb", out string nameForVB))
                    {
                        item.NameForVB = nameForVB;
                    }
                }
            }

            if (item.Items != null)
            {
                foreach (var i in item.Items)
                {
                    ResolveUid(i, xrefs);
                }
            }
        }

        private void GoThrough(TocItemViewModel item, FileAndType file, Dependencies dep, Context context, HashSet<string> containedPathKeys)
        {
            if (item == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(item.Uid))
            {
                dep.Xrefs.Add(item.Uid);
            }

            // HomepageUid and Uid is deprecated, unified to TopicUid
            if (string.IsNullOrEmpty(item.TopicUid))
            {
                if (!string.IsNullOrEmpty(item.Uid))
                {
                    item.TopicUid = item.Uid;
                    item.Uid = null;
                }
                else if (!string.IsNullOrEmpty(item.HomepageUid))
                {
                    item.TopicUid = item.HomepageUid;
                    Logger.LogWarning($"HomepageUid is deprecated in TOC. Please use topicUid to specify uid {item.Homepage}");
                    item.HomepageUid = null;
                }
            }
            // Homepage is deprecated, unified to TopicHref
            if (!string.IsNullOrEmpty(item.Homepage))
            {
                if (string.IsNullOrEmpty(item.TopicHref))
                {
                    item.TopicHref = item.Homepage;
                }
                else
                {
                    Logger.LogWarning($"Homepage is deprecated in TOC. Homepage {item.Homepage} is overwritten with topicHref {item.TopicHref}");
                }
            }

            // TocHref supports 2 forms: absolute path and local toc file.
            // When TocHref is set, using TocHref as Href in output, and using Href as Homepage in output

            var hrefType = Utility.GetHrefType(item.Href);
            switch (hrefType)
            {
                case HrefType.AbsolutePath:
                    break;
                case HrefType.RelativeFile:
                    {
                        var path = ((RelativePath)file.File + (RelativePath)item.Href).GetPathFromWorkingFolder();
                        dep.Links.Add(path);
                        containedPathKeys.Add(path);
                        break;
                    }
                case HrefType.RelativeFolder:
                    {
                        var path = ((RelativePath)file.File + (RelativePath)item.Href).GetPathFromWorkingFolder();
                        // get possible includes
                        dep.Includes.Add(path + "/toc.md"); 
                        dep.Includes.Add(path + "/toc.yml");
                        break;
                    }
                case HrefType.MarkdownTocFile:
                case HrefType.YamlTocFile:
                    {
                        var path = ((RelativePath)file.File + (RelativePath)item.Href).GetPathFromWorkingFolder();
                        dep.Includes.Add(path);
                    }
                    break;
                default:
                    break;
            }


            if (!string.IsNullOrEmpty(item.TopicUid))
            {
                dep.Xrefs.Add(item.TopicUid);
                if (context.PossibleUidMapping.TryGetValue(item.TopicUid, out var files))
                {
                    foreach(var f in files)
                    {
                        containedPathKeys.Add(f);
                    }
                }
            }


            if (item.Items != null)
            {
                foreach(var ii in item.Items)
                {
                    GoThrough(ii, file, dep, context, containedPathKeys);
                }
            }
        }
    }
}
