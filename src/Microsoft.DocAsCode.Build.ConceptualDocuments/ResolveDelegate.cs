// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Docs.BuildCore
{
    delegate (string content, T path) ResolveContent<T>(T relativeTo, string href);

    delegate string ResolveLink<T>(T relativeTo, string href, T resultRelativeTo);
}
