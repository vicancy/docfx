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

    public class TaskYTest
    {
        [Fact]
        public async Task TestY()
        {
            var sw = new Stopwatch();
            var bag = new List<(int, long)>();
            var tasks = new TaskY[1000000];
            for (int i = 0; i < tasks.Length; i++)
            {
                var j = i;
                tasks[i] = new TaskY(
                    $"task-{i}",
                    async self =>
                    {
                        if (j >= 100000)
                        {
                            await self.DependOn(tasks[j - 100000]);
                        }
                        //await Task.Delay(1);
                        if (j > 2000)
                        {
                            //await self.DependOn((from x in Enumerable.Range(1001, 10) select tasks[j - x]).ToArray());
                            await self.DependOn((from x in Enumerable.Range(2, 10) select tasks[j / x]).Distinct().ToArray());
                        }
                        lock (bag)
                        {
                            bag.Add((j, sw.ElapsedMilliseconds));
                        }
                    });
            }
            // full build
            //var result = new TaskY("result", tasks.Skip(1000000 - 100000).ToArray());
            // watch
            var result = new TaskY("result", tasks[999999], tasks[959999]);
            sw.Start();
            await result.RunAsync();
            await TaskSchedulerY.Instance.WhenComplete();
            sw.Stop();
            Console.WriteLine(sw.ElapsedMilliseconds);
            // full build
            //Assert.Equal(1000000, bag.Count);
            // watch
            Assert.Equal(1956, bag.Count);
        }

        #region MyRegion
        public async Task TestSln()
        {

        }

        private class TaskTableItem
        {
            public string Name;
            public TaskY Load;
            public TaskY Ast;
            public TaskY XrefBasic;
            public TaskY XrefAdvance;
            public TaskY Bookmark;
            public TaskY Output;
        }

        private TaskTableItem GenerateTasks(string file, string content)
        {
            var result = new TaskTableItem { Name = file };
            result.Load = new TaskY(file + "-Load", async self => { });
            result.Ast = new TaskY(file + "-AST", async self => { }, result.Load);
            result.XrefBasic = new TaskY(file + "-XrefBasic", async self => { }, result.Load);
            result.XrefAdvance = new TaskY(file + "-XrefAdvance", async self => { }, result.Ast);
            result.Bookmark = new TaskY(file + "-Bookmark", async self => { }, result.Ast);
            result.Output = new TaskY(file + "-Output", async self => { }, result.XrefAdvance);
            return result;
        }
        #endregion
    }

    public sealed class TaskSchedulerY
    {
        private const int MaxParallelism = 4;
        private const int MaxQueueLength = MaxParallelism * 4;
        public static readonly TaskSchedulerY Instance = new TaskSchedulerY();

        private readonly object _syncRoot = new object();
        private readonly SemaphoreSlim _semaphore =
            new SemaphoreSlim(MaxParallelism);
        private readonly List<TaskY> _allTasks = new List<TaskY>();
        private readonly ConcurrentQueue<TaskY> _committingTasks = new ConcurrentQueue<TaskY>();
        private readonly List<TaskY> _committedTasks = new List<TaskY>();
        private int _id;
        private Task _runTask;
        private int _suspendCount = 0;
        private int _todoCount = 0;
        private int _sortedRetrieveCount;
        private int _fastPassRetrieveCount;

        internal int Register(TaskY task)
        {
            lock (_allTasks)
            {
                _allTasks.Add(task);
            }
            task.onRunnable = t => _committingTasks.Enqueue(t);
            return Interlocked.Increment(ref _id);
        }

        internal void EnsureRun()
        {
            lock (_syncRoot)
            {
                if (_runTask == null)
                {
                    _runTask = Task.Run(RunAsync);
                }
            }
        }

        public Task WhenComplete()
        {
            Thread.MemoryBarrier();
            return _runTask ?? Task.CompletedTask;
        }

        private async void BackgroundCommit(ManualResetEventSlim exitEvent)
        {
            while (true)
            {
                if (_committingTasks.Count > 0)
                {
                    lock (_committedTasks)
                    {
                        while (_committingTasks.TryDequeue(out var item))
                        {
                            _committedTasks.Add(item);
                        }
                        _committedTasks.Sort((x, y) => x.Weight - y.Weight);
                        _todoCount = _committedTasks.Count;
                    }
                }
                else
                {
                    if (exitEvent.IsSet)
                    {
                        return;
                    }
                }
                await Task.Delay(1);
            }
        }

        private async Task RunAsync()
        {
            try
            {
                await RunCoreAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private async Task RunCoreAsync()
        {
            var exitEvent = new ManualResetEventSlim();
            BackgroundCommit(exitEvent);
            int turn = 0;
            while (true)
            {
                TaskY taskToRun = null;
                if (Thread.VolatileRead(ref _todoCount) > 0)
                {
                    lock (_committedTasks)
                    {
                        if (_committedTasks.Count > 0)
                        {
                            taskToRun = _committedTasks[_committedTasks.Count - 1];
                            _committedTasks.RemoveAt(_committedTasks.Count - 1);
                            _todoCount = _committedTasks.Count;
                        }
                    }
                }
                if (taskToRun == null)
                {
                    Interlocked.Increment(ref _fastPassRetrieveCount);
                    _committingTasks.TryDequeue(out taskToRun);
                }
                else
                {
                    Interlocked.Increment(ref _sortedRetrieveCount);
                }
                if (taskToRun == null)
                {
                    if (_semaphore.CurrentCount == MaxParallelism && Thread.VolatileRead(ref _suspendCount) == 0)
                    {
                        if (turn < 1)
                        {
                            turn++;
                        }
                        else
                        {
                            lock (_syncRoot)
                            {
                                _runTask = null;
                            }
                            exitEvent.Set();
                            break;
                        }
                    }
                    else
                    {
                        turn = 0;
                    }
                    await Task.Delay(1);
                    continue;
                }
                turn = 0;
                await _semaphore.WaitAsync();
                taskToRun.Status = TaskYStatus.Running;
                if (!taskToRun.Run())
                {
                    _semaphore.Release();
                }
            }
        }

        internal void Complete(TaskY task)
        {
            task.Status = TaskYStatus.Completed;
            _semaphore.Release();
        }

        internal void Suspend()
        {
            Interlocked.Increment(ref _suspendCount);
            _semaphore.Release();
        }

        internal async Task Resume()
        {
            await _semaphore.WaitAsync();
            Interlocked.Decrement(ref _suspendCount);
        }
    }

    public enum TaskYStatus
    {
        Created,
        WaitingToRun,
        PlanningToRun,
        Running,
        WaitingDependency,
        Completed,
    }

    public sealed class TaskY
    {
        private readonly Func<TaskY, Task> _func;
        private readonly TaskY[] _dependencies;
        private int _weight;
        public int Id { get; }
        public string Name { get; }
        public TaskYStatus Status { get; internal set; }
        private Task WorkTask { get; }
        public int Weight => _weight;
        private Action<int> WeightChanged;
        internal Action<TaskY> onRunnable;
        private readonly TaskCompletionSource<object> _schedule =
            new TaskCompletionSource<object>();

        public TaskY(string name, params TaskY[] dependencies)
            : this(name, self => Task.CompletedTask, dependencies) { }

        public TaskY(string name, Func<TaskY, Task> func, params TaskY[] dependencies)
        {
            Name = name;
            _func = func;
            _dependencies = dependencies?.Where(d => d != null).ToArray() ?? Array.Empty<TaskY>();
            Status = TaskYStatus.Created;
            Id = TaskSchedulerY.Instance.Register(this);
            WorkTask = ToTask(this);
            foreach (var d in _dependencies)
            {
                WeightChanged += d.OnWeightChanged;
            }
        }

        private void OnWeightChanged(int weight)
        {
            if (_weight > 1000)
            {
                return;
            }
            Interlocked.Add(ref _weight, weight);
            WeightChanged?.Invoke(weight);
            CheckRunnable();
        }

        internal bool Run()
        {
            return _schedule.TrySetResult(null);
        }

        private void CheckRunnable()
        {
            lock (this)
            {
                if (Weight > 0 && Status == TaskYStatus.WaitingToRun)
                {
                    onRunnable(this);
                    Status = TaskYStatus.PlanningToRun;
                }
            }
        }

        private async static Task ToTask(TaskY task)
        {
            await Task.WhenAll(Array.ConvertAll(task._dependencies, t => t.WorkTask));
            task.Status = TaskYStatus.WaitingToRun;
            task.CheckRunnable();
            await task._schedule.Task;
            await Task.Yield();
            try
            {
                await task._func(task);
            }
            finally
            {
                TaskSchedulerY.Instance.Complete(task);
            }
        }

        public async Task DependOn(params TaskY[] tasks)
        {
            foreach (var d in tasks)
            {
                d.OnWeightChanged(Weight);
                WeightChanged += d.OnWeightChanged;
            }
            Status = TaskYStatus.WaitingDependency;
            TaskSchedulerY.Instance.Suspend();
            await Task.WhenAll(from task in tasks select task.WorkTask);
            await TaskSchedulerY.Instance.Resume();
            Status = TaskYStatus.Running;
        }

        public async Task RunAsync()
        {
            OnWeightChanged(1);
            TaskSchedulerY.Instance.EnsureRun();
            await WorkTask;
        }

        public override string ToString()
        {
            return $"{Id}:{Name}, S:{Status}, W:{Weight}";
        }
    }
}
