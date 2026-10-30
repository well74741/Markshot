# Markshot

Windows 截图 / 贴图 / 批注 / 截图翻译工具，交互对标 PixPin 的基础工作流。

> 当前版本 **0.1.0（MVP）**。长截图、录屏不在本阶段范围。

---

## 已实现

### 截图
- 全局热键唤起，冻结当前屏幕并压暗，框选后保持原画面
- 区域选择、8 向手柄缩放、拖动移动、尺寸标签
- 方向键 1px 微调，`Shift + 方向键` 10px
- 多显示器虚拟桌面抓取（同一缩放比例下）
- 框选后可复制、另存为、快速保存、贴图、翻译

### 批注
- 矩形、椭圆、箭头、画笔、荧光笔、马赛克、文字、序号
- 撤销 / 重做，颜色盘，细 / 中 / 粗三档线宽
- 文字批注：`Ctrl + Enter` 确认，`Esc` 取消
- 批注在复制、保存、贴图时合成进图片；翻译使用未批注的原图

### 贴图
- 无边框置顶窗口，按截图位置钉在屏幕上
- 拖动移动、滚轮缩放、`Ctrl + 滚轮` 透明度、中键重置
- 双击 / `Esc` 关闭，`T` 置顶切换，`L` 锁定，边框颜色随状态变化
- 右键菜单：复制、另存为、翻译、置顶、锁定、重置、关闭
- 关闭的贴图进入历史，可从托盘“恢复最近贴图”
- 剪贴板没有图片时，可把纯文本渲染成卡片贴出

### OCR 与翻译
- 使用 Windows 内置 OCR，本地识别，图片不出本机
- 翻译服务为任意 OpenAI 兼容 `/chat/completions` 接口（DeepSeek / OpenAI / Moonshot / Ollama / LM Studio / one-api 等）
- 原文可编辑，译文对照显示；支持复制译文、把译文贴到屏幕
- 目标语言可临时切换，默认语言可在设置中修改

### 其他
- 托盘常驻、单实例、全局热键可配置、开机不自动启动
- 日志写入 exe 同目录 `logs/app.log`
- 配置支持漫游目录与便携模式（exe 同目录 `config.json` 优先）

---

## 快捷键

### 截图态

| 操作 | 快捷键 |
| --- | --- |
| 唤起截图 | `F1`（可在设置中修改） |
| 复制并退出 | `Enter` / `Ctrl + C` / 双击选区 |
| 另存为 | `Ctrl + S` |
| 快速保存 | `Ctrl + Shift + S` |
| 贴图 | `F3` |
| 翻译 | `Ctrl + Q` |
| 全选当前屏幕 | `Ctrl + A` |
| 撤销 / 重做 | `Ctrl + Z` / `Ctrl + Y` |
| 切换工具 | `R` 矩形、`O` 椭圆、`A` 箭头、`P` 画笔、`H` 荧光、`M` 马赛克、`T` 文字、`N` 序号 |
| 逐层退出 | `Esc`（先退工具，再退选区，最后关窗） |
| 移动选区 | 方向键，`Shift` 加速 |

### 贴图窗口

| 操作 | 快捷键 |
| --- | --- |
| 移动 | 鼠标拖动 |
| 缩放 / 透明度 | 滚轮 / `Ctrl + 滚轮` |
| 重置缩放与透明度 | 鼠标中键 |
| 关闭 | 双击 / `Esc` / `Ctrl + W` |
| 复制 | `Ctrl + C` |
| 另存为 | `Ctrl + S` |
| 置顶切换 / 锁定 | `T` / `L` |

### 全局

| 操作 | 快捷键 |
| --- | --- |
| 截图 | `F1` |
| 贴图剪贴板 | `F3` |

---

## 环境要求

- Windows 10 2004+ 或 Windows 11，x64
- 构建：.NET 8 SDK
- 运行：.NET 8 Desktop Runtime（自包含发布则不需要）

OCR 使用系统语言包。若提示不可用，请在“设置 → 时间和语言 → 语言”中为目标语言安装“光学字符识别”可选功能。

---

## 构建与运行

```powershell
dotnet build -c Release
dotnet run -c Release
```

发布为单文件（目标机需安装 .NET 8 Desktop Runtime）：

```powershell
dotnet publish Markshot.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

或发布为自包含单文件：

```powershell
dotnet publish Markshot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

产物在 `publish\Markshot.exe`。

