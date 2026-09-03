using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Внешний ридер сейвов Big Ambitions. Следит за папкой сохранений, при каждом
    /// сохранении/автосейве десериализует самый свежий .hsg, собирает снапшот и отдаёт
    /// его по HTTP вместе с панелью (http://localhost:8113/). Steam и моды не нужны.
    /// </summary>
    internal static class Program
    {
        private static string ManagedDir =
            @"D:\Big.Ambitions.v1.0\Big Ambitions\Big Ambitions_Data\Managed";

        private static MonitorHttpServer _server;
        private static string _saveDir;
        private static string _lastPath;
        private static DateTime _lastWrite;

        private static int Main(string[] args)
        {
            // Резолвим любые игровые DLL из папки Managed
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                var path = Path.Combine(ManagedDir, name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            return Run(args);
        }

        // В отдельном методе, чтобы AssemblyResolve уже стоял до JIT игровых типов.
        private static int Run(string[] args)
        {
            var cfg = ParseArgs(args);
            if (cfg.ManagedDir != null) ManagedDir = cfg.ManagedDir;
            if (!Directory.Exists(ManagedDir))
            {
                Console.Error.WriteLine($"Не найдена папка игры Managed: {ManagedDir}");
                Console.Error.WriteLine("Укажите её: --game \"X:\\...\\Big Ambitions_Data\\Managed\"");
                return 1;
            }

            RuntimeShims.Apply();

            _saveDir = cfg.SaveDir ?? DefaultSaveDir();
            var rootPath = cfg.WebRoot ?? AppContext.BaseDirectory;

            _server = new MonitorHttpServer(rootPath, msg => Console.Error.WriteLine("[http] " + msg));
            _server.Start();
            Console.WriteLine($"Панель:   http://localhost:{MonitorHttpServer.Port}/");
            Console.WriteLine($"Сейвы:    {_saveDir}");
            Console.WriteLine($"dashboard: {Path.Combine(rootPath, "dashboard.html")}");
            Console.WriteLine("Слежу за сохранениями. Ctrl+C для выхода.\n");

            // Первый снимок + периодический опрос (FileSystemWatcher ненадёжен на .hsg из-за
            // временного файла и переименования, поэтому просто опрашиваем mtime раз в 2 сек).
            while (true)
            {
                try { Poll(); }
                catch (Exception e) { Console.Error.WriteLine("[poll] " + e.Message); }
                Thread.Sleep(2000);
            }
        }

        private static void Poll()
        {
            if (!Directory.Exists(_saveDir)) return;

            var newest = new DirectoryInfo(_saveDir)
                .EnumerateFiles("*.hsg", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null) return;

            if (newest.FullName == _lastPath && newest.LastWriteTimeUtc == _lastWrite)
                return; // без изменений

            // Файл мог ещё дописываться — небольшая пауза и защита от частичного чтения.
            Thread.Sleep(300);
            var g = SaveDeserializer.Load(newest.FullName);
            if (g == null)
            {
                Console.Error.WriteLine("[poll] не удалось прочитать " + newest.Name);
                return;
            }

            _server.SetSnapshot(SnapshotCollector.BuildSnapshotJson(g));
            _lastPath = newest.FullName;
            _lastWrite = newest.LastWriteTimeUtc;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {newest.Name}: день {g.Day}, ${g.Money:0}, обновлено");
        }

        private static string DefaultSaveDir()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var localLow = local.Replace(@"\Local", @"\LocalLow");
            return Path.Combine(localLow, "Hovgaard Games", "Big Ambitions", "SaveGames");
        }

        private static (string ManagedDir, string SaveDir, string WebRoot) ParseArgs(string[] args)
        {
            string game = null, saves = null, web = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--game") game = args[i + 1];
                else if (args[i] == "--saves") saves = args[i + 1];
                else if (args[i] == "--web") web = args[i + 1];
            }
            return (game, saves, web);
        }
    }
}
