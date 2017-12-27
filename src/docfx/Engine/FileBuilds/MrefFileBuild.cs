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
    internal class MrefFileBuild : IBuildStep
    {
        private readonly Config _config;
        private readonly FileAndType _file;
        IDocumentProcessor _processor;
        private FileModel _fm;
        private TaskRegister _tr = new TaskRegister();
        private PageViewModel _model;
        public MrefFileBuild(FileAndType file, Config config, IDocumentProcessor processor)
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
                    using (new LoggerFileScope(_file.FullPath))
                    {
                        using (new LoggerPhaseScope("BuildOutAllTocs"))
                        {        
                            // Wait for all toc's AST loaded
                            await Task.WhenAll(
                            context.Tocs.Keys.Select(s => Utility.CreateOrGetOneTask(context.Tocs[s], context, _config).Load(context))
                            );
                        }

                        var mta = Utility.ApplyFileMetadata(_file.File, _config.Metadata, _config.FileMetadata);
                        _fm = _processor.Load(_file, mta);
                        _model = (PageViewModel)_fm.Content;
                    }
                }
                );
        }


        private static IEnumerable<XRefSpec> GetXRefInfo(ItemViewModel item, string key,
            List<ReferenceViewModel> references)
        {
            var result = new XRefSpec
            {
                Uid = item.Uid,
                Name = item.Name,
                Href = ((RelativePath)key).UrlEncode().ToString(),
                CommentId = item.CommentId,
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
                if (reference != null)
                {
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

        public Task ExportXrefMap(Context ctxt)
        {
            return _tr.RegisterAndCall(nameof(ExportXrefMap),
                async () =>
                {
                    await Load(ctxt);

                    // export xrefmap
                    foreach (var i in _model.Items.SelectMany(s => GetXRefInfo(s, _file.Key, _model.References)))
                    {
                        ctxt.XrefSpecMapping[i.Uid] = i;
                    }
                    // TODO: external xref map needed?
                });
        }

        private Task CalcNearestToc(Context context)
        {
            var uids = new HashSet<string>(_fm.Uids.Select(s => s.Name));

            // get nearest toc
            FileInfos nearestToc = null;
            using (new LoggerPhaseScope("CalcNearestToc"))
            {
                if (context.FilePossibleTocMapping.TryGetValue(_file.Key, out var tocs))
                {
                    var parentTocFiles = tocs.Where(s => s.ForSure || uids.Contains(s.Uid)).Select(s => new FileInfos(s.TocKey, (RelativePath)context.FileMapping[s.TocKey].DestFile));
                    nearestToc = GetNearestToc(parentTocFiles, (RelativePath)_file.DestFile);

                    Logger.LogDiagnostic($"It's calculated nearest toc is {nearestToc.Key}");
                }
            }

            // dependent on toc's build through
            if (nearestToc != null)
            {
                using (new LoggerPhaseScope("BuildNearestToc"))
                {
                    return context.FileStepMapping[nearestToc.Key].Build(context);
                }
            }

            return Task.CompletedTask;
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
                       await Load(context);
                       await ExportXrefMap(context);

                       Logger.LogDiagnostic($"Processor {_processor.Name}: Building...");
                       BuildPhaseUtility.RunBuildSteps(
                           _processor.BuildSteps,
                           buildStep =>
                           {
                               buildStep.Build(_fm, _config.HostService);
                           });
                       var linkToFiles = _fm.LinkToFiles;
                       var linkToUids = _fm.LinkToUids;

                       // wait for the dependent uids to complete
                       await Task.WhenAll(
                           linkToUids.SelectMany(s => GetUids(s, context)).Select(s => Utility.CreateOrGetOneTask(context.FileMapping[s], context, _config).ExportXrefMap(context))
                           .Concat(new Task[] { Task.FromResult(CalcNearestToc(context)) })
                           );
                       _processor.Save(_fm);
                       _config.DBC.XRefSpecMap = context.XrefSpecMapping;
                       // apply template
                       using (new LoggerPhaseScope("ApplyTemplate"))
                       {
                           var manifest = _config.TemplateProcessor.ProcessOne(_fm, "ManagedReference", _config.ApplyTemplateSettings);
                           context.ManifestItems.Add(manifest);
                       }
                   });
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
