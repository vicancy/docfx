// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.DocAsCode.Build.ConceptualDocuments;
using Microsoft.DocAsCode.Build.Engine;
using Microsoft.DocAsCode.Build.ManagedReference;
using Microsoft.DocAsCode.Build.TableOfContents;
using Microsoft.DocAsCode.Common;
using Microsoft.DocAsCode.Exceptions;
using Microsoft.DocAsCode.Plugins;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.DocAsCode
{
    public class BuildStep
    {
        private readonly object _syncRoot = new object();
        private volatile TaskCompletionSource<object> _workCompleted =
            new TaskCompletionSource<object>();
        private volatile TaskCompletionSource<object> _moveNext =
            new TaskCompletionSource<object>();
        private int _require;
        public string Name { get; }

        public BuildStep(string name)
        {
            Name = name;
        }

        internal bool EnsureResumable()
        {
            if (_moveNext.TrySetResult(null))
            {
                return true;
            }
            lock (_syncRoot)
            {
                if (_moveNext.Task.Status == TaskStatus.Canceled)
                {
                    _moveNext = new TaskCompletionSource<object>(null);
                    return false;
                }
            }
            return true;
        }

        internal bool StopOnComplete()
        {
            if (Thread.VolatileRead(ref _require) > 0)
            {
                return false;
            }
            return _moveNext.TrySetCanceled();
        }

        internal void Require()
        {
            Interlocked.Increment(ref _require);
        }

        public async Task Report()
        {
            _workCompleted.TrySetResult(null);
            await _moveNext.Task;
        }

        public Task WorkTask =>
            _workCompleted.Task;
    }

    internal class BuildPipeline
    {
        private readonly object _syncRoot = new object();
        private volatile Task _current;
        public BuildController Controller { get; }
        public BuildStep[] Steps { get; }
        private Func<BuildPipeline, Context, Task> CreateTask { get; }
        public Task Current => _current;

        public BuildPipeline(BuildController controller, string[] steps, Func<BuildPipeline, Context, Task> func)
        {
            Controller = controller;
            Steps = Array.ConvertAll(steps, n => new BuildStep(n));
            CreateTask = func;
        }

        internal Task EnsureTask(Context context)
        {
            lock (_syncRoot)
            {
                var current = _current;
                if (current == null || current.IsCanceled)
                {
                    current = Task.Run(() => CreateTask(this, context));
                    _current = current;
                }
                return current;
            }
        }

        internal Task EnsureTask(string stepName, Context context)
        {
            lock (_syncRoot)
            {
                var index = Array.FindIndex(Steps, s => s.Name == stepName);
                if (index == -1)
                {
                    throw new InvalidOperationException();
                }
                bool restartTask = false;
                for (int i = 0; i < index - 1; i++)
                {
                    if (!Steps[i].EnsureResumable())
                    {
                        restartTask = true;
                    }
                }
                if (restartTask)
                {
                    _current = Task.Run(() => CreateTask(this, context));
                }
                return Steps[index].WorkTask;
            }
        }

        public Task Require(string step, Context context, FileAndType file) =>
            Controller.BuildAsync(file, step, context);

        public Task Require(string step, Context context, params FileAndType[] files) =>
            Task.WhenAll(Array.ConvertAll(files, file => Controller.BuildAsync(file, step, context)));

        internal Task Require(Context context)
        {
            bool restart = false;
            for (int i = 0; i < Steps.Length; i++)
            {
                Steps[i].Require();
                if (!Steps[i].EnsureResumable())
                {
                    restart = true;
                }
            }
            if (restart || Current == null)
            {
                return EnsureTask(context);
            }
            return Current;
        }

        internal Task Require(string name, Context context)
        {
            for (int i = 0; i < Steps.Length; i++)
            {
                var step = Steps[i];
                if (step.Name == name)
                {
                    step.Require();
                    bool restart = false;
                    for (int j = 0; j < i; j++)
                    {
                        Steps[j].Require();
                        if (!Steps[j].EnsureResumable())
                        {
                            restart = true;
                        }
                    }
                    if (restart || Current == null)
                    {
                        EnsureTask(context);
                    }
                    return step.WorkTask;
                }
            }
            throw new InvalidOperationException("Step not found.");
        }

        internal Task Report(string name) =>
            Steps.First(s => s.Name == name).Report();
    }

    internal class BuildController
    {
        private readonly ConcurrentDictionary<FileAndType, BuildPipeline> _dictionary =
            new ConcurrentDictionary<FileAndType, BuildPipeline>();
        private readonly Func<BuildController, FileAndType, BuildPipeline> _creator;

        public BuildController(Func<BuildController, FileAndType, BuildPipeline> creator)
        {
            _creator = creator;
        }

        public Task BuildAsync(FileAndType file, Context context)
        {
            var pipeline = _dictionary.GetOrAdd(file, f => _creator(this, f));
            if (pipeline == null)
            {
                return Task.CompletedTask;
            }

            return pipeline.Require(context);
        }

        public Task BuildAsync(FileAndType file, string stepName, Context context)
        {
            var pipeline = _dictionary.GetOrAdd(file, f => _creator(this, f));
            if (pipeline == null)
            {
                return Task.CompletedTask;
            }
            return pipeline.Require(stepName, context);
        }

        public void StopAll()
        {
            foreach (var pipeline in _dictionary.Values)
            {
                foreach (var step in pipeline.Steps)
                {
                    step.StopOnComplete();
                }
            }
        }
    }
}
