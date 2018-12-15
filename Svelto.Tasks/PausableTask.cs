#if ENABLE_PLATFORM_PROFILER || TASKS_PROFILER_ENABLED || (DEBUG && !PROFILER)
#define GENERATE_NAME
#endif

using Svelto.Utilities;
using System;
using System.Collections;

namespace Svelto.Tasks
{
    public class PausableTaskException : Exception
    {
        public PausableTaskException(Exception e)
            : base(e.ToString(), e)
        {
        }

        public PausableTaskException(string message, Exception e)
            : base(message.FastConcat(" -", e.ToString()), e)
        {
        }
    }

    public interface IPausableTask<T> where T:IEnumerator
    {
        TaskCollection<T>.CollectionTask Current { get; }
        
        bool MoveNext();

        void Stop();
    }

    //The Continuation Wrapper contains a valid value until the task is not stopped. After that it should be released.
    public class ContinuationWrapper : IEnumerator
    {
        public bool MoveNext()
        {
            ThreadUtility.MemoryBarrier();
            if (_completed == true)
            {
                _completed = false;
                ThreadUtility.MemoryBarrier();
                return false;
            }

            return true;
        }

        internal void Completed()
        {
            _completed = true;
            ThreadUtility.MemoryBarrier();
        }

        public bool completed
        {
            get { return _completed; }
        }

        public void Reset()
        {
            _completed = false;
            ThreadUtility.MemoryBarrier();
        }

        public object Current
        {
            get { return null; }
        }

        volatile bool _completed;
    }
}

namespace Svelto.Tasks.Internal
{
    sealed class PooledPausableTask: IPausableTask<IEnumerator>
    {
        public PooledPausableTask(PausableTaskPool pool)
        {
            _pool         = pool;
            _pausableTask = new SveltoTask<IEnumerator>();
        }

        public bool MoveNext()
        {
            if (_pausableTask.MoveNext() == false)
            {
                _pausableTask._continuationWrapper.Completed();
                
                CleanUpOnRecycle();
                _pool.PushTaskBack(this);

                return false;
            }

            return true;
        }

        public void Stop()
        {
            _pausableTaskImplementation.Stop();
        }

        /// <summary>
        /// Clean up task on complete. This function doesn't need to reset any state, is only to release resources
        /// before it goes back to the pool!!!
        /// It's called only for the pooled tasks and will be called again on reset
        /// </summary>
        internal void CleanUpOnRecycle()
        {
#if GENERATE_NAME
            _pausableTask._name = string.Empty;
#endif
            _pausableTask._threadSafeStates = new SveltoTask<IEnumerator>.State();
            _pausableTask._coroutineWrapper.Clear();
            _pausableTask.ClearInvokes();
        }

        public TaskCollection<IEnumerator>.CollectionTask Current
        {
            get { return _pausableTask.Current; }
        }

        public ContinuationWrapper Start(IRunner runner, IEnumerator task)
        {
            DBC.Tasks.Check.Require(task != null,
                                    "An enumerator or enumerator provider is required to enable this function, please use SetEnumeratorProvider/SetEnumerator before to call start");

            DBC.Tasks.Check.Require(runner != null, "SetScheduler function has never been called");
            
#if GENERATE_NAME
            _pausableTask._name = task.ToString();
#endif
            _pausableTask._threadSafeStates.paused = true;
            _pausableTask._continuationWrapper.Reset();
            _pausableTask._threadSafeStates.started = true;
            _pausableTask.SetTask(task);
            _pausableTask._threadSafeStates.paused = false;
            
            runner.StartCoroutine(this);

            return _pausableTask._continuationWrapper;
        }
        
        public override string ToString()
        {
#if !GENERATE_NAME            
             return "PooledTask";
#else
            return _pausableTask._name;
#endif    
        }

        readonly SveltoTask<IEnumerator> _pausableTask;
        readonly PausableTaskPool _pool;
        IPausableTask<IEnumerator> _pausableTaskImplementation;
    }

    sealed class TaskRoutine<T>: IPausableTask<T>, ITaskRoutine<T> where T:IEnumerator
    {
        public TaskRoutine(IRunner runner)
        {
            _pausableTask = new SveltoTask<T>();
            _runner = runner;
        }

