// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.SchemaDriven.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Composition;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;

    using Microsoft.DocAsCode.Build.Engine;
    using Microsoft.DocAsCode.Build.SchemaDriven.Processors;
    using Microsoft.DocAsCode.Build.TableOfContents;
    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.Exceptions;
    using Microsoft.DocAsCode.Plugins;
    using Microsoft.DocAsCode.Tests.Common;

    using Newtonsoft.Json.Linq;
    using Xunit;

    [Trait("Owner", "lianwei")]
    [Trait("EntityType", "SchemaDrivenProcessorTest")]
    public class SchemaDrivenProcessorWithOverwriteTest : TestBase
    {
        private string _outputFolder;
        private string _inputFolder;
        private string _templateFolder;
        private FileCollection _defaultFiles;
        private ApplyTemplateSettings _applyTemplateSettings;
        private TemplateManager _templateManager;

        private const string RawModelFileExtension = ".raw.json";

        public SchemaDrivenProcessorWithOverwriteTest()
        {
            _outputFolder = GetRandomFolder();
            _inputFolder = GetRandomFolder();
            _templateFolder = GetRandomFolder();
            _defaultFiles = new FileCollection(Directory.GetCurrentDirectory());
            _applyTemplateSettings = new ApplyTemplateSettings(_inputFolder, _outputFolder)
            {
                RawModelExportSettings = { Export = true },
                TransformDocument = true,
            };

            _templateManager = new TemplateManager(null, null, new List<string> { "template" }, null, _templateFolder);
        }

        [Fact]
        public void TestContextObjectSchemaWithOverwrite()
        {
            using (var listener = new TestListenerScope("TestContextObjectSchemaWithOverwrite"))
            {
                var schemaFile = CreateFile("template/schemas/contextobject.schema.json", File.ReadAllText("TestData/schemas/contextobject.test.schema.json"), _templateFolder);
                var tocTemplate = CreateFile("template/toc.json.tmpl", "toc template", _templateFolder);
                var inputFileName = "co/active.yml";
                var includeFile = CreateFile("a b/inc.md", @"**Include**", _inputFolder);
                var inputFile = CreateFile(inputFileName, @"### YamlMime:ContextObject
breadcrumb_path: /absolute/toc.json
toc_rel: ../a b/toc.md
file_include: ../a b/inc.md
uhfHeaderId: MSDocsHeader-DotNet
empty:
searchScope:
  - .NET
", _inputFolder);
                var overwriteFile = CreateFile("overwrite/a.md", @"---
uid: Hello
summary: *content
---
Nice
", _inputFolder);
                FileCollection files = new FileCollection(_defaultFiles);
                files.Add(DocumentType.Article, new[] { inputFile }, _inputFolder);
                files.Add(DocumentType.Overwrite, new[] { overwriteFile }, _inputFolder);
                BuildDocument(files);

                Assert.Equal(3, listener.Items.Count);
                Assert.NotNull(listener.Items.FirstOrDefault(s => s.Message.StartsWith("There is no template processing document type(s): ContextObject")));
                Assert.NotNull(listener.Items.FirstOrDefault(s => s.Message.StartsWith("Invalid file link")));
                listener.Items.Clear();

                var rawModelFilePath = GetRawModelFilePath(inputFileName);
                Assert.True(File.Exists(rawModelFilePath));
                var rawModel = JsonUtility.Deserialize<JObject>(rawModelFilePath);

                Assert.Equal("Hello world!", rawModel["meta"].ToString());
                Assert.Equal("/absolute/toc.json", rawModel["breadcrumb_path"].ToString());
                Assert.Equal("../a b/toc.md", rawModel["toc_rel"].ToString());
                Assert.Equal($"<p sourcefile=\"{includeFile}\" sourcestartlinenumber=\"1\" sourceendlinenumber=\"1\"><strong>Include</strong></p>\n",
                    rawModel["file_include"].ToString());
                Assert.Equal("MSDocsHeader-DotNet", rawModel["uhfHeaderId"].ToString());
                Assert.Equal($".NET", rawModel["searchScope"][0].ToString());

                files = new FileCollection(_defaultFiles);
                files.Add(DocumentType.Article, new[] { inputFile }, _inputFolder);
                var tocFile = CreateFile("a b/toc.md", "### hello", _inputFolder);
                files.Add(DocumentType.Article, new[] { tocFile }, _inputFolder);

                BuildDocument(files);

                Assert.Equal(2, listener.Items.Count);
                Assert.NotNull(listener.Items.FirstOrDefault(s => s.Message.StartsWith("There is no template processing document type(s): ContextObject")));

                Assert.True(File.Exists(rawModelFilePath));
                rawModel = JsonUtility.Deserialize<JObject>(rawModelFilePath);

                Assert.Equal("Hello world!", rawModel["meta"].ToString());
                Assert.Equal("/absolute/toc.json", rawModel["breadcrumb_path"].ToString());
                Assert.Equal("../a%20b/toc.json", rawModel["toc_rel"].ToString());
                Assert.Equal("MSDocsHeader-DotNet", rawModel["uhfHeaderId"].ToString());
                Assert.Equal($".NET", rawModel["searchScope"][0].ToString());
            }
        }

        [Fact]
        public void TestMrefSchemaWithOverwrite()
        {
            using (var listener = new TestListenerScope("TestMrefSchemaWithOverwrite"))
            {
                var schemaFile = CreateFile("template/schemas/mref.test.schema.json", File.ReadAllText("TestData/schemas/mref.test.schema.json"), _templateFolder);
                var templateXref = CreateFile("template/partials/overview.tmpl", @"{{name}}:{{{summary}}}", _templateFolder);
                var templateFile = CreateFile("template/ManagedReference.html.tmpl", @"
{{#items}}
{{#children}}
<xref uid={{.}} template=""partials/overview.tmpl""/>
{{/children}}
{{/items}}
", _templateFolder);
                var inputFileName = "inputs/CatLibrary.ICat.yml";
                var inputFile = CreateFile(inputFileName, File.ReadAllText("TestData/inputs/CatLibrary.ICat.yml"), _inputFolder);
                var overwriteFile = CreateFile("overwrite/a.md", @"---
uid: CatLibrary.ICat
summary: *content
newArray:
  - item1
  - item2
example:
  - item1
  - xref: <xref:System.String>
source:
  remote:
    branch: live
---
Refers to @CatLibrary.IAnimal.Item(System.Int32)
syntax:
   ret
---
uid: CatLibrary.ICat.eat

---
Hello
", _inputFolder);

                FileCollection files = new FileCollection(_defaultFiles);
                files.Add(DocumentType.Article, new[] { inputFile }, _inputFolder);
                files.Add(DocumentType.Overwrite, new[] { overwriteFile }, _inputFolder);

                BuildDocument(files);

                Assert.Equal(1, listener.Items.Count);
                listener.Items.Clear();

                var xrefspec = Path.Combine(_outputFolder, "xrefmap.yml");
                var xrefmap = YamlUtility.Deserialize<XRefMap>(xrefspec);
                Assert.Equal(2, xrefmap.References.Count);
                Assert.Equal(8, xrefmap.References[0].Keys.Count);
                Assert.Equal(7, xrefmap.References[1].Keys.Count);

                Assert.Equal("ICat", xrefmap.References[0].Name);
                Assert.Equal("CatLibrary.ICat.CatLibrary.ICatExtension.Sleep(System.Int64)", xrefmap.References[0]["extensionMethods/0"]);
                var outputFileName = Path.ChangeExtension(inputFileName, ".html");
                Assert.Equal(outputFileName, xrefmap.References[0].Href);
                Assert.NotNull(xrefmap.References[0]["summary"]);

                var outputFilePath = Path.Combine(_outputFolder, outputFileName);
                Assert.True(File.Exists(outputFilePath));

                Assert.Equal($@"
eat:<p>eat event of cat. Every cat must implement this event.</p>
"
                    .Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None),
File.ReadAllLines(outputFilePath));
            }
        }

        [Fact]
        public void TestValidMetadataReference()
        {
            using (var listener = new TestListenerScope("TestGeneralFeaturesInSDP"))
            {
                var schemaFile = CreateFile("template/schemas/mta.reference.test.schema.json", @"
{
  ""$schema"": ""http://dotnet.github.io/docfx/schemas/v1.0/schema.json#"",
  ""version"": ""1.0.0"",
  ""title"": ""MetadataReferenceTest"",
  ""description"": ""A simple test schema for sdp"",
  ""type"": ""object"",
  ""properties"": {
      ""metadata"": {
            ""type"": ""object""
      }
            },
  ""metadata"": ""/metadata""
}
", _templateFolder);
                var inputFileName1 = "page1.yml";
                var inputFile1 = CreateFile(inputFileName1, @"### YamlMime:MetadataReferenceTest
title: Web Apps Documentation
metadata:
  title: Azure Web Apps Documentation - Tutorials, API Reference
  meta.description: Learn how to use App Service Web Apps to build and host websites and web applications.
  ms.service: app-service
  ms.tgt_pltfrm: na
  ms.author: carolz
sections:
- title: 5-Minute Quickstarts
toc_rel: ../a b/toc.md
uhfHeaderId: MSDocsHeader-DotNet
searchScope:
  - .NET
", _inputFolder);
                var inputFileName2 = "page2.yml";
                var inputFile2 = CreateFile(inputFileName2, @"### YamlMime:MetadataReferenceTest
title: Web Apps Documentation
", _inputFolder);

                FileCollection files = new FileCollection(_defaultFiles);
                files.Add(DocumentType.Article, new[] { inputFile1, inputFile2 }, _inputFolder);
                BuildDocument(files);

                Assert.Equal(3, listener.Items.Count);
                Assert.NotNull(listener.Items.FirstOrDefault(s => s.Message.StartsWith("There is no template processing document type(s): MetadataReferenceTest")));
                listener.Items.Clear();

                var rawModelFilePath = GetRawModelFilePath(inputFileName1);
                Assert.True(File.Exists(rawModelFilePath));
                var rawModel = JsonUtility.Deserialize<JObject>(rawModelFilePath);

                Assert.Equal("overwritten", rawModel["metadata"]["meta"].ToString());
                Assert.Equal("1", rawModel["metadata"]["another"].ToString());
                Assert.Equal("app-service", rawModel["metadata"]["ms.service"].ToString());

                var rawModelFilePath2 = GetRawModelFilePath(inputFileName2);
                Assert.True(File.Exists(rawModelFilePath2));
                var rawModel2 = JsonUtility.Deserialize<JObject>(rawModelFilePath2);

                Assert.Equal("Hello world!", rawModel2["metadata"]["meta"].ToString());
                Assert.Equal("2", rawModel2["metadata"]["another"].ToString());
            }
        }

        [Fact(Skip = "Temporarily disable schema validation as Json.NET schema has limitation of 1000 calls per hour")]
        public void TestInvalidObjectAgainstSchema()
        {
            using (var listener = new TestListenerScope("TestInvalidMetadataReference"))
            {
                var schemaFile = CreateFile("template/schemas/mta.reference.test.schema.json", @"
{
  ""$schema"": ""http://dotnet.github.io/docfx/schemas/v1.0/schema.json#"",
  ""version"": ""1.0.0"",
  ""title"": ""MetadataReferenceTest"",
  ""description"": ""A simple test schema for sdp"",
  ""type"": ""object"",
  ""properties"": {
      ""metadata"": {
            ""type"": ""object""
      }
            },
  ""metadata"": ""/metadata""
}
", _templateFolder);

                var inputFile = CreateFile("invalid.yml", @"### YamlMime:MetadataReferenceTest
metadata: Web Apps Documentation
", _inputFolder);

                FileCollection files = new FileCollection(_defaultFiles);
                files.Add(DocumentType.Article, new[] { inputFile }, _inputFolder);
                Assert.Throws<InvalidSchemaException>(() => BuildDocument(files));
            }
        }

        [Fact]
        public void TestInvalidMetadataReference()
        {
            using (var listener = new TestListenerScope("TestGeneralFeaturesInSDP"))
            {
                var schemaFile = CreateFile("template/schemas/mta.reference.test.schema.json", @"
{
  ""$schema"": ""http://dotnet.github.io/docfx/schemas/v1.0/schema.json#"",
  ""version"": ""1.0.0"",
  ""title"": ""MetadataReferenceTest"",
  ""description"": ""A simple test schema for sdp"",
  ""type"": ""object"",
  ""properties"": {
      ""metadata"": {
            ""type"": ""string""
      }
            },
  ""metadata"": ""/metadata""
}
", _templateFolder);
                var inputFileName1 = "page1.yml";
                var inputFile1 = CreateFile(inputFileName1, @"### YamlMime:MetadataReferenceTest
title: Web Apps Documentation
metadata:
  title: Azure Web Apps Documentation - Tutorials, API Reference
  meta.description: Learn how to use App Service Web Apps to build and host websites and web applications.
  ms.service: app-service
  ms.tgt_pltfrm: na
  ms.author: carolz
sections:
- title: 5-Minute Quickstarts
toc_rel: ../a b/toc.md
uhfHeaderId: MSDocsHeader-DotNet
searchScope:
  - .NET
", _inputFolder);

                FileCollection files = new FileCollection(_defaultFiles);
                files.Add(DocumentType.Article, new[] { inputFile1 }, _inputFolder);
                Assert.Throws<InvalidJsonPointerException>(() => BuildDocument(files));
            }
        }

        private void BuildDocument(FileCollection files)
        {
            var parameters = new DocumentBuildParameters
            {
                Files = files,
                OutputBaseDir = _outputFolder,
                ApplyTemplateSettings = _applyTemplateSettings,
                Metadata = new Dictionary<string, object>
                {
                    ["meta"] = "Hello world!",
                }.ToImmutableDictionary(),
                TemplateManager = _templateManager,
            };

            using (var builder = new DocumentBuilder(LoadAssemblies(), ImmutableArray<string>.Empty, null))
            {
                builder.Build(parameters);
            }
        }

        private static IEnumerable<System.Reflection.Assembly> LoadAssemblies()
        {
            yield return typeof(SchemaDrivenDocumentProcessor).Assembly;
            yield return typeof(TocDocumentProcessor).Assembly;
            yield return typeof(SchemaDrivenProcessorTest).Assembly;
        }

        private string GetRawModelFilePath(string fileName)
        {
            return Path.Combine(_outputFolder, Path.ChangeExtension(fileName, RawModelFileExtension));
        }

        private string GetOutputFilePath(string fileName)
        {
            return Path.GetFullPath(Path.Combine(_outputFolder, Path.ChangeExtension(fileName, "html")));
        }
    }
}
