using System.Collections;
using System.Collections.Generic;
using Svelto.Tasks.Unity;
using UnityEngine.Networking;

namespace Svelto.Tasks.Enumerators
{
    public class UnityWebRequestEnumerator : IEnumerator<TaskContract?>
    {
        public UnityWebRequestEnumerator(UnityWebRequest www, int timeOut = -1)
        {
            _www         = www;
            _www.timeout = timeOut;
#if UNITY_2017_2_OR_NEWER
            _www.SendWebRequest();
#else
            _www.Send();
#endif
        }

        public bool MoveNext()
        {
            return _www.isDone == false;
        }

        public void Reset()
        { }

        object IEnumerator.Current
        {
            get { return _www; }
        }

        TaskContract? IEnumerator<TaskContract?>.Current { get; }

        public UnityWebRequest Current
        {
            get { return _www; }
        }

        public void Dispose()
        {
            _www.Dispose();
        }

        public UnityWebRequest www { get { return _www; } }

        readonly UnityWebRequest _www;
    }
}