        public bool MoveNext()
        {
            if (_pausableTask.MoveNext(_onFail, _onStop) == false)
            {
                if (_pausableTask._threadSafeStates.pendingTask == true)
                {
                    _previousContinuationWrapper.Completed();

                    //start new coroutine using this task this will put _started to true (it was already though)
                    //it uses the current runner to start the pending task
                    DBC.Tasks.Check.Require(_runner != null, "SetScheduler function has never been called");

                    _pausableTask.SetTask(_pendingTask);

                    _pausableTask._threadSafeStates = new SveltoTask<T>.State();
                    _pausableTask._continuationWrapper.Reset();

                    _pausableTask.ClearInvokes();
                }
                else
                    _pausableTask._continuationWrapper.Completed();
                
                _pendingTask = default(T);
                _previousContinuationWrapper = null;

                return false;
            }

            return true;
        }

        public TaskCollection<T>.CollectionTask Current
        {
            get { return _pausableTask.Current; }
        }
        
        /// <summary>
        /// Calling SetEnumeratorProvider, SetEnumerator
        /// on a running task won't stop the task until either 
        /// Stop() or Start() is called.
        /// </summary>
        /// <param name="runner"></param>
        /// <returns></returns>
        public void SetEnumeratorProvider(Func<T> taskGenerator)
        {
            _taskGenerator  = taskGenerator;
        }

        public void SetEnumerator(T taskEnumerator)
        {
            _taskGenerator = null;
            
            if (!(default(T) == null && (IEnumerator)_taskEnumerator == (IEnumerator)taskEnumerator))
                _pausableTask._threadSafeStates.taskEnumeratorJustSet = true;
            _taskEnumerator = taskEnumerator;
        }
        
        bool isTaskEnumeratorValid
        {
            get { if (default(T) == null)
                    return _taskEnumerator != null;
                return true;
            }
        }

        public ContinuationWrapper Start(Action<PausableTaskException> onFail = null, Action onStop = null)
        {
            _onStop = onStop;
            _onFail = onFail;

            _pausableTask._threadSafeStates.paused = true;
            _pausableTask.OnTaskInterrupted();
            
            var continuationWrapper = _pausableTask._continuationWrapper;
            
            DBC.Tasks.Check.Require(_taskGenerator != null || _taskEnumerator != null,
                                    "An enumerator or enumerator provider is required to enable this function, please use SetEnumeratorProvider/SetEnumerator before to call start");

            var newTask = _taskGenerator != null ? _taskGenerator() : _taskEnumerator;

            if (_pausableTask._threadSafeStates.isRunning         == true
             && _pausableTask._threadSafeStates.explicitlyStopped == true)
            {
                //Stop() Start() causes this (previous continuation wrapper will stop before to start the new one)
                //Start() Start() is perceived as a continuation of the previous task therefore it won't
                //cause the continuation wrapper to stop
                _pendingTask                     = newTask;
                _previousContinuationWrapper      = _pausableTask._continuationWrapper;

                continuationWrapper = _pausableTask._continuationWrapper = new ContinuationWrapper();
                
                Resume(); //if it's paused, must resume
            }
            else
            {
                DBC.Tasks.Check.Require(_runner != null, "SetScheduler function has never been called");

                if (isTaskEnumeratorValid && _pausableTask._threadSafeStates.taskEnumeratorJustSet == false)
                {
#if DEBUG && !PROFILER
                    DBC.Tasks.Check.Assert(newTask.GetType().IsCompilerGenerated() == false, "Cannot restart a compiler generated iterator block, use SetEnumeratorProvider instead ".FastConcat(_pausableTask._name));
#endif
                    newTask.Reset();
                }

                _pausableTask.SetTask(newTask);
                _pausableTask._continuationWrapper.Reset();
                _pausableTask.ClearInvokes();

                if (_pausableTask._threadSafeStates.isRunning == false)
                {
                    _pausableTask._threadSafeStates = new SveltoTask<T>.State();
                    _pausableTask._threadSafeStates.started = true;
                    _runner.StartCoroutine(this as IPausableTask<IEnumerator>);
                }
                else
                    _pausableTask._threadSafeStates.paused = false;
            }

            return continuationWrapper;
        }

        public void Pause()
        {
            _pausableTask._threadSafeStates.paused = true;
        }

        public void Resume()
        {
            _pausableTask._threadSafeStates.paused = false;
        }

        public void Stop()
        {
            _pausableTask.Stop();
        }

        public bool isRunning
        {
            get { return _pausableTask._threadSafeStates.isRunning; }
        }

        public bool isDone
        {
            get { return _pausableTask._threadSafeStates.isDone; }
        }
        
        public override string ToString()
        {
#if GENERATE_NAME
            if (_pausableTask._name == string.Empty)
            {
                if (_taskGenerator == null && _taskEnumerator == null)
                    _pausableTask._name = base.ToString();
                else
                if (_taskEnumerator != null)
                    _pausableTask._name = _taskEnumerator.ToString();
                else
                {
                    var methodInfo = _taskGenerator.GetMethodInfoEx();
                    
                    _pausableTask._name = methodInfo.GetDeclaringType().ToString().FastConcat(".", methodInfo.Name);
                }
            }
    
            return _pausableTask._name;
#endif
            return "TaskRoutine";
        }


