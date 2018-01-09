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
    internal class TocFileBuild : IBuildStep
    {
        private readonly Config _config;
        private readonly FileAndType _file;
        IDocumentProcessor _processor;
        private FileModel _fm;
        private TocItemViewModel Content;
        private TaskRegister _tr = new TaskRegister();
        public bool Completed { get; private set; }

        public TocFileBuild(FileAndType file, Config config, IDocumentProcessor processor)
        {
            _config = config;
            _file = file;
            _processor = processor;
        }

        public Task Load(Context context)
        {
            return _tr.RegisterAndCall(nameof(Load),
                   async () =>
                   {
                       using (new LoggerFileScope(_file.File))
                       {
                           Logger.LogDiagnostic($"Processor {_processor.Name}: Building...");
                           var mta = Utility.ApplyFileMetadata(_file.File, _config.Metadata, _config.FileMetadata);
                           _fm = _processor.Load(_file, mta);
                           Content = (TocItemViewModel)_fm.Content;

                           // get links and uids mapping
                           var dep = new Dependencies
                           {
                               SourceFile = _file,
                           };
                           GoThrough(Content, _file, dep, context);
                           // TODO: include
                           // TODO: xref dependencies
                           BuildPhaseUtility.RunBuildSteps(
                               _processor.BuildSteps,
                               buildStep =>
                               {
                                   Logger.LogDiagnostic($"Processor {_processor.Name}, step {buildStep.Name}: Building...");
                                   using (new LoggerPhaseScope(buildStep.Name, LogLevel.Diagnostic))
                                   {
                                       buildStep.Build(_fm, _config.HostService);
                                   }
                               });

                       }
                   });
        }

        public Task ExportXrefMap(Context context)
        {
            using (new LoggerFileScope(_file.File))
            {
                return Load(context);
            }
        }

        private IEnumerable<string> GetUids(string uid, Context context)
        {
            if (context.PossibleUidMapping.TryGetValue(uid, out var value))
            {
                foreach (var v in value)
                {
                    yield return v;
                }
            }
        }

        public Task Build(Context context)
        {
            return _tr.RegisterAndCall(nameof(Build),
                   async () =>
                   {
                       using (new LoggerFileScope(_file.File))
                       {
                           await Load(context);
                           await ExportXrefMap(context);

                           var linkToFiles = _fm.LinkToFiles;
                           var linkToUids = _fm.LinkToUids;
                           // wait for the dependent uids to complete
                           using (new LoggerPhaseScope($"Dedendencies({linkToUids.Count}).ExportXrefMap", LogLevel.Info))
                           {
                               await Task.WhenAll(
                                   linkToUids.SelectMany(s => GetUids(s, context)).Select(s => Utility.CreateOrGetOneTask(context.FileMapping[s], context, _config).ExportXrefMap(context))
                                   );
                           }
                           // update href
                           ResolveUid(Content, context);
                           var result = _processor.Save(_fm);
                           if (result != null)
                           {
                               // apply template
                               //var manifest = _config.TemplateProcessor.ProcessOne(_fm, result.DocumentType, _config.ApplyTemplateSettings);
                               //context.ManifestItems.Add(manifest);
                           }
                           Content = null;
                           _fm = null;
                           Completed = true;
                       }
                   });
        }

        private void ResolveUid(TocItemViewModel item, Context context)
        {
            if (item.TopicUid != null)
            {
                if (!context.XrefSpecMapping.TryGetValue(item.TopicUid, out var xref))
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
                    ResolveUid(i, context);
                }
            }
        }
        private void GoThrough(TocItemViewModel item, FileAndType file, Dependencies dep, Context context)
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
                        var pt = new PossibleToc { TocKey = file.Key };
                        context.FilePossibleTocMapping.AddOrUpdate(path, s => new ConcurrentBag<PossibleToc> { pt }, (k, v) => { v.Add(pt); return v; });
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
                    var pt = new PossibleToc { Uid = item.TopicUid, TocKey = file.Key };
                    foreach (var f in files)
                    {
                        context.FilePossibleTocMapping.AddOrUpdate(f, s => new ConcurrentBag<PossibleToc> { pt }, (k, v) => { v.Add(pt); return v; });
                    }
                }
            }


            if (item.Items != null)
            {
                foreach(var ii in item.Items)
                {
                    GoThrough(ii, file, dep, context);
                }
            }
        }
    }
}
