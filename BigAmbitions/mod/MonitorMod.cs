using System;
using System.Threading.Tasks;
using BAModAPI;
using BAModAPI.Services;

[assembly: RegisterModClass(typeof(BigAmbitionsMonitor.MonitorMod))]

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Точка входа мода. Загружается вместе с городом (когда сейв уже в памяти),
    /// раз в несколько секунд снимает снапшот состояния и отдаёт его по HTTP.
    /// </summary>
    [ModEntryOnCityLoad]
    public class MonitorMod : IModBigAmbitions
    {
        private const int SnapshotIntervalMs = 3000;

        private ModContext _context;
        private MonitorHttpServer _server;
        private int _lastSnapshotTick;

        public string[] RelativeAssetBundlePaths => Array.Empty<string>();

        public Task OnLoadAsync(ModContext context)
        {
            _context = context;
            _server = new MonitorHttpServer(context.ModRootPath, msg => context.Logger.Warn(msg));
            _server.Start();

            _lastSnapshotTick = Environment.TickCount - SnapshotIntervalMs;
            UnityLifecycleProvider.OnUpdate += OnUpdate;

            context.Logger.Info($"BigAmbitionsMonitor started: http://localhost:{MonitorHttpServer.Port}/");
            return Task.CompletedTask;
        }

        public Task OnUnloadAsync()
        {
            UnityLifecycleProvider.OnUpdate -= OnUpdate;
            _server?.Stop();
            _server = null;
            _context?.Logger.Info("BigAmbitionsMonitor stopped.");
            return Task.CompletedTask;
        }

        // Снапшот собирается в главном потоке Unity — игровые структуры не потокобезопасны.
        private void OnUpdate()
        {
            if (unchecked(Environment.TickCount - _lastSnapshotTick) < SnapshotIntervalMs)
                return;
            _lastSnapshotTick = Environment.TickCount;

            try
            {
                _server.SetSnapshot(SnapshotCollector.BuildSnapshotJson(SaveGameManager.Current));
            }
            catch (Exception e)
            {
                _context?.Logger.Error("Snapshot failed: " + e);
            }
        }
    }
}
