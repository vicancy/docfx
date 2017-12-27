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

    internal class Utility
    {
        private static readonly IEnumerable<IDocumentProcessor> processors = new IDocumentProcessor[]
        {
            new ConceptualDocumentProcessor{
                BuildSteps = new IDocumentBuildStep[]
                {
                    new BuildConceptualDocument(),
                    new CountWord(),
                }
            },
            new TocDocumentProcessor{
                BuildSteps = new IDocumentBuildStep[]
                {
                    new BuildTocDocument(),
                }
            },
            new ManagedReferenceDocumentProcessor{
                BuildSteps = new IDocumentBuildStep[]
                {
                    new BuildManagedReferenceDocument(),
                    new FillReferenceInformation(),
                    new MergeManagedReferenceDocument(),
                    new ApplyPlatformVersion(),
                    new ApplyOverwriteDocumentForMref(),
                }
            }
        };

        public static ImmutableDictionary<string, object> ApplyFileMetadata(
            string file,
            ImmutableDictionary<string, object> metadata,
            FileMetadata fileMetadata)
        {
            if (fileMetadata == null || fileMetadata.Count == 0) return metadata;
            var result = new Dictionary<string, object>(metadata);
            var baseDir = string.IsNullOrEmpty(fileMetadata.BaseDir) ? Directory.GetCurrentDirectory() : fileMetadata.BaseDir;
            var relativePath = PathUtility.MakeRelativePath(baseDir, file);
            foreach (var item in fileMetadata)
            {
                // As the latter one overrides the former one, match the pattern from latter to former
                for (int i = item.Value.Length - 1; i >= 0; i--)
                {
                    if (item.Value[i].Glob.Match(relativePath))
                    {
                        // override global metadata if metadata is defined in file metadata
                        result[item.Value[i].Key] = item.Value[i].Value;
                        Logger.LogDiagnostic($"{relativePath} matches file metadata with glob pattern {item.Value[i].Glob.Raw} for property {item.Value[i].Key}");
                        break;
                    }
                }
            }
            return result.ToImmutableDictionary();
        }

        public static IBuildStep CreateOrGetOneTask(FileAndType file, Context context, Config config)
        {
            // Use the stored one from fileMapping

            if (!context.FileMapping.TryGetValue(file.Key, out file))
            {
                Logger.LogError($"{file.Key} is not in global scope");
                throw new DocfxException(file.Key);
            }

            if (context.FileStepMapping.TryGetValue(file.Key, out var step))
            {
                return step;
            }

            var processor = (from p in processors
                             let priority = p.GetProcessingPriority(file)
                             where priority != ProcessingPriority.NotSupported
                             group p by priority into ps
                             orderby ps.Key descending
                             select ps.ToList()).FirstOrDefault().FirstOrDefault();
            if (processor == null)
            {
                context.UnhandledItems.Add(file);
                return IdleStep.Default;
            }
            else if (processor is TocDocumentProcessor)
            {
                step = new TocFileBuild(file, config, processor);
            }
            else
            {
                step = new ConceptualFileBuild(file, config, processor);
            }
            context.FileStepMapping.TryAdd(file.Key, step);
            return step;
        }

        public static bool IsSupportedFile(string file)
        {
            var fileType = GetTocFileType(file);
            if (fileType == TocFileType.Markdown || fileType == TocFileType.Yaml)
            {
                return true;
            }

            return false;
        }

        public static bool IsSupportedRelativeHref(string href)
        {
            var hrefType = GetHrefType(href);
            return IsSupportedRelativeHref(hrefType);
        }

        public static bool IsSupportedRelativeHref(HrefType hrefType)
        {
            // TocFile href type can happen when homepage is set to toc.yml explicitly
            return hrefType == HrefType.RelativeFile
                || hrefType == HrefType.YamlTocFile
                || hrefType == HrefType.MarkdownTocFile;
        }

        public static HrefType GetHrefType(string href)
        {
            var hrefWithoutAnchor = href != null ? UriUtility.GetPath(href) : href;
            if (!PathUtility.IsRelativePath(hrefWithoutAnchor))
            {
                return HrefType.AbsolutePath;
            }
            var fileName = Path.GetFileName(hrefWithoutAnchor);
            if (string.IsNullOrEmpty(fileName))
            {
                return HrefType.RelativeFolder;
            }

            var tocFileType = GetTocFileType(hrefWithoutAnchor);

            if (tocFileType == TocFileType.Markdown)
            {
                return HrefType.MarkdownTocFile;
            }

            if (tocFileType == TocFileType.Yaml)
            {
                return HrefType.YamlTocFile;
            }

            return HrefType.RelativeFile;
        }

        public static TocFileType GetTocFileType(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return TocFileType.None;
            }

            var fileName = Path.GetFileName(filePath);

            if (Constants.TableOfContents.MarkdownTocFileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return TocFileType.Markdown;
            }
            if (Constants.TableOfContents.YamlTocFileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return TocFileType.Yaml;
            }

            return TocFileType.None;
        }

        internal enum TocFileType
        {
            None,
            Markdown,
            Yaml
        }

        internal enum HrefType
        {
            AbsolutePath,
            RelativeFile,
            RelativeFolder,
            MarkdownTocFile,
            YamlTocFile,
        }

        class Constants
        {
            public static class TableOfContents
            {
                public const string MarkdownTocFileName = "toc.md";
                public const string YamlTocFileName = "toc.yml";
            }
        }
    }
}
