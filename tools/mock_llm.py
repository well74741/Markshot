"""Local stand-in for an OpenAI-compatible /chat/completions endpoint.

Run `python tools/mock_llm.py`, then set Base URL to http://127.0.0.1:8123/v1
(no API key needed for localhost). Batch requests (a JSON array of strings)
get a JSON array back with the same length, like a real model; plain text gets
a prefixed echo. The tiny dictionary makes in-place translation visibly change.
"""
import json
import re
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

WORDS = {
    "settings": "设置", "file": "文件", "edit": "编辑", "view": "视图", "help": "帮助",
    "open": "打开", "save": "保存", "cancel": "取消", "download": "下载", "the": "",
    "quick": "快速的", "brown": "棕色", "fox": "狐狸", "jumps": "跳过", "over": "越过",
    "lazy": "懒惰的", "dog": "狗", "screenshot": "截图", "translation": "翻译",
    "release": "发布", "notes": "说明", "version": "版本", "new": "新", "features": "功能",
    "and": "和", "improvements": "改进", "welcome": "欢迎", "to": "到", "markshot": "Markshot",
}


def fake_translate(text):
    def word(match):
        w = match.group(0)
        return WORDS.get(w.lower(), w)
    out = re.sub(r"[A-Za-z]+", word, text)
    return re.sub(r"\s+", " ", out).strip() or text


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        try:
            payload = json.loads(self.rfile.read(length))
        except Exception:
            payload = {}

        text = ""
        for message in payload.get("messages", []):
            if message.get("role") == "user":
                text = message.get("content", "")

        try:
            items = json.loads(text)
            is_batch = isinstance(items, list)
        except Exception:
            is_batch = False

        if is_batch:
            content = json.dumps([fake_translate(str(i)) for i in items], ensure_ascii=False)
        else:
            content = "[MOCK] " + fake_translate(text[:300])

        body = json.dumps(
            {"choices": [{"message": {"role": "assistant", "content": content}}]},
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
