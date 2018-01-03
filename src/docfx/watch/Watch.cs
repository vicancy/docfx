// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Docs.Build
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.DocAsCode;
    using Microsoft.DocAsCode.Build.Engine;
    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.SubCommands;
    using Microsoft.Docs.BuildCore;
    using Newtonsoft.Json.Linq;

    static class Watch
    {
        static readonly JObject[] s_watchConfig = new[]
        {
            new JObject { ["output"] = new JObject { ["stable"] = false } }
        };

        enum DocumentType
        {
            MREF,
            TOC,
            MD
        }
        private static async Task Build(string file, DocumentType type, string basedir)
        {
            string srcFile ;
            if (type == DocumentType.TOC)
            {
                // TODO: use glob for file naming "(filewithoutextension.*)"
                // use file mapping if target folder changes
                srcFile = Path.ChangeExtension(file, ".json");
            }
            else if (type == DocumentType.MREF)
            {
                srcFile = file + ".json";
            }
            else
            {
                srcFile = file;
            }

            if (GloballySharedContext.Engine != null)
            {
                var fc = new FileCollection(basedir);
                fc.Add(DocAsCode.Plugins.DocumentType.Article, new string[] { srcFile });

                await GloballySharedContext.Engine.BuildInscope(fc.EnumerateFiles(), fc, 1);
            }
            else
            {
                var option = new BuildCommandOptions()
                {
                    ChangesFile = srcFile,
                };

                new BuildCommand(option).Exec(new DocAsCode.Plugins.SubCommandRunningContext());
            }
        }

        public static async Task Run(string docsetPath, params JObject[] configOverride)
        {
            var output = new ConcurrentDictionary<string, (string path, string content)>(StringComparer.OrdinalIgnoreCase);

            Logger.LogInfo("Launching web server");

            using (var host = Hosting.Create(ReadDocument, ReadTemplate, 56788))
            {
                host.Start();

                await LaunchWebServer(host.ServerFeatures.GetAddress());
            }

            async Task<(Stream stream, bool isDynamicRender)> ReadDocument(string sitePath)
            {
                // read html mapping from context?
                Debug.Assert(sitePath.StartsWith("docs/"));
                var filePath = sitePath.Substring("docs/".Length);

                string extension;
                bool dr = true;
                if (sitePath.EndsWith("toc.json", StringComparison.OrdinalIgnoreCase))
                {
                    extension = "";
                    dr = false;
                    var outputPath = Path.Combine(docsetPath, filePath) + extension;
                    if (!File.Exists(outputPath))
                    {
                        await Build(filePath, DocumentType.TOC, docsetPath);
                    }
                }
                else
                {
                    extension = ".raw.page.json";
                    var outputPath = Path.Combine(docsetPath, filePath) + extension;
                    if (!File.Exists(outputPath))
                    {
                        await Build(filePath, DocumentType.MREF, docsetPath);
                    }
                }
                {
                    var outputPath = Path.Combine(docsetPath, filePath) + extension;
                    if (!File.Exists(outputPath))
                    {
                        Logger.LogError("Unable to find " + outputPath);
                        return (Stream.Null, false);
                    }
                    Logger.LogInfo("Loading " + outputPath);
                    // File.OpenRead(outputPath)
                    var stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(outputPath)));
                    return (stream, dr);
                }
            }

            async Task<Stream> ReadTemplate(string sitePath)
            {
                var home = Path.GetDirectoryName(docsetPath);
                var templatePath = Path.Combine(home, "_themes", sitePath);
                if (!File.Exists(templatePath))
                {
                    Logger.LogError("Unable to find " + templatePath);
                    return Stream.Null;
                }

                Logger.LogInfo("Loading " + templatePath);
                return File.Exists(templatePath) ? File.OpenRead(templatePath) : null;
            }

            Func<string> WriteOutput(string path, string content)
            {
                output.TryAdd(path, (null, content));
                return () => output[path].content;
            }

            void CopyOutput(string src, string dest)
            {
                output.TryAdd(dest, (src, null));
            }
        }

        static Task<int> LaunchWebServer(string hostUrl)
        {
            var port = 56789;
            var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "App.exe" : "App";
            var exeDir = @"E:\Repo1\ops-cli\bin\win7-x64\web";
            var exe = Path.Combine(exeDir, name);
            var serverDir = @"E:\Repo1\ops-cli\dep\Docs.Rendering\Source\App";
            var serverArgs = "run --no-launch-profile --no-build --no-restore";
            var psi = File.Exists(exe)
                ? new ProcessStartInfo { FileName = exe, WorkingDirectory = exeDir, }
                : new ProcessStartInfo { FileName = "dotnet", WorkingDirectory = serverDir, Arguments = serverArgs };

            psi.UseShellExecute = false;
            psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
            psi.EnvironmentVariables["ASPNETCORE_URLS"] = $"http://*:{port}";
            psi.EnvironmentVariables["APPSETTING_DocumentHostingServiceClientOptions__BaseUri"] = hostUrl;
            psi.EnvironmentVariables["APPSETTING_DocumentHostingServiceApiAccessKey"] = "c2hvd21ldGhlbW9uZXk=";

            var tcs = new TaskCompletionSource<int>();
            var process = Process.Start(psi);
            process.EnableRaisingEvents = true;
            process.Exited += (a, b) => tcs.TrySetResult(process.ExitCode);
            return tcs.Task;
        }
    }
}
