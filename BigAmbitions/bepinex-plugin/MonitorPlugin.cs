using System;
using System.IO;
using BepInEx;
using UnityEngine;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Плагин BepInEx: крутится внутри игры, раз в секунду в главном потоке Unity снимает
    /// снапшот живого состояния (SaveGameManager.Current) и отдаёт его по HTTP вместе с панелью.
    /// Данные обновляются в реальном времени, а не только при сохранении.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class MonitorPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.dccontrol.bigambitionsmonitor";
        public const string PluginName = "Big Ambitions Monitor";
        public const string PluginVersion = "1.0.0";

        private const float SnapshotIntervalSec = 1f;

        private MonitorHttpServer _server;
        private float _timer;

        private void Awake()
        {
            // dashboard.html лежит рядом с DLL плагина (BepInEx/plugins/...).
            var root = Path.GetDirectoryName(Info.Location);
            _server = new MonitorHttpServer(root, msg => Logger.LogWarning(msg));
            _server.Start();
            Logger.LogInfo($"{PluginName}: http://localhost:{MonitorHttpServer.Port}/");
        }

        private void Update()
        {
            _timer += Time.unscaledDeltaTime;
            if (_timer < SnapshotIntervalSec) return;
            _timer = 0f;

            try
            {
                _server.SetSnapshot(SnapshotCollector.BuildSnapshotJson(SaveGameManager.Current));
            }
            catch (Exception e)
            {
                Logger.LogError("snapshot failed: " + e.Message);
            }
        }

        private void OnDestroy()
        {
            _server?.Stop();
            _server = null;
        }
    }
}
