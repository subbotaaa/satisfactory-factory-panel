using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Минимальный HTTP-сервер на сырых сокетах (System.Net.Sockets.Socket).
    /// HttpListener/TcpListener в урезанном BCL игры вырезаны, поэтому используем Socket.
    /// Отдаёт /api/snapshot (JSON) и / (dashboard.html). Один запрос на соединение.
    /// </summary>
    public class SocketHttpServer
    {
        public const int Port = 8113;

        private readonly string _rootPath;
        private readonly Action<string> _log;
        private Socket _listener;
        private Thread _thread;
        private volatile string _snapshotJson = "{\"error\":\"not ready\"}";
        private volatile bool _running;

        public SocketHttpServer(string rootPath, Action<string> log = null)
        {
            _rootPath = rootPath;
            _log = log ?? (_ => { });
        }

        public void SetSnapshot(string json) => _snapshotJson = json;

        /// <summary>Обработчик POST: (путь, тело) → JSON-ответ. null = 404.</summary>
        public Func<string, string, string> OnPost;

        public void Start()
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, Port));
            _listener.Listen(16);
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "BAMonitor.Http" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Close(); } catch { }
            _listener = null;
        }

        private void Loop()
        {
            while (_running)
            {
                Socket client = null;
                try
                {
                    client = _listener.Accept();
                    Handle(client);
                }
                catch (Exception e)
                {
                    // Таймаут приёма на «пустом» соединении (keep-alive браузера) — штатно, не шумим.
                    bool timeout = e is SocketException se && (se.SocketErrorCode == SocketError.TimedOut || se.SocketErrorCode == SocketError.ConnectionReset);
                    if (_running && !timeout) _log("http: " + e.Message);
                }
                finally
                {
                    try { client?.Close(); } catch { }
                }
            }
        }

        private void Handle(Socket client)
        {
            // Таймауты, чтобы «битый»/медленный клиент не заблокировал единственный цикл приёма.
            try { client.ReceiveTimeout = 3000; client.SendTimeout = 3000; } catch { }

            // Читаем запрос (для GET хватает одного-двух чтений до конца заголовков).
            // Используем 4-аргументные Receive/Send — одноаргументные вырезаны из BCL игры.
            var sb = new StringBuilder();
            var buf = new byte[2048];
            for (int i = 0; i < 8; i++)
            {
                int n = client.Receive(buf, 0, buf.Length, SocketFlags.None);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.ToString().Contains("\r\n\r\n")) break;
            }

            var req = sb.ToString();
            string method = "GET", path = "/";
            int sp1 = req.IndexOf(' ');
            if (sp1 >= 0)
            {
                method = req.Substring(0, sp1).ToUpperInvariant();
                int sp2 = req.IndexOf(' ', sp1 + 1);
                if (sp2 > sp1) path = req.Substring(sp1 + 1, sp2 - sp1 - 1);
            }
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);

            // Preflight CORS (панель может открываться и не с этого origin)
            if (method == "OPTIONS")
            {
                Respond(client, 204, "text/plain", new byte[0]);
                return;
            }

            if (method == "POST")
            {
                // Дочитываем тело по Content-Length (заголовки уже в sb).
                // Без Regex и перегрузок со StringComparison — они вырезаны из BCL игры.
                int hdrEnd = req.IndexOf("\r\n\r\n");
                int contentLength = 0;
                var lower = req.ToLowerInvariant();
                int clIdx = lower.IndexOf("content-length:");
                if (clIdx >= 0)
                {
                    int vStart = clIdx + "content-length:".Length;
                    int lineEnd = req.IndexOf("\r\n", vStart);
                    if (lineEnd < 0) lineEnd = req.Length;
                    int.TryParse(req.Substring(vStart, lineEnd - vStart).Trim(), out contentLength);
                }
                var bodySb = new StringBuilder();
                if (hdrEnd >= 0) bodySb.Append(req.Substring(hdrEnd + 4));
                int got = Encoding.UTF8.GetByteCount(bodySb.ToString());
                while (got < contentLength)
                {
                    int n = client.Receive(buf, 0, buf.Length, SocketFlags.None);
                    if (n <= 0) break;
                    bodySb.Append(Encoding.UTF8.GetString(buf, 0, n));
                    got += n;
                }
                var handler = OnPost;
                string json = null;
                try { json = handler?.Invoke(path, bodySb.ToString()); }
                catch (Exception e) { json = "{\"ok\":false,\"error\":" + Newtonsoft.Json.JsonConvert.ToString(e.Message) + "}"; }
                if (json == null) { Respond(client, 404, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"not found\"}")); return; }
                Respond(client, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                return;
            }

            if (path == "/api/snapshot")
            {
                Respond(client, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_snapshotJson));
                return;
            }
            if (path == "/" || path == "/index.html" || path == "/dashboard.html")
            {
                var file = Path.Combine(_rootPath, "dashboard.html");
                if (File.Exists(file))
                {
                    Respond(client, 200, "text/html; charset=utf-8", File.ReadAllBytes(file));
                    return;
                }
                Respond(client, 200, "text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes("<h1>BigAmbitionsMonitor</h1><p>dashboard.html не найден. <a href=\"/api/snapshot\">/api/snapshot</a></p>"));
                return;
            }
            Respond(client, 404, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"not found\"}"));
        }

        private static void Respond(Socket client, int code, string contentType, byte[] body)
        {
            var status = code == 200 ? "200 OK" : code == 204 ? "204 No Content" : code == 404 ? "404 Not Found" : code.ToString();
            var head = "HTTP/1.1 " + status + "\r\n"
                     + "Content-Type: " + contentType + "\r\n"
                     + "Content-Length: " + body.Length + "\r\n"
                     + "Access-Control-Allow-Origin: *\r\n"
                     + "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n"
                     + "Access-Control-Allow-Headers: Content-Type\r\n"
                     + "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            client.Send(headBytes, 0, headBytes.Length, SocketFlags.None);
            if (body.Length > 0) client.Send(body, 0, body.Length, SocketFlags.None);
        }
    }
}
