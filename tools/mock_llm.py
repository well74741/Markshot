import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length)
        try:
            payload = json.loads(raw)
        except Exception:
            payload = {}

        text = ""
        for message in payload.get("messages", []):
            if message.get("role") == "user":
                text = message.get("content", "")

        body = json.dumps(
            {"choices": [{"message": {"role": "assistant", "content": "[MOCK EN] " + text[:300]}}]},
            ensure_ascii=False,
        ).encode("utf-8")

        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        pass


if __name__ == "__main__":
    print("mock llm listening on http://127.0.0.1:8123/v1")
    ThreadingHTTPServer(("127.0.0.1", 8123), Handler).serve_forever()
