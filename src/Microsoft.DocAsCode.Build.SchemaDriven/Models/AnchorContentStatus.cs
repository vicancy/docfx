// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.SchemaDriven
{
    public class AnchorContentStatus
    {
        public const string AnchorContentName = "*content";
        public const string DefaultContentPropertyName = "conceptual";
        public bool ContainsAnchor { get; set; }
        public string Content { get; set; }
    }
}
