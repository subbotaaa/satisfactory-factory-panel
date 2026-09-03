using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using BigAmbitionsMonitor;

namespace Doorstop
{
    /// <summary>
    /// Точка входа для UnityDoorstop (winhttp.dll). Doorstop вызывает Start() внутри процесса
    /// игры при инициализации Mono. Мы поднимаем фоновый поток с HTTP-сервером и раз в секунду
    /// читаем живое состояние игры (SaveGameManager.Current) — данные в реальном времени.
    ///
    /// Namespace/класс/метод обязаны называться Doorstop.Entrypoint.Start (так требует Doorstop).
    /// Свой минимальный загрузчик вместо BepInEx: не тянет MonoMod/Harmony, которые давятся
    /// урезанным mscorlib игры.
    /// </summary>
    public static class Entrypoint
    {
        private static SocketHttpServer _server;
        private static Thread _worker;
        private static string _root;

        public static void Start()
        {
            try
            {
                _root = ResolveRoot();

                // Локализация названий товаров через движок игры (язык берётся из настроек игры).
                SnapshotCollector.LocalizeItem = key => Localizor.LocalizorManager.GetLocalization(key);
                SnapshotCollector.CanWrite = () => MainThreadDispatcher.Hooked;

                // Цены/рынок/закупка/рабочие места/ёмкость/требования/оптовики — ТОЛЬКО из кэша,
                // который пересчитывается в главном потоке (MainThreadCache.Refresh). Из фонового
                // потока эти игровые API вызывать нельзя — игра падает (проверено крашем).
                MainThreadCache.Localize = key => Localizor.LocalizorManager.GetLocalization(key);
                SnapshotCollector.PricesFor = k => MainThreadCache.Buildings.TryGetValue(k, out var d) ? d.prices : null;
                SnapshotCollector.WorkstationsFor = k => MainThreadCache.Buildings.TryGetValue(k, out var d) ? d.workstations : null;
                SnapshotCollector.CapacityFor = k => MainThreadCache.Buildings.TryGetValue(k, out var d) ? d.capacity : null;
                SnapshotCollector.FreeCapacityFor = k => MainThreadCache.Buildings.TryGetValue(k, out var d) ? d.freeCapacity : 0;
                SnapshotCollector.WholesalePrice = item => MainThreadCache.Wholesale.TryGetValue(item ?? "", out var w) ? w : 0f;
                SnapshotCollector.WeeklyMaxFor = item => MainThreadCache.WeeklyMax.TryGetValue(item ?? "", out var m) ? m : 0;
                SnapshotCollector.UnfulfilledFor = id => MainThreadCache.Unfulfilled.TryGetValue(id ?? "", out var l) ? l : null;
                SnapshotCollector.WholesalersProvider = () => MainThreadCache.Wholesalers;
                SnapshotCollector.DeliveryProvider = () => MainThreadCache.Delivery;

                // Мост в главный поток — подписываемся здесь, при инициализации Mono, до первого
                // кадра: в этот момент список колбэков ещё никто не перебирает (нет гонки).
                MainThreadDispatcher.Hook(Log);

                // Сервер поднимаем ПЕРВЫМ — до любого логирования, чтобы порт точно привязался.
                _server = new SocketHttpServer(_root, Log);
                _server.OnPost = HandlePost;
                _server.Start();

                _worker = new Thread(SnapshotLoop) { IsBackground = true, Name = "BAMonitor.Snapshot" };
                _worker.Start();

                Log("started; root=" + _root + " port=" + SocketHttpServer.Port);
            }
            catch (Exception e)
            {
                Log("start failed: " + e);
            }
        }

        private static string ResolveRoot()
        {
            try
            {
                var loc = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    var dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
            catch { }
            // Запасной вариант: <корень игры>/MonitorLoader
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "MonitorLoader");
        }

