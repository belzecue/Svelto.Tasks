#if UNITY_5 || UNITY_5_3_OR_NEWER
using System.Collections;
using System.Collections.Generic;
using Svelto.Tasks.Unity;
#endif

namespace Svelto.Tasks
{
    public static class StandardSchedulers
    {
        static MultiThreadRunner<IEnumerator<TaskContract?>> _multiThreadScheduler;
#if UNITY_5 || UNITY_5_3_OR_NEWER
        static CoroutineMonoRunner<IEnumerator<TaskContract?>> _coroutineScheduler;
        static PhysicMonoRunner<IEnumerator<TaskContract?>> _physicScheduler;
        static LateMonoRunner<IEnumerator<TaskContract?>> _lateScheduler;
        static UpdateMonoRunner<IEnumerator<TaskContract?>> _updateScheduler;
        static EarlyUpdateMonoRunner<IEnumerator<TaskContract?>> _earlyScheduler;
#endif
        

        public static IRunner<IEnumerator<TaskContract?>> multiThreadScheduler { get { if (_multiThreadScheduler == null) _multiThreadScheduler = new MultiThreadRunner("MultiThreadRunner", false);
            return _multiThreadScheduler;
        } }
        
#if UNITY_5 || UNITY_5_3_OR_NEWER
        public static IRunner<IEnumerator<TaskContract?>> standardScheduler 
        { 
            get 
            { 
                return coroutineScheduler;
            } 
        }
        public static IRunner<IEnumerator<TaskContract?>> coroutineScheduler { get { if (_coroutineScheduler == null) _coroutineScheduler = new CoroutineMonoRunner("StandardCoroutineRunner");
            return _coroutineScheduler;
        } }
        public static IRunner<IEnumerator<TaskContract?>> physicScheduler { get { if (_physicScheduler == null) _physicScheduler = new PhysicMonoRunner("StandardPhysicRunner");
            return _physicScheduler;
        } }
        public static IRunner<IEnumerator<TaskContract?>> lateScheduler { get { if (_lateScheduler == null) _lateScheduler = new LateMonoRunner("StandardLateRunner");
            return _lateScheduler;
        } }
        public static IRunner<IEnumerator<TaskContract?>> earlyScheduler { get { if (_earlyScheduler == null) _earlyScheduler = new EarlyUpdateMonoRunner("EarlyUpdateMonoRunner");
            return _earlyScheduler;
        } }
        public static IRunner<IEnumerator<TaskContract?>> updateScheduler { get { if (_updateScheduler == null) _updateScheduler = new UpdateMonoRunner("StandardMonoRunner");
            return _updateScheduler;
        } }

        internal static void StartYieldInstruction(this IEnumerator instruction)
        {
            _coroutineScheduler.StartYieldInstruction(instruction);
        }
#else
        public static IRunner<IEnumerator<TaskContract?>> standardScheduler 
        { 
            get 
            { 
                return _multiThreadScheduler;
            } 
        }
#endif

        //physicScheduler -> earlyScheduler -> updateScheduler -> coroutineScheduler -> lateScheduler

        internal static void KillSchedulers()
        {
            if (_multiThreadScheduler != null && multiThreadScheduler.isKilled == false)
                _multiThreadScheduler.Dispose();
            _multiThreadScheduler = null;
            
#if UNITY_5 || UNITY_5_3_OR_NEWER
            if (_coroutineScheduler != null)
                 _coroutineScheduler.Dispose();
            if (_physicScheduler != null)
                _physicScheduler.Dispose();
            if (_lateScheduler != null)
                _lateScheduler.Dispose();
            if (_updateScheduler != null)
                _updateScheduler.Dispose();
            
            _coroutineScheduler = null;
            _physicScheduler = null;
            _lateScheduler = null;
            _updateScheduler = null;
            _earlyScheduler = null;
#endif
        }
    }
}
