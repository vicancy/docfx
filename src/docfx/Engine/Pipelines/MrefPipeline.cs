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
    internal static class BuildPipelineExtension
    {
        public static Task ReportLoaded(this BuildPipeline p)
        {
            return p.Report(Steps.ArticleLoaded);
        }

        public static Task ReportXRefSpecs(this BuildPipeline p, IEnumerable<XRefSpec> xrefspecs)
        {
            return p.Report(xrefspecs);
        }

        public static async Task<IEnumerable<XRefSpec>> RequireXRefSpecs(this BuildPipeline p, Context context, IEnumerable<FileAndType> files)
        {
            return (await p.Require<XRefSpec[]>(context, files.Distinct().ToArray())).SelectMany(s => s);
        }

        public static Task ReportTopicSave(this BuildPipeline p)
        {
            return p.Report(Steps.ArticleLoaded);
        }

        public static Task RequireTopicSave(this BuildPipeline p, Context context, params FileAndType[] files)
        {
            return p.Require(Steps.Saved, context, files);
        }
    }

    internal class MrefPipeline
    {
        private readonly Config _config;
        private readonly FileAndType _file;
        private readonly IDocumentProcessor _processor;

        public BuildPipeline Pipeline { get; }

        public MrefPipeline(BuildController controller, FileAndType file, Config config, IDocumentProcessor processor)
        {
            _config = config;
            _file = file;
            _processor = processor;
            Pipeline = new BuildPipeline(controller, new[]
                    {
                        Steps.ArticleLoaded, Steps.XrefmapExported, Steps.Saved
                    },
                    new Type[] { typeof(XRefSpec[]) },
                    BuildDocument);
        }

        private async Task BuildDocument(BuildPipeline p, Context context)
        {
            using (new LoggerFileScope(_file.File))
            {
                var destFile = ((RelativePath)_file.DestFile).RemoveWorkingFolder();

                var mta = Utility.ApplyFileMetadata(_file.File, _config.Metadata, _config.FileMetadata);
                var fileModel = _processor.Load(_file, mta);
                var pageModel = (PageViewModel)fileModel.Content;

                await p.Report(Steps.ArticleLoaded);

                var xrefspecs = pageModel.Items.SelectMany(s => GetXRefInfo(s, _file.Key, pageModel.References, destFile)).ToArray();

                await p.ReportXRefSpecs(xrefspecs);

                var linkToUids = BuildAndExtractUidDependencies(fileModel);

                // wait for the dependent uids to complete
                using (new LoggerPhaseScope($"Dedendencies({linkToUids.Count}).ExportXrefMap", LogLevel.Info))
                {
                    var files = linkToUids.SelectMany(s => GetUids(s, context));
                    var xrefs = await p.RequireXRefSpecs(context, files);

                    // for later template apply
                    _config.DBC.XRefSpecMap = new ConcurrentDictionary<string, XRefSpec>(xrefs.Select(s => new KeyValuePair<string, XRefSpec>(s.Uid, s)));
                }

                // This one can be parallel to export xrefmap
                using (new LoggerPhaseScope("CalcMetadata"))
                {
                    // wait for toc's loaded

                    // No need to if _tocRel is set
                    if (!pageModel.Metadata.TryGetValue("_tocRel", out var val))
                    {
                        pageModel.Metadata["_tocRel"] = null;
                        var invertedTocMap = await p.Require<InvertedTocMap>(context, FileAndType.AllToc);

                        var uids = new HashSet<string>(fileModel.Uids.Select(s => s.Name));

                        using (new LoggerPhaseScope("CalcNearestToc"))
                        {
                            if (invertedTocMap.TryGetValue(_file.Key, out var tocs))
                            {
                                var parentTocFiles = tocs.Select(s => new FileInfos(s, (RelativePath)context.FileMapping[s].DestFile));
                                var nearestToc = GetNearestToc(parentTocFiles, destFile.RemoveWorkingFolder());
                                if (nearestToc != null)
                                {
                                    pageModel.Metadata["_tocRel"] = (string)nearestToc.File.RemoveWorkingFolder().MakeRelativeTo(destFile);
                                    Logger.LogDiagnostic($"It's calculated nearest toc is {nearestToc.Key}");
                                    
                                    // no need to wait for toc to go through as the client will request for the toc.json and that is the time for the toc to build
                                    //using (new LoggerPhaseScope("BuildNearestToc"))
                                    //{
                                    //    // dependent on toc's build through
                                    //    await p.RequireTopicSave(context, context.FileMapping[nearestToc.Key]);
                                    //}
                                }
                            }
                        }
                    }

                    pageModel.Metadata["_rel"] = (string)(RelativePath.Empty).MakeRelativeTo(destFile);
                    pageModel.Metadata["document_type"] = "Reference";
                }

                _processor.Save(fileModel);

                // apply template
                using (new LoggerPhaseScope("ApplyTemplate"))
                {
                    var manifest = _config.TemplateProcessor.ProcessOne(fileModel, "ManagedReference", _config.ApplyTemplateSettings);
                    context.ManifestItems.Add(manifest);
                }

                await p.ReportTopicSave();
            }
        }


        private IReadOnlyCollection<string> BuildAndExtractUidDependencies(FileModel fileModel)
        {
            Logger.LogDiagnostic($"Processor {_processor.Name}: Building...");
            BuildPhaseUtility.RunBuildSteps(
                _processor.BuildSteps,
                buildStep =>
                {
                    buildStep.Build(fileModel, _config.HostService);
                });
            var linkToFiles = fileModel.LinkToFiles;
            var linkToUids = fileModel.LinkToUids;
            return linkToUids;
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

        private IEnumerable<XRefSpec> GetXRefInfo(ItemViewModel item, string key,
            List<ReferenceViewModel> references, RelativePath destFile)
        {
            var result = new XRefSpec
            {
                Uid = item.Uid,
                Name = item.Name,
                Href = (destFile.GetPathFromWorkingFolder()).UrlEncode().ToString(),
                CommentId = item.CommentId,
                ["key"] = key,
            };
            if (item.Names.Count > 0)
            {
                foreach (var pair in item.Names)
                {
                    result["name." + pair.Key] = pair.Value;
                }
            }
            if (!string.IsNullOrEmpty(item.FullName))
            {
                result["fullName"] = item.FullName;
            }
            if (item.FullNames.Count > 0)
            {
                foreach (var pair in item.FullNames)
                {
                    result["fullName." + pair.Key] = pair.Value;
                }
            }
            if (!string.IsNullOrEmpty(item.NameWithType))
            {
                result["nameWithType"] = item.NameWithType;
            }
            if (item.NamesWithType.Count > 0)
            {
                foreach (var pair in item.NamesWithType)
                {
                    result["nameWithType." + pair.Key] = pair.Value;
                }
            }
            yield return result;
            // generate overload xref spec.
            if (item.Overload != null)
            {
                var reference = references.Find(r => r.Uid == item.Overload);
                if (reference != null && !reference.Reported)
                {
                    reference.Reported = true;
                    yield return GetXRefInfo(reference, key);
                }
            }
        }

        private static XRefSpec GetXRefInfo(ReferenceViewModel item, string key)
        {
            var result = GetXRefSpecFromReference(item);
            result.Href = ((RelativePath)key).UrlEncode().ToString();
            return result;
        }

        private static XRefSpec GetXRefSpecFromReference(ReferenceViewModel item)
        {
            var result = new XRefSpec
            {
                Uid = item.Uid,
                Name = item.Name,
                Href = item.Href,
                CommentId = item.CommentId,
                IsSpec = item.Specs.Count > 0,
            };
            if (item.NameInDevLangs.Count > 0)
            {
                foreach (var pair in item.NameInDevLangs)
                {
                    result["name." + pair.Key] = pair.Value;
                }
            }
            if (!string.IsNullOrEmpty(item.FullName))
            {
                result["fullName"] = item.FullName;
            }
            if (item.FullNameInDevLangs.Count > 0)
            {
                foreach (var pair in item.FullNameInDevLangs)
                {
                    result["fullName." + pair.Key] = pair.Value;
                }
            }
            if (!string.IsNullOrEmpty(item.NameWithType))
            {
                result["nameWithType"] = item.NameWithType;
            }
            if (item.NameWithTypeInDevLangs.Count > 0)
            {
                foreach (var pair in item.NameWithTypeInDevLangs)
                {
                    result["nameWithType." + pair.Key] = pair.Value;
                }
            }
            if (item.Additional != null)
            {
                foreach (var pair in item.Additional)
                {
                    if (pair.Value is string s)
                    {
                        result[pair.Key] = s;
                    }
                }
            }
            return result;
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

        private static FileInfos GetNearestToc(IEnumerable<FileInfos> tocFiles, RelativePath file)
        {
            if (tocFiles == null)
            {
                return null;
            }
            return (from toc in tocFiles
                    where toc.File != null
                    let relativePath = toc.File.RemoveWorkingFolder() - file
                    orderby relativePath.SubdirectoryCount, relativePath.ParentDirectoryCount
                    select toc)
                .FirstOrDefault();
        }

    }
}
