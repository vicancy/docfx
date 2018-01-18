// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.SubCommands
{
    using System;
    using System.IO;

    using Microsoft.DocAsCode;
    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.Plugins;
    using Owin.StaticFiles;
    using Owin.FileSystems;
    using Owin.Hosting;
    using global::Owin;
    using System.Threading.Tasks;
    using Microsoft.Docs.Build;

    internal sealed class WatchCommand : ISubCommand
    {
        private readonly WatchCommandOptions _options;
        public bool AllowReplay => false;

        public string Name { get; } = nameof(WatchCommand);

        public WatchCommand(WatchCommandOptions options)
        {
            _options = options;
        }

        public void Exec(SubCommandRunningContext context)
        {
            _options.ChangesFile = string.Empty;
            var buildCommand = new BuildCommand(_options as BuildCommandOptions);

            buildCommand.Exec(context);

            var folder = EnvironmentContext.OutputDirectory;
            Serve(folder,
                _options.Host,
                _options.Port.HasValue ? _options.Port.Value.ToString() : null);
        }

        public static void Serve(string folder, string host, string port)
        {
            if (string.IsNullOrEmpty(folder)) folder = Directory.GetCurrentDirectory();
            folder = Path.GetFullPath(folder);
            host = string.IsNullOrWhiteSpace(host) ? "localhost" : host;
            port = string.IsNullOrWhiteSpace(port) ? "8080" : port;
            var url = $"http://{host}:{port}";
            if (!Directory.Exists(folder))
            {
                throw new ArgumentException("Site folder does not exist. You may need to build it first. Example: \"docfx docfx_project/docfx.json\"", nameof(folder));
            }
            var fileServerOptions = new FileServerOptions
            {
                EnableDirectoryBrowsing = true,
                FileSystem = new PhysicalFileSystem(folder),
            };

            // Fix the issue that .JSON file is 404 when running docfx serve
            fileServerOptions.StaticFileOptions.ServeUnknownFileTypes = true;

            if (!File.Exists(Path.Combine(folder, "index.html")) && File.Exists(Path.Combine(folder, "toc.html")))
            {
                File.Copy(Path.Combine(folder, "toc.html"), Path.Combine(folder, "index.html"));
            }

            try
            {
                // WebApp.Start(url, builder => builder.UseFileServer(fileServerOptions));
                var task = Task.Run(() => Watch.Run(folder));
                task.Wait();
                Console.WriteLine($"Serving \"{folder}\" on {url}");
                Console.ReadLine();
            }
            catch (System.Reflection.TargetInvocationException)
            {
                Logger.LogError($"Error serving \"{folder}\" on {url}, check if the port is already being in use.");
            }
        }
    }
}
