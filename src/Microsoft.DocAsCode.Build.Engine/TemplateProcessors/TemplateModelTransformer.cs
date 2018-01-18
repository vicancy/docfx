// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.Engine
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using System.Web;

    using HtmlAgilityPack;

    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.Plugins;
    using Newtonsoft.Json.Linq;

    public class TemplateModelTransformer
    {
        private const string GlobalVariableKey = "__global";
        private const int MaxInvalidXrefMessagePerFile = 10;

        private readonly DocumentBuildContext _context;
        private readonly ApplyTemplateSettings _settings;
        private readonly TemplateCollection _templateCollection;
        private readonly RendererLoader _rendererLoader;
        private readonly IDictionary<string, object> _globalVariables;

        public TemplateModelTransformer(DocumentBuildContext context, TemplateCollection templateCollection, ApplyTemplateSettings settings, IDictionary<string, object> globals)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _templateCollection = templateCollection;
            _settings = settings;
            _globalVariables = globals;
            _rendererLoader = new RendererLoader(templateCollection.Reader, templateCollection.MaxParallelism);
        }

        /// <summary>
        /// Must guarantee thread safety
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        internal ManifestItem Transform(InternalManifestItem item)
        {
            if (item.Model == null || item.Model.Content == null)
            {
                throw new ArgumentNullException("Content for item.Model should not be null!");
            }

            var model = ConvertObjectToDictionary(item.Model.Content);
            AppendGlobalMetadata(model);

            // Add ops required properties before process
            model["content_git_url"] = "https://github.com/MicrosoftDocs/dotnet-ci-demo-1/blob/master/ci-demo/xml/CatLibrary/Cat`2.xml";
            model["original_ref_skeleton_git_url"] = "https://github.com/MicrosoftDocs/dotnet-ci-demo-1/blob/master/ci-demo/xml/CatLibrary/Cat`2.xml";
            model["search.ms_sitename"] = "Docs";

            model["search.ms_docsetname"] = "dotnet";
            model["search.ms_product"] = "MSDN";
            model["version"] = null;
            model["_op_canonicalUrlPrefix"] = "https://ppe.docs.microsoft.com/en-us/dotnet-ci-demo-1/";
            model["locale"] = "en-us";
            model["site_name"] = "Docs";
            model["_op_openToPublicContributors"] = true;
            model["depot_name"] = "MSDN.dotnet-ci-demo-1";

            model["_op_gitRefSkeletonCommitHistory"] = new string[] { };
            model["open_to_public_contributors"] = true;
            model["api_name"] = new string[] {
    "CatLibrary.Cat`2.Age",
    "CatLibrary.Cat`2.get_Age",
    "CatLibrary.Cat`2.set_Age"
  };
            model["api_location"] = new string[] {
    "CatLibrary.dll"
  };
            model["topic_type"] = new string[] {
    "apiref"
  };
            model["api_type"] = new string[] {
    "Assembly"
  };
            model["f1_keywords"] = new string[] {
    "CatLibrary.Cat`2.Age",
    "CatLibrary::Cat`2::Age"
  };
            model["dev_langs"] = new string[] {
    "CSharp",
    "powershell",
    "VB"
  };
            model["helpviewer_keywords"] = new string[] {
    "Cat<T,K>.Age property [.NET]",
    "Age property [.NET] class Cat<T,K>",
  };
            model["updated_at"] = "2017-12-19 07:11 AM";
            model["document_id"] = "9b34a91d-485c-7e2c-12a7-cd53ca125078";
            model["document_version_independent_id"] = "3e5dd3da-ff84-5c05-b8c7-7ee3ad7291cb";
            model["fileRelativePath"] = item.FileWithoutExtension + ".html";
            //model["_tocPath"] = "api/dotnet-ci-demo-1/_splitted/CatLibrary/toc.json";
            //model["_path"] = "api/CatLibrary.Cat-2.Age.html";
            //model["_key"] = "api/CatLibrary.Cat-2.Age.yml";
            //model["_tocKey"] = "~/api/dotnet-ci-demo-1/_splitted/CatLibrary/toc.yml";

            if (_settings.Options.HasFlag(ApplyTemplateOptions.ExportRawModel))
            {
                ExportModel(model, item.FileWithoutExtension, _settings.RawModelExportSettings);
            }

            var manifestItem = new ManifestItem
            {
                DocumentType = item.DocumentType,
                SourceRelativePath = item.LocalPathFromRoot,
                Metadata = item.Metadata,
                Version = _context.VersionName,
                Group = _context.GroupInfo?.Name,
            };
            var outputDirectory = _settings.OutputFolder ?? Directory.GetCurrentDirectory();

            // 1. process resource
            if (item.ResourceFile != null)
            {
                // Resource file has already been processed in its plugin
                var ofi = new OutputFileInfo
                {
                    RelativePath = item.ResourceFile,
                    LinkToPath = GetLinkToPath(item.ResourceFile),
                };
                manifestItem.OutputFiles.Add("resource", ofi);
            }

            // 2. process model
            var templateBundle = _templateCollection[item.DocumentType];
            if (templateBundle == null)
            {
                return manifestItem;
            }

            var unresolvedXRefs = new List<XRefDetails>();
            string html = null;
            // Must convert to JObject first as we leverage JsonProperty as the property name for the model
            foreach (var template in templateBundle.Templates)
            {
                if (template.Renderer == null)
                {
                    continue;
                }
                try
                {
                    var extension = template.Extension;
                    string outputFile = Path.Combine(_settings.OutputFolder, item.FileWithoutExtension + extension);
                    object viewModel = null;
                    try
                    {
                        viewModel = template.TransformModel(model);
                    }
                    catch (Exception e)
                    {
                        string message;
                        if (_settings.DebugMode)
                        {
                            // save raw model for further investigation:
                            var rawModelPath = ExportModel(model, item.FileWithoutExtension, _settings.RawModelExportSettingsForDebug);
                            message = $"Error transforming model \"{rawModelPath}\" generated from \"{item.LocalPathFromRoot}\" using \"{template.ScriptName}\". {e.Message}";
                        }
                        else
                        {
                            message = $"Error transforming model generated from \"{item.LocalPathFromRoot}\" using \"{template.ScriptName}\". To get the detailed raw model, please run docfx with debug mode --debug. {e.Message} ";
                        }

                        Logger.LogError(message);
                        throw new DocumentException(message, e);
                    }

                    string result;
                    try
                    {
                        result = template.Transform(viewModel);
                    }
                    catch (Exception e)
                    {
                        string message;
                        if (_settings.DebugMode)
                        {
                            // save view model for further investigation:
                            var viewModelPath = ExportModel(viewModel, outputFile, _settings.ViewModelExportSettingsForDebug);
                            message = $"Error applying template \"{template.Name}\" to view model \"{viewModelPath}\" generated from \"{item.LocalPathFromRoot}\". {e.Message}";
                        }
                        else
                        {
                            message = $"Error applying template \"{template.Name}\" generated from \"{item.LocalPathFromRoot}\". To get the detailed view model, please run docfx with debug mode --debug. {e.Message}";
                        }

                        Logger.LogError(message);
                        throw new DocumentException(message, e);
                    }

                    if (_settings.Options.HasFlag(ApplyTemplateOptions.ExportViewModel))
                    {
                        ExportModel(viewModel, outputFile, _settings.ViewModelExportSettings);
                    }

                    if (_settings.Options.HasFlag(ApplyTemplateOptions.TransformDocument))
                    {
                        if (string.IsNullOrWhiteSpace(result))
                        {
                            string message;
                            if (_settings.DebugMode)
                            {
                                var viewModelPath = ExportModel(viewModel, outputFile, _settings.ViewModelExportSettingsForDebug);
                                message = $"Model \"{viewModelPath}\" is transformed to empty string with template \"{template.Name}\"";
                            }
                            else
                            {
                                message = $"Model is transformed to empty string with template \"{template.Name}\". To get the detailed view model, please run docfx with debug mode --debug";
                            }
                            Logger.LogWarning(message);
                        }

                        List<XRefDetails> invalidXRefs;
                        var transformed = TransformDocument(result ?? string.Empty, extension, _context, outputFile, manifestItem, out invalidXRefs);
                        if (extension == ".html")
                        {
                            html = transformed;
                        }
                        unresolvedXRefs.AddRange(invalidXRefs);
                        Logger.LogDiagnostic($"Transformed model \"{item.LocalPathFromRoot}\" to \"{outputFile}\".");
                    }
                }
                catch (PathTooLongException e)
                {
                    var message = $"Error processing {item.LocalPathFromRoot}: {e.Message}";
                    throw new PathTooLongException(message, e);
                }
            }
            if (item.DocumentType == "ManagedReference")
            {
                var opsModel = ToOPSModel(model, html);

                JsonUtility.Serialize(Path.Combine(_settings.OutputFolder, item.FileWithoutExtension + ".raw.page.json"), opsModel, Newtonsoft.Json.Formatting.Indented);
            }

            item.Model = null;
            LogInvalidXRefs(unresolvedXRefs);

            return manifestItem;
        }

        static JObject ToOPSModel(IDictionary<string, object> model, string html)
        {
            var pageMetadata = new Dictionary<string, object>
            {
                ["site_name"] = "Docs",
                ["locale"] = "en-us",
                ["depot_name"] = $"MSDN.Docs",
                ["pagetype"] = model["document_type"],
                ["pdf_url_template"] = "https://docs.microsoft.com/pdfstore/en-us/MSDN.Docs/{branchName}{pdfName}",
                ["document_id"] = model["document_id"],
                ["document_version_independent_id"] = model["document_version_independent_id"],
                ["version"] = 0,
                ["word_count"] = 100,
                ["search.ms_product"] = "MSDN",
                ["search.ms_docsetname"] = "Dotnet",
                ["search.ms_sitename"] = "Docs",
                ["toc_rel"] = model["_tocRel"],
            };

            var rawMetadata = new JObject
            {
                ["title"] = JValue.FromObject( model["uid"]),
                ["layout"] = JValue.FromObject(model["document_type"]),
                ["_op_rawTitle"] = JValue.FromObject(model["uid"]),
                ["open_to_public_contributors"] = JValue.FromObject(model["open_to_public_contributors"]),
                ["_op_openToPublicContributors"] = JValue.FromObject(model["_op_openToPublicContributors"]),
                ["_op_wordCount"] = JValue.FromObject(100),
                ["_op_canonicalUrlPrefix"] = JValue.FromObject(model["_op_canonicalUrlPrefix"]),
                ["_op_canonicalUrl"] = JValue.FromObject(model["_op_canonicalUrlPrefix"]),
                ["canonical_url"] = JValue.FromObject(model["_op_canonicalUrlPrefix"]),
                ["fileRelativePath"] = JValue.FromObject(model["fileRelativePath"]),
                ["_op_pdfUrlPrefixTemplate"] = JValue.FromObject("https://docs.microsoft.com/pdfstore/en-us/docs/{branchName}"),
            };

            var sks = new HashSet<string>((model["_systemKeys"] as IEnumerable<object>).Select(s => s.ToString()));
            foreach(var key in model)
            {
                if (!sks.Contains(key.Key))
                {
                    rawMetadata[key.Key] = key.Value == null ? null : JValue.FromObject(key.Value);
                }
            }

            var result = new JObject
            {
                ["pageMetadata"] = ToMetaHtml(pageMetadata),
                ["rawMetadata"] = rawMetadata,
                ["themesRelativePathToOutputRoot"] = "_themes/",
            };

            result["content"] = html;

            return result;
        }

        private static string ToMetaHtml(Dictionary<string, object> metadata)
        {
            var sb = new StringBuilder();
            foreach (var meta in metadata)
            {
                sb.Append("<meta name=\"");
                sb.Append(HttpUtility.HtmlEncode(meta.Key));
                sb.Append("\" content=\"");
                sb.Append(HttpUtility.HtmlEncode(meta.Value));
                sb.Append("\" />");
            }
            return sb.ToString();
        }

        private void LogInvalidXRefs(List<XRefDetails> unresolvedXRefs)
        {
            if (unresolvedXRefs == null || unresolvedXRefs.Count == 0)
            {
                return;
            }

            var distinctUids = unresolvedXRefs.Select(i => i.RawSource).Distinct().Select(s => $"\"{HttpUtility.HtmlDecode(s)}\"").ToList();
            Logger.LogWarning($"{distinctUids.Count} invalid cross reference(s) {distinctUids.ToDelimitedString(", ")}.");
            foreach (var group in unresolvedXRefs.GroupBy(i => i.SourceFile))
            {
                // For each source file, print the first 10 invalid cross reference
                var details = group.Take(MaxInvalidXrefMessagePerFile).Select(i => $"\"{HttpUtility.HtmlDecode(i.RawSource)}\" in line {i.SourceStartLineNumber.ToString()}").Distinct().ToList();
                var prefix = details.Count > MaxInvalidXrefMessagePerFile ? $"top {MaxInvalidXrefMessagePerFile} " : string.Empty;
                var message = $"Details for {prefix}invalid cross reference(s): {details.ToDelimitedString(", ")}";

                if (group.Key != null)
                {
                    Logger.LogInfo(message, file: group.Key);
                }
                else
                {
                    Logger.LogInfo(message);
                }
            }
        }

        private string GetLinkToPath(string fileName)
        {
            if (EnvironmentContext.FileAbstractLayerImpl == null)
            {
                return null;
            }
            string pp;
            try
            {
                pp = ((FileAbstractLayer)EnvironmentContext.FileAbstractLayerImpl).GetOutputPhysicalPath(fileName);
            }
            catch (FileNotFoundException)
            {
                pp = ((FileAbstractLayer)EnvironmentContext.FileAbstractLayerImpl).GetPhysicalPath(fileName);
            }
            var expandPP = Path.GetFullPath(Environment.ExpandEnvironmentVariables(pp));
            var outputPath = Path.GetFullPath(_context.BuildOutputFolder);
            if (expandPP.Length > outputPath.Length &&
                (expandPP[outputPath.Length] == '\\' || expandPP[outputPath.Length] == '/') &&
                FilePathComparer.OSPlatformSensitiveStringComparer.Equals(outputPath, expandPP.Remove(outputPath.Length)))
            {
                return null;
            }
            else
            {
                return pp;
            }
        }

        private void AppendGlobalMetadata(IDictionary<string, object> model)
        {
            if (_globalVariables == null)
            {
                return;
            }

            if (model.ContainsKey(GlobalVariableKey))
            {
                Logger.LogWarning($"Data model contains key {GlobalVariableKey}, {GlobalVariableKey} is to keep system level global metadata and is not allowed to overwrite. The {GlobalVariableKey} property inside data model will be ignored.");
            }

            model[GlobalVariableKey] = new Dictionary<string, object>(_globalVariables);
        }

        private static IDictionary<string, object> ConvertObjectToDictionary(object model)
        {
            if (model is IDictionary<string, object> dictionary)
            {
                return dictionary;
            }

            var objectModel = ConvertToObjectHelper.ConvertStrongTypeToObject(model) as IDictionary<string, object>;
            if (objectModel == null)
            {
                throw new ArgumentException("Only object model is supported for template transformation.");
            }

            return objectModel;
        }

        private static string ExportModel(object model, string modelFileRelativePath, ExportSettings settings)
        {
            if (model == null)
            {
                return null;
            }
            var outputFolder = settings.OutputFolder ?? string.Empty;
            string modelPath;
            try
            {
                modelPath = Path.GetFullPath(Path.Combine(outputFolder, settings.PathRewriter(modelFileRelativePath)));
            }
            catch (PathTooLongException)
            {
                modelPath = Path.GetFullPath(Path.Combine(outputFolder, Path.GetRandomFileName()));
            }

            JsonUtility.Serialize(modelPath, model);
            return StringExtension.ToDisplayPath(modelPath);
        }

        private string TransformDocument(string result, string extension, IDocumentBuildContext context, string destFilePath, ManifestItem manifestItem, out List<XRefDetails> unresolvedXRefs)
        {
            Task<byte[]> hashTask;
            unresolvedXRefs = new List<XRefDetails>();
            if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase))
            {
                result = TransformHtml(context, result, manifestItem.SourceRelativePath, destFilePath, out unresolvedXRefs);
            }
            using (var stream = EnvironmentContext.FileAbstractLayer.Create(destFilePath).WithMd5Hash(out hashTask))
            using (var sw = new StreamWriter(stream))
            {
                sw.Write(result);
            }
            var ofi = new OutputFileInfo
            {
                RelativePath = destFilePath,
                LinkToPath = GetLinkToPath(destFilePath),
                Hash = Convert.ToBase64String(hashTask.Result)
            };
            manifestItem.OutputFiles.Add(extension, ofi);
            return result;
        }

        private string TransformHtml(IDocumentBuildContext context, string html, string sourceFilePath, string destFilePath, out List<XRefDetails> unresolvedXRefs)
        {
            // Update href and xref
            HtmlDocument document = new HtmlDocument();
            document.LoadHtml(html);

            TransformHtmlCore(context, sourceFilePath, destFilePath, document, out unresolvedXRefs);

            using (var sw = new StringWriter())
            {
                document.Save(sw);
                return sw.ToString();
            }
        }

        private void TransformHtmlCore(IDocumentBuildContext context, string sourceFilePath, string destFilePath, HtmlDocument html, out List<XRefDetails> unresolvedXRefs)
        {
            unresolvedXRefs = new List<XRefDetails>();
            var xrefLinkNodes = html.DocumentNode.SelectNodes("//a[starts-with(@href, 'xref:')]");
            if (xrefLinkNodes != null)
            {
                foreach (var xref in xrefLinkNodes)
                {
                    TransformXrefLink(xref, context);
                }
            }

            var xrefNodes = html.DocumentNode.SelectNodes("//xref");
            if (xrefNodes != null)
            {
                foreach (var xref in xrefNodes)
                {
                    var resolved = UpdateXref(xref, context, Constants.DefaultLanguage, out var xrefDetails);
                    if (!resolved)
                    {
                        unresolvedXRefs.Add(xrefDetails);
                    }
                }
            }

            var srcNodes = html.DocumentNode.SelectNodes("//*/@src");
            if (srcNodes != null)
            {
                foreach (var link in srcNodes)
                {
                    UpdateHref(link, "src", context, sourceFilePath, destFilePath);
                }
            }

            var hrefNodes = html.DocumentNode.SelectNodes("//*/@href");
            if (hrefNodes != null)
            {
                foreach (var link in hrefNodes)
                {
                    UpdateHref(link, "href", context, sourceFilePath, destFilePath);
                }
            }
        }

        private static void TransformXrefLink(HtmlNode node, IDocumentBuildContext context)
        {
            var convertedNode = XRefDetails.ConvertXrefLinkNodeToXrefNode(node);
            node.ParentNode.ReplaceChild(convertedNode, node);
        }

        private bool UpdateXref(HtmlNode node, IDocumentBuildContext context, string language, out XRefDetails xref)
        {
            xref = XRefDetails.From(node);
            XRefSpec xrefSpec = null;
            if (!string.IsNullOrEmpty(xref.Uid))
            {
                // Resolve external xref map first, and then internal xref map.
                // Internal one overrides external one
                xrefSpec = context.GetXrefSpec(HttpUtility.HtmlDecode(xref.Uid));
                xref.ApplyXrefSpec(xrefSpec);
            }

            var renderer = xref.TemplatePath == null ? null : _rendererLoader.Load(xref.TemplatePath);
            var convertedNode = xref.ConvertToHtmlNode(language, renderer);
            node.ParentNode.ReplaceChild(convertedNode, node);
            if (xrefSpec == null && xref.ThrowIfNotResolved == true)
            {
                return false;
            }

            return true;
        }

        private void UpdateHref(HtmlNode link, string attribute, IDocumentBuildContext context, string sourceFilePath, string destFilePath)
        {
            var originalHref = link.GetAttributeValue(attribute, null);
            var anchor = link.GetAttributeValue("anchor", null);
            link.Attributes.Remove("anchor");
            var originalPath = UriUtility.GetPath(originalHref);
            var path = RelativePath.TryParse(originalPath);

            if (path == null)
            {
                if (!string.IsNullOrEmpty(anchor))
                {
                    link.SetAttributeValue(attribute, originalHref + anchor);
                }

                return;
            }

            var fli = FileLinkInfo.Create(sourceFilePath, destFilePath, originalPath, context);
            var href = _settings.HrefGenerator?.GenerateHref(fli) ?? fli.Href;
            link.SetAttributeValue(attribute, href + UriUtility.GetQueryString(originalHref) + (anchor ?? UriUtility.GetFragment(originalHref)));
        }
    }
}
