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
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.DocAsCode
{
    internal static class GloballySharedContext
    {
        public static EngineVNext Engine { get; set; }
    }

    internal class EngineVNext
    {
        private readonly Config _config;
        private static HashSet<string> _allowedToc = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "toc.md", "toc.yml", "toc.yaml", "toc.markdown", "toc.json" };
        private static HashSet<string> _allowedYaml = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".yml", ".yaml", ".json"};
        public EngineVNext(Config config)
        {
            _config = config;
        }

        private Context _context = new Context();

        public async Task BuildInscope(IEnumerable<FileAndType> globalScopeFiles, FileCollection inScopeFiles, int count)
        {
            var context = _context;

            // 1. Get files, quick scan
            using (new LoggerPhaseScope("QuickScan", LogLevel.Info))
            {
                Logger.LogInfo($"Quick scanning {count} files.");
                await QuickScanAsync(globalScopeFiles, context);
                Logger.LogInfo($"{context.FileMapping.Count} files mapping created.");
            }

            using (new LoggerPhaseScope("Build", LogLevel.Info))
            {
                await Task.WhenAll((inScopeFiles?.EnumerateFiles() ?? context.FileMapping.Values).ForEachInParallelAsync(s =>
                {
                    var step = Utility.CreateOrGetOneTask(s, context, _config);

                    return step.Build(context);

                }, 64));
                Logger.LogInfo($"{context.FileStepMapping.Count} files task mapping executed.");
            }
        }

        public async Task Build(IEnumerable<FileAndType> globalScopeFiles, FileCollection inScopeFiles, string outputFolder, int count)
        {
            GloballySharedContext.Engine = this;
            var context = _context;

            await BuildInscope(globalScopeFiles, inScopeFiles, count);

            using (new LoggerPhaseScope("DumpContext", LogLevel.Info))
            {
                var path = outputFolder + "/context.json";
                JsonUtility.Serialize(path, context, Formatting.Indented);
                Logger.LogInfo($"Context saved to {path}.");
            }

            // _config.TemplateProcessor.ProcessDependencies(new HashSet<string> { "Conceptual" }, _config.ApplyTemplateSettings);
        }

        private async Task QuickScan(IEnumerable<FileAndType> files, Context context)
        {
            foreach(var f in files)
            {
                var uids = OneFileQuickScan(f);
                if (f.IsToc)
                {
                    context.Tocs.TryAdd(f.Key, f);
                }
                context.FileMapping[f.Key] = f;
                foreach (var uid in uids)
                {
                    if (context.PossibleUidMapping.TryGetValue(uid, out var list))
                    {
                        list.Add(f.Key);
                    }
                    else
                    {
                        context.PossibleUidMapping[uid] = new HashSet<string> { f.Key };
                    }
                }
            }
            // await files.EnumerateFiles().ForEachInParallelAsync(s => OneFileQuickScan(s, context));
        }

        private async Task QuickScanAsync(IEnumerable<FileAndType> files, Context context)
        {
            var results = await files.SelectInParallelAsync(OneJsonFileQuickScanAsync, 16);
            foreach (var r in results)
            {
                var f = r.File;
                if (r.File.IsToc)
                {
                    context.Tocs[r.File.Key] = r.File;
                }
                context.FileMapping[r.File.Key] = r.File;
                foreach (var uid in r.Uids)
                {
                    if (context.PossibleUidMapping.TryGetValue(uid, out var list))
                    {
                        list.Add(f.Key);
                    }
                    else
                    {
                        context.PossibleUidMapping[uid] = new HashSet<string> { f.Key };
                    }
                }
            }
        }

        private void OneFileQuickScanTest(FileAndType file)
        {
            IEnumerable<string> uids = Enumerable.Empty<string>();
            if (file.Type == DocumentType.Article)
            {
                if (_allowedToc.Contains(Path.GetFileName(file.File)))
                {
                    file.DestFile = Path.ChangeExtension(file.File, ".json");
                    file.DestUrl = file.DestFile;
                    file.IsToc = true;
                }
                else
                {
                    file.DestFile = Path.ChangeExtension(file.File, _config.pageFileExtension);
                    file.DestUrl = Path.ChangeExtension(file.File, _config.pageUrlExtension);
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
        }

        private IEnumerable<string> OneFileQuickScan(FileAndType file)
        {
            IEnumerable<string> uids = Enumerable.Empty<string>();
            if (file.Type == DocumentType.Article)
            {
                if (_allowedToc.Contains(Path.GetFileName(file.File)))
                {
                    file.DestFile = Path.ChangeExtension(file.File, ".json");
                    file.DestUrl = file.DestFile;
                    file.IsToc = true;
                }
                else
                {
                    file.DestFile = Path.ChangeExtension(file.File, _config.pageFileExtension);
                    file.DestUrl = Path.ChangeExtension(file.File, _config.pageUrlExtension);
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

        class FileScanResult
        {
            public IEnumerable<string> Uids { get; set; }
            public FileAndType File { get; set; }
        }

        private async Task<FileScanResult> OneJsonFileQuickScanAsync(FileAndType file)
        {
            IEnumerable<string> uids = Enumerable.Empty<string>();
            if (file.Type == DocumentType.Article)
            {
                if (_allowedToc.Contains(Path.GetFileName(file.File)))
                {
                    file.DestFile = Path.ChangeExtension(file.File, ".json");
                    file.DestUrl = file.DestFile;
                    file.IsToc = true;
                }
                else
                {
                    file.DestFile = Path.ChangeExtension(file.File, _config.pageFileExtension);
                    file.DestUrl = Path.ChangeExtension(file.File, _config.pageUrlExtension);
                    if (_allowedYaml.Contains(Path.GetExtension(file.File)))
                    {
                        uids = await QuickScanUidsInJsonAsync(file.File);
                    }
                }
            }
            else if (file.Type == DocumentType.Resource)
            {
                file.DestFile = file.File;
            }

            return new FileScanResult { Uids = uids, File = file };
        }

        private async Task<FileScanResult> OneFileQuickScanAsync(FileAndType file)
        {
            IEnumerable<string> uids = Enumerable.Empty<string>();
            if (file.Type == DocumentType.Article)
            {
                if (_allowedToc.Contains(Path.GetFileName(file.File)))
                {
                    file.DestFile = Path.ChangeExtension(file.File, ".json");
                    file.DestUrl = file.DestFile;
                    file.IsToc = true;
                }
                else
                {
                    file.DestFile = Path.ChangeExtension(file.File, _config.pageFileExtension);
                    file.DestUrl = Path.ChangeExtension(file.File, _config.pageUrlExtension);
                    if (_allowedYaml.Contains(Path.GetExtension(file.File)))
                    {
                        uids = QuickScanUids(file.File).ToArray();
                    }
                }
            }
            else if (file.Type == DocumentType.Resource)
            {
                file.DestFile = file.File;
            }

            return new FileScanResult { Uids = uids, File = file };
        }

        private static readonly Regex UidMatcher = new Regex(@"^\s*-?\s+(uid|overload):\s*(.*)$", RegexOptions.Compiled);

        private IEnumerable<string> QuickScanUids(string path)
        {
            var uids = new List<string>();
            using (var reader = File.OpenText(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 11 &&
                        line[0] == 'r' && line[1] == 'e' && line[2] == 'f' && line[3] == 'e' && line[4] == 'r' && line[5] == 'e' &&
                        line[6] == 'n' && line[7] == 'c' && line[8] == 'e' && line[9] == 's' && line[10] == ':')
                    // line == "references:")
                    {
                        break;
                    }
                    else if (line.Length > 7 && line[0] == '-' && line[1] == ' ' && line[2] == 'u' && line[3] == 'i' && line[4] == 'd' && line[5] == ':' && line[6] == ' ') //line.StartsWith("- uid: "))
                    {
                        uids.Add(line.Substring(7));
                    }
                    else if (line.Length > 12 &&
                        line[0] == ' ' && line[1] == ' ' && line[2] == 'o' && line[3] == 'v' && line[4] == 'e' && line[5] == 'r' &&
                        line[6] == 'l' && line[7] == 'o' && line[8] == 'a' && line[9] == 'd' && line[10] == ':' && line[11] == ' ')
                    {
                        uids.Add(line.Substring(12));
                    }
                }
            }

            return uids;
        }

        private static readonly Regex UidJsonMatcher = new Regex(@"""uid"": ""(.*)"",$", RegexOptions.Compiled);

        private Task<List<string>> QuickScanUidsInJsonAsync(string path)
        {
            return Task.Run(()=> QuickScanUidsInJson(path));
        }

        private List<string> QuickScanUidsInJson(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096
                , FileOptions.SequentialScan))
            {
                var uids = new List<string>();
                using (var sr = new StreamReader(fs))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        var text = line.TrimStart();
                        if (text.StartsWith("\"references\":"))
                        {
                            break;
                        }
                        var uidStr = "\"uid\":";
                        if (text.StartsWith(uidStr))
                        {
                            var trimmed = text.Substring(uidStr.Length).Trim(' ', ',', '"');
                            uids.Add(trimmed);
                        }
                        var overloadStr = "\"overload\":";
                        if (text.StartsWith(overloadStr))
                        {
                            var trimmed = text.Substring(overloadStr.Length).Trim(' ', ',', '"');
                            uids.Add(trimmed);
                        }
                    }
                }

                return uids;
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