        readonly SveltoTask<T>        _pausableTask;
        Action<PausableTaskException> _onFail;
        Action                        _onStop;
        ContinuationWrapper           _previousContinuationWrapper;
        T                             _pendingTask;
        Func<T>                       _taskGenerator;
        T                             _taskEnumerator;
        IRunner                       _runner;
    }

    sealed class SveltoTask<T> where T:IEnumerator
    {
        const string CALL_START_FIRST_ERROR = "Enumerating PausableTask without starting it, please call Start() first";

        internal event Action onTaskHasBeenInterrupted;

        /// <summary>
        /// a TaskRoutine can be stopped by the user, but a PooledTask is also stopped when the runner is stopped
        /// explicitly
        /// </summary>
        internal void Stop()
        {
            _threadSafeStates.explicitlyStopped = true;

            OnTaskInterrupted();
        }

        internal void OnTaskInterrupted()
        {
            if (onTaskHasBeenInterrupted != null)
            {
                onTaskHasBeenInterrupted.Invoke();
                ClearInvokes();
            }
        }

        internal void ClearInvokes()
        {
            if (onTaskHasBeenInterrupted == null) return;
            
            foreach (var d in onTaskHasBeenInterrupted.GetInvocationList())
            {
                onTaskHasBeenInterrupted -= (Action) d;
            }
        }

        internal TaskCollection<T>.CollectionTask Current
        {
            get
            {
                //this is currently the serial task
                if (_stackingTask != null)
                {
                    //this is the enumerator held by the serial task
                    return _stackingTask.Current;
                }

                return new TaskCollection<T>.CollectionTask(null);
            }
        }

