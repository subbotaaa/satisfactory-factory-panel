using System;
using System.Collections.Generic;
using System.Threading;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Мост в главный поток Unity. Подписка на Application.onBeforeRender — управляемая
    /// (BeforeRenderHelper.RegisterCallback под lock), поэтому её можно сделать из фонового
    /// потока, а сам колбэк Unity вызывает в главном потоке каждый кадр. В нём исполняем
    /// очередь команд записи (изменение расписания) — единственно безопасный способ менять
    /// состояние игры, т.к. игра мутирует его только из главного потока.
    /// </summary>
    public static class MainThreadDispatcher
    {
        private sealed class Job
        {
            public Action Run;
            public volatile bool Done;
            public string Error;
        }

        private static readonly List<Job> Queue = new List<Job>();
        private static readonly object Lock = new object();
        private static volatile bool _hooked;
        private static Action<string> _log = _ => { };

        public static bool Hooked => _hooked;

        /// <summary>Подписаться на кадровый колбэк. Повторные вызовы безопасны.</summary>
        public static void Hook(Action<string> log)
        {
            if (log != null) _log = log;
            if (_hooked) return;
            try
            {
                UnityEngine.Application.onBeforeRender += Tick;
                _hooked = true;
                _log("main-thread hook: ok (Application.onBeforeRender)");
            }
            catch (Exception e)
            {
                _log("main-thread hook failed: " + e.Message);
            }
        }

        // Главный поток Unity, каждый кадр. Дёшево, когда очередь пуста.
        private static void Tick()
        {
            Job[] jobs;
            lock (Lock)
            {
                if (Queue.Count == 0) return;
                jobs = Queue.ToArray();
                Queue.Clear();
            }
            foreach (var j in jobs)
            {
                try { j.Run(); }
                catch (Exception e) { j.Error = e.ToString(); }
                finally { j.Done = true; }
            }
        }

        /// <summary>Поставить команду в очередь без ожидания (fire-and-forget), если hook активен.</summary>
        public static bool Enqueue(Action action)
        {
            if (!_hooked || action == null) return false;
            lock (Lock) Queue.Add(new Job { Run = action });
            return true;
        }

        /// <summary>
        /// Поставить команду в очередь и дождаться выполнения (вызывать из HTTP-потока).
        /// Возвращает текст ошибки или null при успехе.
        /// </summary>
        public static string RunAndWait(Action action, int timeoutMs)
        {
            if (!_hooked) return "главный поток недоступен (hook не установлен)";
            var job = new Job { Run = action };
            lock (Lock) Queue.Add(job);
            int waited = 0;
            while (!job.Done && waited < timeoutMs)
            {
                Thread.Sleep(25);
                waited += 25;
            }
            if (!job.Done) return "таймаут: игра не рендерит кадры (меню/пауза/свернута?)";
            return job.Error;
        }
    }
}
