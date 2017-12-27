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
    internal class TaskRegister
    {
        private Dictionary<string, Task> _tasks = new Dictionary<string, Task>();
        public Task RegisterAndCall(string name, Func<Task> task)
        {
            if (_tasks.TryGetValue(name, out var saved))
            {
                return saved;
            }
            saved = _tasks[name] = task();
            return saved;
        }
    }

    internal class ConceptualFileBuild : IBuildStep
    {
        private readonly Config _config;
        private readonly FileAndType _file;
        IDocumentProcessor _processor;
        private FileModel _fm;
        private TaskRegister _tr = new TaskRegister();

        public ConceptualFileBuild(FileAndType file, Config config, IDocumentProcessor processor)
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
                        Logger.LogDiagnostic($"Processor {_processor.Name}: Building...");
                        BuildPhaseUtility.RunBuildSteps(
                            _processor.BuildSteps,
                            buildStep =>
                            {
                                buildStep.Build(_fm, _config.HostService);
                            });
                    }
                }
                );
        }

        public Task ExportXrefMap(Context ctxt)
        {
            return _tr.RegisterAndCall(nameof(ExportXrefMap),
                async () =>
                {
                    await Load(ctxt);

                    // export xrefmap
                    if (_fm.Properties.XrefSpec != null)
                    {
                        ctxt.XrefSpecMapping[_file.Key] = ImmutableArray.Create(_fm.Properties.XrefSpec);
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

                       var linkToFiles = _fm.LinkToFiles;
                       var linkToUids = _fm.LinkToUids;

                       // wait for the dependent uids to complete
                       await Task.WhenAll(
                           linkToUids.SelectMany(s => GetUids(s, context)).Select(s => Utility.CreateOrGetOneTask(context.FileMapping[s], context, _config).Load(context))
                           .Concat(new Task[] { Task.FromResult(CalcNearestToc(context)) })
                           );
                       var result = _processor.Save(_fm);
                       if (result != null)
                       {
                           // apply template
                           using (new LoggerPhaseScope("ApplyTemplate"))
                           {
                               var manifest = _config.TemplateProcessor.ProcessOne(_fm, result.DocumentType, _config.ApplyTemplateSettings);
                               context.ManifestItems.Add(manifest);
                           }
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
