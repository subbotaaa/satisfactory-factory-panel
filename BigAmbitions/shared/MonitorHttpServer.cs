using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Лёгкий HTTP-сервер на HttpListener: отдаёт кэшированный JSON-снапшот
    /// (/api/snapshot) и dashboard.html (/). Снапшот пишется одним потоком, читается другими.
    /// Общий для мода и внешнего ридера сейвов.
    /// </summary>
    public class MonitorHttpServer
    {
        public const int Port = 8113;

        private readonly string _rootPath;   // папка с dashboard.html
        private readonly Action<string> _log;
        private HttpListener _listener;
        private Thread _thread;
        private volatile string _snapshotJson = "{\"error\":\"not ready\"}";
        private volatile bool _running;

        public MonitorHttpServer(string rootPath, Action<string> log = null)
        {
            _rootPath = rootPath;
            _log = log ?? (_ => { });
        }

        public void SetSnapshot(string json) => _snapshotJson = json;

        public void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "BigAmbitionsMonitor.Http" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch (Exception) { }
            try { _listener?.Close(); } catch (Exception) { }
            _listener = null;
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { if (_running) continue; return; }

                try { Handle(ctx); }
                catch (Exception e)
                {
                    _log("HTTP handler error: " + e.Message);
                    try { ctx.Response.Abort(); } catch (Exception) { }
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath;
            var resp = ctx.Response;
            resp.Headers["Access-Control-Allow-Origin"] = "*";

            if (path == "/api/snapshot")
            {
                WriteText(resp, _snapshotJson, "application/json; charset=utf-8");
                return;
            }

            if (path == "/" || path == "/index.html" || path == "/dashboard.html")
            {
                var file = Path.Combine(_rootPath, "dashboard.html");
                if (File.Exists(file))
                {
                    WriteText(resp, File.ReadAllText(file, Encoding.UTF8), "text/html; charset=utf-8");
                    return;
                }
                WriteText(resp,
                    "<h1>BigAmbitionsMonitor</h1><p>dashboard.html не найден. API: <a href=\"/api/snapshot\">/api/snapshot</a></p>",
                    "text/html; charset=utf-8");
                return;
            }

            resp.StatusCode = 404;
            WriteText(resp, "{\"error\":\"not found\"}", "application/json; charset=utf-8");
        }

        private static void WriteText(HttpListenerResponse resp, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            resp.ContentType = contentType;
            resp.ContentLength64 = bytes.Length;
            resp.OutputStream.Write(bytes, 0, bytes.Length);
            resp.OutputStream.Close();
        }
    }
}