        // Читаем SaveGameManager.Current из фонового потока. Игра мутирует состояние в главном
        // потоке, поэтому оборачиваем каждый снимок в try/catch: при гонке просто пропускаем такт
        // и отдаём предыдущий снапшот. Мы только читаем — игре это не вредит.
        private static void SnapshotLoop()
        {
            int tick = 0;
            while (true)
            {
                try
                {
                    // Если hook не встал при старте — пробуем ещё (RegisterCallback под lock, безопасно).
                    if (!MainThreadDispatcher.Hooked) MainThreadDispatcher.Hook(null);
                    // Раз в 5 с — пересчёт «рискованного» кэша в главном потоке (fire-and-forget).
                    if (tick++ % 5 == 0) MainThreadDispatcher.Enqueue(MainThreadCache.Refresh);
                    var g = SaveGameManager.Current;
                    _server.SetSnapshot(SnapshotCollector.BuildSnapshotJson(g));
                }
                catch (Exception)
                {
                    // гонка чтения/мутации — пропускаем такт
                }
                Thread.Sleep(1000);
            }
        }

        // POST-маршруты. Запись в игру уходит в главный поток через MainThreadDispatcher.
        private static string HandlePost(string path, string body)
        {
            if (path == "/api/schedule/apply")
            {
                ScheduleWriter.ApplyRequest req;
                try { req = Newtonsoft.Json.JsonConvert.DeserializeObject<ScheduleWriter.ApplyRequest>(body); }
                catch (Exception e) { return "{\"ok\":false,\"error\":" + Newtonsoft.Json.JsonConvert.ToString("bad json: " + e.Message) + "}"; }

                ScheduleWriter.ApplyResult result = null;
                var err = MainThreadDispatcher.RunAndWait(() => result = ScheduleWriter.Apply(req), 6000);
                if (err != null) return "{\"ok\":false,\"error\":" + Newtonsoft.Json.JsonConvert.ToString(err) + "}";
                Log($"schedule apply: added={result?.added} removed={result?.removed} hours={result?.hoursChanged} ok={result?.ok} {result?.error}");
                return Newtonsoft.Json.JsonConvert.SerializeObject(result);
            }
            if (path == "/api/prices/set")
            {
                GameWriters.PricesRequest req;
                try { req = Newtonsoft.Json.JsonConvert.DeserializeObject<GameWriters.PricesRequest>(body); }
                catch (Exception e) { return "{\"ok\":false,\"error\":" + Newtonsoft.Json.JsonConvert.ToString("bad json: " + e.Message) + "}"; }
                GameWriters.SimpleResult result = null;
                var err = MainThreadDispatcher.RunAndWait(() => result = GameWriters.SetPrices(req), 6000);
                if (err != null) return "{\"ok\":false,\"error\":" + Newtonsoft.Json.JsonConvert.ToString(err) + "}";
                Log($"prices set: changed={result?.changed} ok={result?.ok} {result?.error}");
                return Newtonsoft.Json.JsonConvert.SerializeObject(result);
            }
            if (path == "/api/order/apply")
            {
                GameWriters.OrderRequest req;
                try { req = Newtonsoft.Json.JsonConvert.DeserializeObject<GameWriters.OrderRequest>(body); }
                catch (Exception e) { return "{\"ok\":false,\"error\":" + Newtonsoft.Json.JsonConvert.ToString("bad json: " + e.Message) + "}"; }
                GameWriters.SimpleResult result = null;
                var err = MainThreadDispatcher.RunAndWait(() => result = GameWriters.ApplyOrder(req), 6000);
                if (err != null) return "{\"ok\":false,\"error\":" + Newtonsoft.Json.JsonConvert.ToString(err) + "}";
                Log($"order apply: changed={result?.changed} day={result?.nextDeliveryDay} ok={result?.ok} {result?.error}");
                return Newtonsoft.Json.JsonConvert.SerializeObject(result);
            }
            return null;
        }

        private static readonly StringBuilder _logBuf = new StringBuilder();

        // File.AppendAllText вырезан из BCL игры, поэтому копим лог в памяти и перезаписываем
        // через File.WriteAllText (он есть). Логирование не должно влиять на работу монитора.
        private static void Log(string msg)
        {
            try
            {
                _logBuf.Append(DateTime.Now.ToString("HH:mm:ss")).Append("  ").Append(msg).Append('\n');
                var text = _logBuf.ToString();
                foreach (var dir in new[] { _root, AppDomain.CurrentDomain.BaseDirectory })
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(dir))
                            File.WriteAllText(Path.Combine(dir, "monitor.log"), text);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
