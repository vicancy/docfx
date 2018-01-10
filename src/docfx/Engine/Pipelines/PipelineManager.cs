// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.DocAsCode.Build.ConceptualDocuments;
using Microsoft.DocAsCode.Build.Engine;
using Microsoft.DocAsCode.Build.ManagedReference;
using Microsoft.DocAsCode.Build.TableOfContents;
using Microsoft.DocAsCode.Common;
using Microsoft.DocAsCode.DataContracts.Common;
using Microsoft.DocAsCode.DataContracts.ManagedReference;
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
    internal class PipelineManager
    {
        private BuildController _controller;

        public PipelineManager(Config config)
        {
            _controller = new BuildController((controller, file) =>
            {
                var processor = (from p in Utility.processors
                                 let priority = p.GetProcessingPriority(file)
                                 where priority != ProcessingPriority.NotSupported
                                 group p by priority into ps
                                 orderby ps.Key descending
                                 select ps.ToList()).FirstOrDefault().FirstOrDefault();
                if (processor == null)
                {
                    return null;
                }
                else if (processor is TocDocumentProcessor)
                {
                    return new TocPipeline(controller, file, config, processor).Pipeline;
                }
                else if (processor is ConceptualDocumentProcessor)
                {
                    return new ConceptualPipeline(controller, file, config, processor).Pipeline;
                }
                else
                {
                    return new MrefPipeline(controller, file, config, processor).Pipeline;
                }
            });
        }

        public Task Run(FileAndType f, Context context)
        {
            return  _controller.BuildAsync(f, context);
        }
    }
}
