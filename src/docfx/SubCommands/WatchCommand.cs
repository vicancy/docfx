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
    using Newtonsoft.Json.Linq;

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

            var folder = buildCommand.OutputDirectory;
            try
            {
                // WebApp.Start(url, builder => builder.UseFileServer(fileServerOptions));
                var task = Task.Run(() => Watch.Run(buildCommand.BaseDirectory, new JObject
                {
                    ["output"] = folder
                }));
                task.Wait();
            }
            catch (Exception e)
            {
                Logger.LogError(e.Message);
                throw;
            }
        }
    }
}
