namespace Simple.Wpf.Template.Services
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Linq;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Windows.Media;
    using Models;
    using NLog;

    public sealed class DiagnosticsService : IDiagnosticsService, IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly ISchedulerService _schedulerService;
        private readonly CompositeDisposable _disposable;
        private readonly IConnectableObservable<Counters> _countersObservable;
        private readonly IConnectableObservable<int> _fpsObservable;
        private readonly Queue<long> _fpsQueue;
        private readonly object _sync;

        private bool _fpsConnected;
        private bool _countersConnected;

        internal sealed class Counters
        {
            public Counters(PerformanceCounter workingSetCounter, PerformanceCounter cpuCounter)
            {
                WorkingSet = workingSetCounter;
                Cpu = cpuCounter;
            }

            public PerformanceCounter WorkingSet { get; private set; }

            public PerformanceCounter Cpu { get; private set; }
        }

        public DiagnosticsService(IIdleService idleService, ISchedulerService schedulerService)
        {
            _schedulerService = schedulerService;

            _disposable = new CompositeDisposable();

            _sync = new object();

            _countersObservable = Observable.Create<Counters>(x =>
            {
                var disposable = new CompositeDisposable();

                try
                {
                    var processName = GetProcessInstanceName();

                    Logger.Info("Creating performance counter 'Working Set'");

                    var workingSetCounter = new PerformanceCounter("Process", "Working Set", processName);
                    disposable.Add(workingSetCounter);

                    Logger.Info("Creating performance counter '% Processor Time'");

                    var cpuCounter = new PerformanceCounter("Process", "% Processor Time", processName);
                    disposable.Add(cpuCounter);

                    using (Duration.Measure(Logger, "Initialising created performance counters"))
                    {
                        workingSetCounter.NextValue();
                        cpuCounter.NextValue();
                    }

                    x.OnNext(new Counters(workingSetCounter, cpuCounter));

                    Logger.Info("Ready");
                }
                catch (ArgumentException exn)
                {
                    LogFailToCreatePerformanceCounter(x, exn);
                }
                catch (InvalidOperationException exn)
                {
                    LogFailToCreatePerformanceCounter(x, exn);
                }
                catch (Win32Exception exn)
                {
                    LogFailToCreatePerformanceCounter(x, exn);
                }
                catch (PlatformNotSupportedException exn)
                {
                    LogFailToCreatePerformanceCounter(x, exn);
                }
                catch (UnauthorizedAccessException exn)
                {
                    LogFailToCreatePerformanceCounter(x, exn);
                }

                return disposable;
            })
                .DelaySubscription(Constants.DiagnosticsSubscriptionDelay, schedulerService.TaskPool)
                .SubscribeOn(schedulerService.TaskPool)
                .ObserveOn(schedulerService.TaskPool)
                .CombineLatest(idleService.Idling.Buffer(Constants.DiagnosticsIdleBuffer, schedulerService.TaskPool).Where(x => x.Any()), (x, y) => x)
                .Replay(1);

            _fpsQueue = new Queue<long>();
            _fpsObservable = Observable.FromEventPattern<EventHandler, EventArgs>(h => CompositionTarget.Rendering += h,
                    h => CompositionTarget.Rendering -= h)
                    .Synchronize()
                    .Select(x => CalculateFps())
                    .Publish();
        }

        public void Dispose()
        {
            using (Duration.Measure(Logger, "Dispose"))
            {
                _disposable.Dispose();
            }
        }

        public IObservable<Memory> Memory
        {
            get
            {
                ConnectCountersObservable();

                return _countersObservable.Select(CalculateMemoryValues)
                    .DistinctUntilChanged();
            }
        }

        public IObservable<int> Cpu
        {
            get
            {
                ConnectCountersObservable();

                return _countersObservable.Select(CalculateCpu)
                    .Delay(Constants.DiagnosticsCpuBuffer, _schedulerService.TaskPool)
                    .DistinctUntilChanged()
                    .Select(DivideByNumberOfProcessors);
            }
        }

        public IObservable<int> Fps
        {
            get
            {
                ConnectFpsObservable();
                
                return _fpsObservable.DistinctUntilChanged();
            }
        }
        
        private static void LogFailToCreatePerformanceCounter(IObserver<Counters> counters, Exception exception)
        {
            Logger.Error("Failed to create performance counters!");
            Logger.Error(exception);
            counters.OnError(exception);
        }

        private static void LogFailToCalculateMemory(Exception exception)
        {
            Logger.Warn("Failed to calculate memory!");
            Logger.Warn(exception);
        }

        private static void LogFailToCalculateCpu(Exception exception)
        {
            Logger.Warn("Failed to calculate cpu!");
            Logger.Warn(exception);
        }

        private static Memory CalculateMemoryValues(Counters counters)
        {
            try
            {
                var rawValue = counters.WorkingSet.NextValue();
                var privateWorkingSet = Convert.ToDecimal(rawValue);
                
                var managed = GC.GetTotalMemory(false);

                return new Memory(privateWorkingSet, managed);
            }
            catch (InvalidOperationException exn)
            {
                LogFailToCalculateMemory(exn);
                return new Memory(0, 0);
            }
            catch (Win32Exception exn)
            {
                LogFailToCalculateMemory(exn);
                return new Memory(0, 0);
            }
            catch (PlatformNotSupportedException exn)
            {
                LogFailToCalculateMemory(exn);
                return new Memory(0, 0);
            }
            catch (UnauthorizedAccessException exn)
            {
                LogFailToCalculateMemory(exn);
                return new Memory(0, 0);
            }
            catch (OverflowException exn)
            {
                LogFailToCalculateMemory(exn);
                return new Memory(0, 0);
            }
        }

        private static int CalculateCpu(Counters counters)
        {
            try
            {
                var rawValue = counters.Cpu.NextValue();
                return Convert.ToInt32(rawValue);
            }
            catch (InvalidOperationException exn)
            {
                LogFailToCalculateCpu(exn);
                return 0;
            }
            catch (Win32Exception exn)
            {
                LogFailToCalculateCpu(exn);
                return 0;
            }
            catch (PlatformNotSupportedException exn)
            {
                LogFailToCalculateCpu(exn);
                return 0;
            }
            catch (UnauthorizedAccessException exn)
            {
                LogFailToCalculateCpu(exn);
                return 0;
            }
            catch (OverflowException exn)
            {
                LogFailToCalculateCpu(exn);
                return 0;
            }
        }

        private static int DivideByNumberOfProcessors(int value)
        {
            try
            {
                return value == 0 ? 0 : value / Environment.ProcessorCount;
            }
            catch (OverflowException)
            {
                return 0;
            }
        }

        private static string GetProcessInstanceName()
        {
            var currentProcess = Process.GetCurrentProcess();
            foreach (var instance in new PerformanceCounterCategory("Process").GetInstanceNames()
                .Where(x => x.StartsWith(currentProcess.ProcessName, StringComparison.InvariantCulture)))
            {
                try
                {
                    using (var counter = new PerformanceCounter("Process", "ID Process", instance, true))
                    {
                        var val = (int)counter.RawValue;
                        if (val == currentProcess.Id)
                        {
                            return instance;
                        }
                    }
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }
                catch (PlatformNotSupportedException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            throw new ArgumentException(@"Could not find performance counter instance name for current process, name '{0}'", currentProcess.ProcessName);
        }

        private void ConnectCountersObservable()
        {
            if (_countersConnected)
            {
                return;
            }

            lock (_sync)
            {
                if (_countersConnected)
                {
                    return;
                }

                var disposable = _countersObservable.Connect();
                _disposable.Add(disposable);

                _countersConnected = true;
            }
        }

        private void ConnectFpsObservable()
        {
            if (_fpsConnected)
            {
                return;
            }

            lock (_sync)
            {
                if (_fpsConnected)
                {
                    return;
                }

                var disposable = _fpsObservable.Connect();
                _disposable.Add(Disposable.Create(() =>
                {
                    disposable.Dispose();
                    _fpsQueue.Clear();
                }));

                _fpsConnected = true;
            }
        }

        private int CalculateFps()
        {
            var now = DateTime.Now;
            var endTime = now.Ticks;
            var startTime = now.AddSeconds(-1).Ticks;

            while (_fpsQueue.Any())
            {
                if (_fpsQueue.Peek() < startTime)
                {
                    _fpsQueue.Dequeue();

                    continue;
                }
                
                break;
            }

            _fpsQueue.Enqueue(endTime);
            return _fpsQueue.Count;
        }
    }
}