---

## 配置

配置读取优先级：

1. exe 同目录 `config.json`（便携模式）
2. `%APPDATA%\Markshot\config.json`

保存时优先写当前生效的配置；失败则尝试另一个位置。日志固定写入 exe 同目录 `logs\app.log`。

示例（DeepSeek）：

```json
{
  "CaptureHotkey": "F1",
  "PinHotkey": "F3",
  "SaveDirectory": "",
  "OcrLanguage": "zh-Hans-CN",
  "Translation": {
    "BaseUrl": "https://api.deepseek.com/v1",
    "ApiKey": "sk-...",
    "Model": "deepseek-chat",
    "TargetLanguage": "简体中文",
    "SystemPrompt": "",
    "TimeoutSeconds": 60
  }
}
```

本地 Ollama 示例：

```json
"Translation": {
  "BaseUrl": "http://127.0.0.1:11434/v1",
  "ApiKey": "",
  "Model": "qwen2.5:7b",
  "TargetLanguage": "简体中文"
}
```

字段说明：

| 字段 | 说明 |
| --- | --- |
| `CaptureHotkey` | 截图全局热键，如 `F1`、`Ctrl+1`、`Ctrl+Shift+S` |
| `PinHotkey` | 贴图剪贴板全局热键 |
| `SaveDirectory` | 保存目录，留空则为 `图片\Markshot` |
| `OcrLanguage` | OCR 语言标签，如 `zh-Hans-CN`、`en-US`；留空使用系统默认 |
| `Translation.BaseUrl` | OpenAI 兼容接口根地址，需包含 `/v1`（如果服务如此） |
| `Translation.ApiKey` | 本地服务可留空 |
| `Translation.Model` | 模型名 |
| `Translation.TargetLanguage` | 默认目标语言 |
| `Translation.TimeoutSeconds` | 请求超时，10–300 秒 |

模板见 `config.sample.json`。

---

## 命令行

| 参数 | 作用 |
| --- | --- |
| `--capture` | 启动后立即进入截图 |
| `--demo` | 启动后进入截图并预置演示选区与批注（开发验证用） |
| `--settings` | 启动后打开设置 |
| `--ocr <图片>` | 对图片执行 OCR，结果写到同目录 `.ocr.txt` |
| `--translate <图片>` | 对图片执行 OCR + 翻译，结果写到同目录 `.translate.txt` |
| `--logtest` | 写一条日志并输出 `logtest.txt`，用于排查日志路径 |

---

## 开发自检

仓库自带一个本地 mock 翻译服务：

```powershell
python tools\mock_llm.py
```

然后把 `BaseUrl` 指向 `http://127.0.0.1:8123/v1`，`ApiKey` 随便填，`Model` 填 `mock-model`，即可在没有真实 API Key 的情况下验证 OCR + 翻译全链路。

```powershell
Markshot.exe --translate .\docs\sample.png
```

---

## 目录结构

```
Markshot/
├─ Annotations/        批注模型、马赛克、画布渲染
├─ Core/               配置、抓屏、剪贴板、热键、托盘、日志
├─ Services/           OCR、翻译、文字卡片渲染
├─ UI/                 覆盖层、贴图窗口、翻译窗口、设置窗口
├─ tools/              本地 mock 翻译服务
├─ App.xaml[.cs]       应用入口、托盘与窗口编排
└─ Markshot.csproj
```

---

## 已知限制

- 主要针对单屏 / 统一缩放比例验证；混合 DPI 的多屏场景仍需联调
- 长截图、录屏、贴图分组、批注二次编辑、鼠标穿透、贴图历史持久化、开机启动尚未实现
- OCR 依赖 Windows 引擎，对界面截图的中文识别准确率一般；后续可接 PaddleOCR / OneOCR
- 翻译按整段文本处理，暂不支持把译文按原版式回填图片
- API Key 以明文保存在本地配置文件中，后续可接 DPAPI
- 保存失败、剪贴板被占用等异常只写日志或弹窗，没有统一的重试中心

---

## Roadmap

1. **P1**：长截图、贴图分组、截图/贴图历史持久化、鼠标穿透、批注二次编辑
2. **P2**：更好的 OCR（PaddleOCR / OneOCR）、译文原位覆盖、术语表、增量翻译
3. **P3**：录屏、表格/公式识别、开机启动、多语言 UI

---

个人项目，暂不考虑商业化。
