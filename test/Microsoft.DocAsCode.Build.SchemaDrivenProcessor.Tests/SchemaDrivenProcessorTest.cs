﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.SchemaDrivenProcessor.Tests
{
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Web;

    using Microsoft.DocAsCode.Build.Engine;
    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.DataContracts.Common;
    using Microsoft.DocAsCode.Plugins;
    using Microsoft.DocAsCode.Tests.Common;

    using Newtonsoft.Json.Linq;
    using Xunit;
    using System.Text.RegularExpressions;

    [Trait("Owner", "lianwei")]
    [Trait("EntityType", "SchemaDrivenProcessorTest")]
    public class SchemaDrivenProcessorTest : TestBase
    {
        private const string SpecPath = @"TestData\specs\docfx_document_schema.md";
        private static Regex InputMatcher = new Regex(@"```(yml|yaml)\s*(### YamlMime:[\s\S]*?)\s*```", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static Regex SchemaMatcher = new Regex(@"```json\s*(\{\s*""\$schema""[\s\S]*?)\s*```", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private string _outputFolder;
        private string _inputFolder;
        private string _templateFolder;
        private FileCollection _defaultFiles;
        private ApplyTemplateSettings _applyTemplateSettings;
        private TemplateManager _templateManager;

        private const string RawModelFileExtension = ".raw.json";

        public SchemaDrivenProcessorTest()
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
        public void TestCaseFromSchemaSpec()
        {
            var listener = new TestLoggerListener(s => s.LogLevel > LogLevel.Info);
            Logger.RegisterListener(listener);

            var spec = File.ReadAllText(SpecPath);
            var input = InputMatcher.Match(spec).Groups[2].Value;
            var inputFileName = "landingPage1.yml";
            var inputFile = CreateFile(inputFileName, input, _inputFolder);
            File.WriteAllText(_inputFolder + "/landingPage1.yml", input);

            var schema = SchemaMatcher.Match(spec).Groups[1].Value;
            var schemaFile = CreateFile("template/schemas/landingpage.schema.json", schema, _templateFolder);
            FileCollection files = new FileCollection(_defaultFiles);
            files.Add(DocumentType.Article, new[] { inputFile }, _inputFolder);
            BuildDocument(files);

            Assert.Equal(5, listener.Items.Count);
            Assert.Equal("There is no template processing document type(s): LandingPage", listener.Items.FirstOrDefault(s => s.Message.StartsWith("There")).Message);
            Assert.Equal(4, listener.Items.Count(s => s.Message.StartsWith("Invalid file link")));

            var rawModelFilePath = GetRawModelFilePath(inputFileName);
            Assert.True(File.Exists(rawModelFilePath));
            var rawModel = JsonUtility.Deserialize<JObject>(rawModelFilePath);

            Assert.Equal($"<p sourcefile=\"{_inputFolder}/landingPage1.yml\" sourcestartlinenumber=\"1\" sourceendlinenumber=\"1\">Create an application using <a href=\"app-service-web-tutorial-dotnet-sqldatabase.md\" data-raw-source=\"[.NET with Azure SQL DB](app-service-web-tutorial-dotnet-sqldatabase.md)\" sourcefile=\"{_inputFolder}/landingPage1.yml\" sourcestartlinenumber=\"1\" sourceendlinenumber=\"1\">.NET with Azure SQL DB</a> or <a href=\"app-service-web-tutorial-nodejs-mongodb-app.md\" data-raw-source=\"[Node.js with MongoDB](app-service-web-tutorial-nodejs-mongodb-app.md)\" sourcefile=\"{_inputFolder}/landingPage1.yml\" sourcestartlinenumber=\"1\" sourceendlinenumber=\"1\">Node.js with MongoDB</a></p>\n"
                , rawModel["sections"][1]["children"][0]["content"].ToString());
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
