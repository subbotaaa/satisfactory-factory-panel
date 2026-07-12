# Сервер панели фабрики: http://localhost:8086/dashboard.html
# Запускается без окна через pythonw.exe (см. задачу планировщика "FactorioMonitor-Panel").
import http.server
import io
import os
import socketserver
import sys

# pythonw не имеет консоли: stdout/stderr = None, и любой print уронил бы обработчик
if sys.stdout is None:
    sys.stdout = io.StringIO()
if sys.stderr is None:
    sys.stderr = io.StringIO()

os.chdir(os.path.dirname(os.path.abspath(__file__)))

class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass  # без логов — некуда писать и незачем

socketserver.ThreadingTCPServer.allow_reuse_address = True
# 0.0.0.0 — панель доступна и с других устройств локальной сети:
# http://<IP-этого-ПК>:8086/dashboard.html
with socketserver.ThreadingTCPServer(("0.0.0.0", 8086), Handler) as srv:
    srv.serve_forever()
