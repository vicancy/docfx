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

        private object _value = null;
        private int _require;
        public string Name { get; }
        public Type Type { get; }
        public BuildStep(Type type)
        {
            Type = type;
        }

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

        public async Task Report<T>(T result)
        {
            _value = result;
            _workCompleted.TrySetResult(result);
            await _moveNext.Task;
        }

        public Task WorkTask =>
            _workCompleted.Task;

        public async Task<T> GetWorkTask<T>() => (T)(await _workCompleted.Task);
    }

    internal class BuildPipeline
    {
        private readonly object _syncRoot = new object();
        private volatile Task _current;
        public BuildController Controller { get; }
        public BuildStep[] Steps { get; }
        private Func<BuildPipeline, Context, Task> CreateTask { get; }
        public Task Current => _current;

        public BuildPipeline(BuildController controller, string[] steps, Type[] types, Func<BuildPipeline, Context, Task> func)
        {
            Controller = controller;
            Steps = steps.Select(s => new BuildStep(s)).Concat(types.Select(t => new BuildStep(t))).ToArray();// Array.ConvertAll(steps, n => new BuildStep(n));
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

        public Task<T> RequireCore<T>(Context context, FileAndType file)
        {
            for (int i = 0; i < Steps.Length; i++)
            {
                var step = Steps[i];
                if (step.Type == typeof(T))
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
                    return step.GetWorkTask<T>();
                }
            }
            throw new InvalidOperationException("Step not found.");
        }

        public Task Require(string step, Context context, FileAndType file) =>
            Controller.BuildAsync(file, step, context);

        public Task Require(string step, Context context, params FileAndType[] files) =>
            Task.WhenAll(Array.ConvertAll(files, file => Controller.BuildAsync(file, step, context)));

        public Task<T[]> Require<T>(Context context, params FileAndType[] files) =>
            Task.WhenAll(Array.ConvertAll(files, file => Controller.BuildAsync<T>(file, context)));

        public Task<T> Require<T>(Context context, FileAndType file) =>
            Controller.BuildAsync<T>(file, context);

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

        internal Task Report<T>(T val) => Steps.First(s => s.Type == val.GetType()).Report<T>(val);
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

        public Task<T> BuildAsync<T>(FileAndType file, Context context)
        {
            var pipeline = _dictionary.GetOrAdd(file, f => _creator(this, f));
            if (pipeline == null)
            {
                return Task.FromResult<T>(default);
            }
            return pipeline.RequireCore<T>(context, file);
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
