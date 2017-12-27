// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.DocAsCode.Build.ConceptualDocuments;
using Microsoft.DocAsCode.Build.Engine;
using Microsoft.DocAsCode.Build.ManagedReference;
using Microsoft.DocAsCode.Build.TableOfContents;
using Microsoft.DocAsCode.Common;
using Microsoft.DocAsCode.Exceptions;
using Microsoft.DocAsCode.Plugins;
using Newtonsoft.Json;
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
    internal class EngineVNext
    {
        private readonly Config _config;
        private static HashSet<string> _allowedToc = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "toc.md", "toc.yml", "toc.yaml", "toc.markdown" };
        private static HashSet<string> _allowedYaml = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".yml", ".yaml"};
        public EngineVNext(Config config)
        {
            _config = config;
        }

        public async Task Build(FileCollection globalScopeFiles, FileCollection inScopeFiles, string outputFolder)
        {
            Logger.LogInfo("start");
            var context = new Context();

            // 1. Get files, quick scan
            using (new LoggerPhaseScope("QuickScan", LogLevel.Info))
            {
                await QuickScan(globalScopeFiles, context);
                Logger.LogInfo($"{context.FileMapping.Count} files mapping created.");
            }

            using (new LoggerPhaseScope("Build", LogLevel.Info))
            {
                await Task.WhenAll(inScopeFiles.EnumerateFiles().ForEachInParallelAsync(s =>
                {
                    var step = Utility.CreateOrGetOneTask(s, context, _config);

                    return step.Build(context);

                }, 64));
                Logger.LogInfo($"{context.FileStepMapping.Count} files task mapping executed.");
            }

            using (new LoggerPhaseScope("DumpContext", LogLevel.Info))
            {
                var path = outputFolder + "/context.json";
                JsonUtility.Serialize(path, context, Formatting.Indented);
                Logger.LogInfo($"Context saved to {path}.");
            }

            // _config.TemplateProcessor.ProcessDependencies(new HashSet<string> { "Conceptual" }, _config.ApplyTemplateSettings);
        }

        private async Task QuickScan(FileCollection files, Context context)
        {
            foreach(var f in files.EnumerateFiles())
            {
                var uids = OneFileQuickScan(f);
                if (f.IsToc)
                {
                    context.Tocs.TryAdd(f.Key, f);
                }
                context.FileMapping[f.Key] = f;
                foreach(var uid in uids)
                {
                    if (context.PossibleUidMapping.TryGetValue(uid, out var list))
                    {
                        list.Add(f.Key);
                    }
                    else
                    {
                        context.PossibleUidMapping[uid] = new List<string> { f.Key };
                    }
                }
            }
            // await files.EnumerateFiles().ForEachInParallelAsync(s => OneFileQuickScan(s, context));
        }

        private IEnumerable<string> OneFileQuickScan(FileAndType file)
        {
            IEnumerable<string> uids = Enumerable.Empty<string>();
            if (file.Type == DocumentType.Article)
            {
                if (_allowedToc.Contains(Path.GetFileName(file.File)))
                {
                    file.DestFile = Path.ChangeExtension(file.File, ".json");
                    file.UrlExtension = ".json";
                    file.IsToc = true;
                }
                else
                {
                    file.DestFile = Path.ChangeExtension(file.File, _config.pageFileExtension);
                    file.UrlExtension = _config.pageUrlExtension;
                    if (_allowedYaml.Contains(Path.GetExtension(file.File)))
                    {
                        uids = QuickScanUids(file.File);
                    }
                }
            }
            else if (file.Type == DocumentType.Resource)
            {
                file.DestFile = file.File;
            }

            return uids;
        }

        private async Task OneFileQuickScanAsync(FileAndType file, Context context)
        {
            IEnumerable<string> uids = Enumerable.Empty<string>();
            if (file.Type == DocumentType.Article)
            {
                if (_allowedToc.Contains(Path.GetFileName(file.File)))
                {
                    file.DestFile = Path.ChangeExtension(file.File, ".json");
                    file.UrlExtension = ".json";
                    context.Tocs.TryAdd(file.Key, file);
                }
                else
                {
                    file.DestFile = Path.ChangeExtension(file.File, _config.pageFileExtension);
                    file.UrlExtension = _config.pageUrlExtension;
                    if (_allowedYaml.Contains(Path.GetExtension(file.File)))
                    {
                        uids = QuickScanUids(file.File);
                    }
                }
            }
            else if (file.Type == DocumentType.Resource)
            {
                file.DestFile = file.File;
            }

            foreach(var uid in uids)
            {
                // context.PossibleUidMapping[uid] = new List<string> { file.Key };
                // var list = context.PossibleUidMapping.GetOrAdd(uid, new List<string>());
                // list.Add(file.Key);
                // context.PossibleUidMapping.AddOrUpdate(uid, new List<string> { file.Key }, (k, v) => { v.Add(file.Key); return v; });
            }

            context.FileMapping.TryAdd(file.Key, file);
        }

        private static readonly Regex UidMatcher = new Regex(@"^\s*-?\s+uid:\s*(.*)$", RegexOptions.Compiled);

        private IEnumerable<string> QuickScanUids(string path)
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line == "references:")
                {
                    yield break;
                }
                else if (UidMatcher.IsMatch(line))
                {
                    yield return UidMatcher.Match(line).Groups[1].Value;
                }
                
            }
        }
    }

    internal interface IBuildStep
    {
        Task Build(Context context);
        Task Load(Context context);
        Task ExportXrefMap(Context context);
    }

    internal class IdleStep : IBuildStep
    {
        public static IdleStep Default = new IdleStep();
        public Task Build(Context context)
        {
            return Task.CompletedTask;
        }

        public Task ExportXrefMap(Context context)
        {
            return Task.CompletedTask;
        }

        public Task Load(Context context)
        {
            return Task.CompletedTask;
        }
    }
}
