namespace Microsoft.DocAsCode.Common.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Xunit;

    public class TaskZTest
    {
        private List<string> _log = new List<string>();

        [Fact]
        public async Task TestZ()
        {
            var controller = new BuildController(CreateBuildPipeline);
            //await Task.WhenAll(from i in Enumerable.Range(0, 1000) select controller.BuildAsync(i.ToString()));
            await controller.BuildAsync("11100000");
            controller.StopAll();
        }

        public BuildPipeline CreateBuildPipeline(BuildController controller, string file) =>
            new BuildPipeline(
                controller,
                file,
                new[] { "AST", "Xref", "Save" },
                BuildDocument);

        private async Task BuildDocument(BuildPipeline p)
        {
            var number = int.Parse(p.File);
            Log($"File {p.File} Runing AST...");
            await p.Report("AST");
            if (number % 3 == 0)
            {
                var b = (number / 2).ToString();
                Log($"File {p.File} Require {b} AST...");
                await p.Require("AST", b);
            }
            Log($"File {p.File} Runing Xref...");
            await p.Report("Xref");
            if (number % 5 == 0)
            {
                var c = (number / 3).ToString();
                var d = (number / 4).ToString();
                Log($"File {p.File} Require {c}, {d} Xref...");
                await p.Require("Xref", c, d);
            }
            Log($"File {p.File} Runing Save...");
            await p.Report("Save");
        }

        private void Log(string message)
        {
            lock (_log)
            {
                _log.Add(message);
            }
        }

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

        public class BuildPipeline
        {
            private readonly object _syncRoot = new object();
            private volatile Task _current;
            public BuildController Controller { get; }
            public string File { get; }
            public BuildStep[] Steps { get; }
            private Func<BuildPipeline, Task> CreateTask { get; }
            public Task Current => _current;

            public BuildPipeline(BuildController controller, string file, string[] steps, Func<BuildPipeline, Task> func)
            {
                Controller = controller;
                File = file;
                Steps = Array.ConvertAll(steps, n => new BuildStep(n));
                CreateTask = func;
            }

            internal Task EnsureTask()
            {
                lock (_syncRoot)
                {
                    var current = _current;
                    if (current == null || current.IsCanceled)
                    {
                        current = Task.Run(() => CreateTask(this));
                        _current = current;
                    }
                    return current;
                }
            }

            internal Task EnsureTask(string stepName)
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
                        _current = Task.Run(() => CreateTask(this));
                    }
                    return Steps[index].WorkTask;
                }
            }

            public Task Require(string step, string file) =>
                Controller.BuildAsync(file, step);

            public Task Require(string step, params string[] files) =>
                Task.WhenAll(Array.ConvertAll(files, file => Controller.BuildAsync(file, step)));

            internal Task Require()
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
                    return EnsureTask();
                }
                return Current;
            }

            internal Task Require(string name)
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
                            EnsureTask();
                        }
                        return step.WorkTask;
                    }
                }
                throw new InvalidOperationException("Step not found.");
            }

            internal Task Report(string name) =>
                Steps.First(s => s.Name == name).Report();
        }

        public class BuildController
        {
            private readonly ConcurrentDictionary<string, BuildPipeline> _dictionary =
                new ConcurrentDictionary<string, BuildPipeline>();
            private readonly Func<BuildController, string, BuildPipeline> _creator;

            public BuildController(Func<BuildController, string, BuildPipeline> creator)
            {
                _creator = creator;
            }

            public Task BuildAsync(string file)
            {
                var pipeline = _dictionary.GetOrAdd(file, f => _creator(this, f));
                return pipeline.Require();
            }

            public Task BuildAsync(string file, string stepName)
            {
                var pipeline = _dictionary.GetOrAdd(file, f => _creator(this, f));
                return pipeline.Require(stepName);
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
}
