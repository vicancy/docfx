// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.SubCommands
{
    using Microsoft.DocAsCode;
    using Microsoft.DocAsCode.Plugins;

    [CommandOption("watch", "Host a local static website")]
    internal sealed class WatchCommandCreator : CommandCreator<WatchCommandOptions, WatchCommand>
    {
        public override WatchCommand CreateCommand(WatchCommandOptions options, ISubCommandController controller)
        {
            return new WatchCommand(options);
        }
    }
}