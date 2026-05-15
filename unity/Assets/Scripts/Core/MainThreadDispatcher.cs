using System;
using System.Collections.Generic;
using UnityEngine;

namespace SLQuest.Core
{
    /// <summary>
    /// Marshals callbacks from libopenmetaverse's worker threads onto Unity's main thread.
    /// libopenmetaverse fires all network events on background threads; Unity API calls
    /// are only legal on the main thread, so every event handler must enqueue here.
    /// </summary>
    public sealed class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private readonly Queue<Action> _queue = new(64);
        private readonly object _lock = new();

        public static MainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                    CreateInstance();
                return _instance;
            }
        }

        private static void CreateInstance()
        {
            var go = new GameObject("[MainThreadDispatcher]");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MainThreadDispatcher>();
        }

        /// <summary>
        /// Thread-safe. Call from any thread to run <paramref name="action"/> on the next Unity frame.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null) return;
            lock (Instance._lock)
                Instance._queue.Enqueue(action);
        }

        private void Update()
        {
            lock (_lock)
            {
                while (_queue.Count > 0)
                {
                    try { _queue.Dequeue()(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }
        }
    }
}