        /// <summary>
        /// Move Next is called by the current runner, which could be on another thread! that means that the
        /// --->class states used in this function must be thread safe<-----
        /// </summary>
        /// <returns></returns>
        internal bool MoveNext(Action<PausableTaskException> onFail = null,
                               Action onStop = null)
        {
            /// Stop() can be called from whatever thread, but the runner won't know about it until the next MoveNext()
            /// is called. It's VERY important that a task is not reused until naturally stopped through this mechanism,
            /// otherwise there is the risk to add the same task twice in the runner queue. The new task must be added
            /// in the queue through the pending enumerator functionality
            try
            {
                if (_threadSafeStates.isNotCompletedAndNotPaused)
                {
                    bool completed;
                    if (_threadSafeStates.explicitlyStopped == true)
                    {
                        completed = true;

                        if (onStop != null)
                        {
                            try
                            {
                                onStop();
                            }
                            catch (Exception onStopException)
                            {
                                Utilities.Console.LogException("Svelto.Tasks task OnStop callback threw an exception: "
                                                                  .FastConcat(base.ToString()), onStopException);
                            }
                        }
                    }
                    else
                    {
                        try
                        {
#if DEBUG && !PROFILER
                            DBC.Tasks.Check.Assert(_threadSafeStates.started == true, _callStartFirstError);
#endif
                            
                            completed = !_stackingTask.MoveNext();

                            var current = _stackingTask.Current;
                            if (current.breakIt == Break.AndStop && onStop != null)
                            {
                                try
                                {
                                    completed = true;

                                    onStop();
                                }
                                catch (Exception onStopException)
                                {
                                    Utilities.Console
                                       .LogException("Svelto.Tasks task OnStop callback threw an exception: "
                                                        .FastConcat(base.ToString()), onStopException);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            completed = true;

                            if (onFail != null && (e is TaskYieldsIEnumerableException) == false)
                            {
                                try
                                {
                                    onFail(new PausableTaskException(e));
                                }
                                catch (Exception onFailException)
                                {
                                    Utilities.Console
                                       .LogException("Svelto.Tasks task OnFail callback threw an exception: "
                                                        .FastConcat(base.ToString()), onFailException);
                                }
                            }
                            else
                            {
                                Utilities.Console.LogException("a Svelto.Tasks task threw an exception:  "
                                                            .FastConcat(base.ToString()), e);
                            }
                        }
                    }
                    
                    if (completed == true)
                        _threadSafeStates.completed = true;
                }

                if (_threadSafeStates.isCompletedAndNotPaused == true)
                    return false;

                return true;
            }
            catch (Exception e)
            {
                Utilities.Console.LogException(
                        new PausableTaskException("Something went drastically wrong inside a PausableTask", e));

                throw;
            }
        }

        internal SveltoTask()
        {
            _coroutineWrapper    = new SerialTaskCollection<T>(1);
            _continuationWrapper = new ContinuationWrapper();
        }

        internal void SetTask(T task)
        {
            if (task is TaskCollection<T>)
            {
                _stackingTask = task as TaskCollection<T>;
            }
            else
            {
                _coroutineWrapper.Clear();
                _coroutineWrapper.Add(task);
                
                _stackingTask = _coroutineWrapper;
            }
                
#if DEBUG && !PROFILER
            _callStartFirstError = CALL_START_FIRST_ERROR.FastConcat(" task: ", ToString());
#endif
        }
        
        internal          ContinuationWrapper               _continuationWrapper;
        internal          State                             _threadSafeStates;
        internal readonly SerialTaskCollection<T> _coroutineWrapper;
        
        TaskCollection<T> _stackingTask;

#if GENERATE_NAME
        internal string                _name = String.Empty;
#endif
#if DEBUG && !PROFILER
        string                        _callStartFirstError;
#endif

        internal struct State
        {
            volatile byte _value;

            const byte COMPLETED_BIT            = 0x1;
            const byte STARTED_BIT              = 0x2;
            const byte EXPLICITLY_STOPPED       = 0x4;
            const byte TASK_ENUMERATOR_JUST_SET = 0x8;
            const byte PAUSED_BIT               = 0x10;
            const byte PENDING_BIT              = 0x20;

            public bool completed
            {
                get { return BIT(COMPLETED_BIT); }
                set
                {
                    if (value) 
                        SETBIT(COMPLETED_BIT);
                    else 
                        UNSETBIT(COMPLETED_BIT);
                }
            }
            
            public bool pendingTask
            {
                get { return BIT(PENDING_BIT); }
                set
                {
                    if (value) 
                        SETBIT(PENDING_BIT);
                    else 
                        UNSETBIT(PENDING_BIT);
                }
            }

            public bool explicitlyStopped
            {
                get { return BIT(EXPLICITLY_STOPPED); }
                set
                {
                    if (value) 
                        SETBIT(EXPLICITLY_STOPPED);
                    else 
                        UNSETBIT(EXPLICITLY_STOPPED);
                }
            }

            public bool paused
            {
                get { return BIT(PAUSED_BIT); }
                set
                {
                    if (value) 
                        SETBIT(PAUSED_BIT);
                    else 
                        UNSETBIT(PAUSED_BIT);
                }
            }

            public bool started
            {
                get { return BIT(STARTED_BIT); }
                set
                {
                    if (value) 
                        SETBIT(STARTED_BIT);
                    else 
                        UNSETBIT(STARTED_BIT);
                }
            }

            public bool taskEnumeratorJustSet
            {
                get { return BIT(TASK_ENUMERATOR_JUST_SET); }
                set
                {
                    if (value) 
                        SETBIT(TASK_ENUMERATOR_JUST_SET);
                    else 
                        UNSETBIT(TASK_ENUMERATOR_JUST_SET);
                }
            }

            void SETBIT(byte bitmask)
            {
                ThreadUtility.VolatileWrite(ref _value, (byte) (_value | bitmask));
            }

            void UNSETBIT(int bitmask)
            {
                ThreadUtility.VolatileWrite(ref _value, (byte) (_value & ~bitmask));
            }

            bool BIT(byte bitmask)
            {
                return (ThreadUtility.VolatileRead(ref _value) & bitmask) == bitmask;
            }

            public bool isRunning
            {
                get
                {
                    byte completedAndStarted = STARTED_BIT | COMPLETED_BIT;

                    //started but not completed
                    return (ThreadUtility.VolatileRead(ref _value) & completedAndStarted) == STARTED_BIT;
                }
            }

            public bool isDone
            {
                get
                {
                    byte completedAndStarted = COMPLETED_BIT | STARTED_BIT;

                    return (ThreadUtility.VolatileRead(ref _value) & completedAndStarted) == COMPLETED_BIT;
                }
            }

            public bool isNotCompletedAndNotPaused
            {
                get
                {
                    byte completedAndPaused = COMPLETED_BIT | PAUSED_BIT;

                    return (ThreadUtility.VolatileRead(ref _value) & completedAndPaused) == 0x0;
                }
            }
            
            public bool isCompletedAndNotPaused
            {
                get
                {
                    byte completedAndPaused = COMPLETED_BIT | PAUSED_BIT;

                    return (ThreadUtility.VolatileRead(ref _value) & completedAndPaused) == COMPLETED_BIT;
                }
            }
        }
    }
}