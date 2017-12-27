// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.Engine
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Composition;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;

    using HtmlAgilityPack;

    using Microsoft.DocAsCode.Build.Engine.Incrementals;
    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.MarkdownLite;
    using Microsoft.DocAsCode.Plugins;

    internal sealed class HostServiceVNext : IHostService, IDisposable
    {
        #region Fields
        private readonly object _syncRoot = new object();
        private readonly object _tocSyncRoot = new object();
        private readonly Dictionary<string, List<FileModel>> _uidIndex = new Dictionary<string, List<FileModel>>();
        private readonly LruList<ModelWithCache> _lru;
        #endregion

        #region Properties

        public TemplateProcessor Template { get; set; }

        public ImmutableDictionary<string, FileAndType> SourceFiles { get; set; }

        public ImmutableDictionary<string, FileIncrementalInfo> IncrementalInfos { get; set; }

        public Dictionary<FileAndType, FileAndType> FileMap { get; } = new Dictionary<FileAndType, FileAndType>();

        public IMarkdownService MarkdownService { get; set; }

        public ImmutableList<IInputMetadataValidator> Validators { get; set; }

        public DependencyGraph DependencyGraph { get; set; }

        public bool ShouldTraceIncrementalInfo { get; set; }

        public bool CanIncrementalBuild { get; set; }

        public ImmutableList<TreeItemRestructure> TableOfContentRestructions { get; set; }

        public string VersionName { get; }

        public string VersionOutputFolder { get; }

        public GroupInfo GroupInfo { get; }

        #endregion

        #region Constructors

        public HostServiceVNext(string baseDir, string versionName, string versionDir, int lruSize, GroupInfo groupInfo)
        {
            VersionName = versionName;
            VersionOutputFolder = versionDir;
            GroupInfo = groupInfo;
        }

        #endregion

        #region IHostService Members

        public IDocumentProcessor Processor { get; set; }

        public ImmutableList<FileModel> GetModels(DocumentType? type)
        {
            return ImmutableList.Create<FileModel>();
        }

        public ImmutableHashSet<string> GetAllUids()
        {
            lock (_syncRoot)
            {
                return _uidIndex.Keys.ToImmutableHashSet();
            }
        }

        public ImmutableList<FileModel> LookupByUid(string uid)
        {
            if (uid == null)
            {
                throw new ArgumentNullException(nameof(uid));
            }
            lock (_syncRoot)
            {
                if (_uidIndex.TryGetValue(uid, out List<FileModel> result))
                {
                    return result.ToImmutableList();
                }
                return ImmutableList<FileModel>.Empty;
            }
        }

        public MarkupResult Markup(string markdown, FileAndType ft)
        {
            if (markdown == null)
            {
                throw new ArgumentNullException(nameof(markdown));
            }
            if (ft == null)
            {
                throw new ArgumentNullException(nameof(ft));
            }
            return MarkupCore(markdown, ft, false);
        }

        public MarkupResult Markup(string markdown, FileAndType ft, bool omitParse)
        {
            if (markdown == null)
            {
                throw new ArgumentNullException(nameof(markdown));
            }
            if (ft == null)
            {
                throw new ArgumentNullException(nameof(ft));
            }
            return MarkupCore(markdown, ft, omitParse);
        }

        public MarkupResult Parse(MarkupResult markupResult, FileAndType ft)
        {
            return MarkupUtility.Parse(markupResult, ft.File, SourceFiles);
        }

        private MarkupResult MarkupCore(string markdown, FileAndType ft, bool omitParse)
        {
            try
            {
                var mr = MarkdownService.Markup(markdown, ft.File);
                if (omitParse)
                {
                    return mr;
                }
                return Parse(mr, ft);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.Fail("Markup failed!");
                var message = $"Markup failed: {ex.Message}.";
                Logger.LogError(message);
                throw new DocumentException(message, ex);
            }
        }

        public void ReportDependencyTo(FileModel currentFileModel, string to, string type)
        {
            ReportDependencyTo(currentFileModel, to, DependencyItemSourceType.File, type);
        }

        public void ReportDependencyTo(FileModel currentFileModel, string to, string toType, string type)
        {
            if (currentFileModel == null)
            {
                throw new ArgumentNullException(nameof(currentFileModel));
            }
            if (string.IsNullOrEmpty(to))
            {
                throw new ArgumentNullException(nameof(to));
            }
            if (toType == null)
            {
                throw new ArgumentNullException(nameof(toType));
            }
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }
            if (DependencyGraph == null)
            {
                return;
            }
            string fromKey = IncrementalUtility.GetDependencyKey(currentFileModel.OriginalFileAndType);
            string toKey = toType == DependencyItemSourceType.File ?
                IncrementalUtility.GetDependencyKey(currentFileModel.OriginalFileAndType.ChangeFile((RelativePath)currentFileModel.OriginalFileAndType.File + (RelativePath)to)) :
                to;
            ReportDependencyCore(fromKey, new DependencyItemSourceInfo(toType, toKey), fromKey, type);
        }

        public void ReportDependencyFrom(FileModel currentFileModel, string from, string type)
        {
            ReportDependencyFrom(currentFileModel, from, DependencyItemSourceType.File, type);
        }

        public void ReportDependencyFrom(FileModel currentFileModel, string from, string fromType, string type)
        {
            if (currentFileModel == null)
            {
                throw new ArgumentNullException(nameof(currentFileModel));
            }
            if (string.IsNullOrEmpty(from))
            {
                throw new ArgumentNullException(nameof(from));
            }
            if (fromType == null)
            {
                throw new ArgumentNullException(nameof(fromType));
            }
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }
            if (DependencyGraph == null)
            {
                return;
            }
            string fromKey = fromType == DependencyItemSourceType.File ?
                IncrementalUtility.GetDependencyKey(currentFileModel.OriginalFileAndType.ChangeFile((RelativePath)currentFileModel.OriginalFileAndType.File + (RelativePath)from)) :
                from;
            string toKey = IncrementalUtility.GetDependencyKey(currentFileModel.OriginalFileAndType);
            ReportDependencyCore(new DependencyItemSourceInfo(fromType, fromKey), toKey, toKey, type);
        }

        public void ReportReference(FileModel currentFileModel, string reference, string referenceType)
        {
            if (currentFileModel == null)
            {
                throw new ArgumentNullException(nameof(currentFileModel));
            }
            if (string.IsNullOrEmpty(reference))
            {
                throw new ArgumentNullException(nameof(reference));
            }
            if (referenceType == null)
            {
                throw new ArgumentNullException(nameof(referenceType));
            }
            if (DependencyGraph == null)
            {
                return;
            }
            string file = IncrementalUtility.GetDependencyKey(currentFileModel.OriginalFileAndType);
            DependencyGraph.ReportReference(new ReferenceItem(new DependencyItemSourceInfo(referenceType, reference), file, file));
        }

        public bool HasMetadataValidation => Validators.Count > 0;

        public void ValidateInputMetadata(string sourceFile, ImmutableDictionary<string, object> metadata)
        {
            foreach (var v in Validators)
            {
                lock (v)
                {
                    v.Validate(sourceFile, metadata);
                }
            }
        }

        public void LogDiagnostic(string message, string file, string line)
        {
            Logger.LogDiagnostic(message, file: file, line: line);
        }

        public void LogVerbose(string message, string file, string line)
        {
            Logger.LogVerbose(message, file: file, line: line);
        }

        public void LogInfo(string message, string file, string line)
        {
            Logger.LogInfo(message, file: file, line: line);
        }

        public void LogWarning(string message, string file, string line)
        {
            Logger.LogWarning(message, file: file, line: line);
        }

        public void LogError(string message, string file, string line)
        {
            Logger.LogError(message, file: file, line: line);
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
        }

        #endregion

        public void Reload(IEnumerable<FileModel> models)
        {
            
        }

        #region Private Methods

        private void HandleFileOrBaseDirChanged(object sender, EventArgs e)
        {
            var m = sender as FileModel;
            if (m == null)
            {
                return;
            }
            lock (_syncRoot)
            {
                FileMap[m.OriginalFileAndType] = m.FileAndType;
            }
        }

        private void ContentAccessedHandler(object sender, EventArgs e)
        {
            _lru.Access((ModelWithCache)sender);
        }

        private static void OnLruRemoving(ModelWithCache m)
        {
            try
            {
                m.Serialize();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Unable to serialize model, details:{ex.ToString()}", file: m.File);
            }
        }

        private void ReportDependencyCore(DependencyItemSourceInfo from, DependencyItemSourceInfo to, DependencyItemSourceInfo reportedBy, string type)
        {
            DependencyGraph.ReportDependency(new DependencyItem(from, to, reportedBy, type));
        }

        #endregion
    }
}
