# Markshot 开发交接文档

> 面向接替开发的智能体/开发者。项目当前是 `0.7.0`，WPF + .NET 8，Windows 专用。
> 本文件不包含 API Key；密钥只放在运行目录的 `config.json`，该文件被 `.gitignore` 忽略。

## 1. 项目目标

Markshot 是 PixPin 风格的截图 / 贴图 / 批注 / OCR / 翻译工具：

- 截图时框选、放大镜、取色；
- 批注栏：图形、画笔/荧光笔、箭头/直线、文字、序号、马赛克/水印、橡皮；
- 批注对象可再次选中、移动、缩放、旋转；
- 贴图可缩放、拖动、锁定、穿透、再次编辑；
- OCR 本地模型，翻译走 OpenAI 兼容 API；
- 暗色、圆角、无系统白色控件的 UI。

项目根目录：

```text
D:\Practice\AItest\Deepseek_projects\Markshot
```

## 2. 构建与发布

```powershell
Set-Location 'D:\Practice\AItest\Deepseek_projects\Markshot'
Get-Process Markshot -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build -c Release
dotnet publish Markshot.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o publish
```

运行：

```text
publish\Markshot.exe
```

截图 `F1`，贴图剪贴板图片 `F3`。开发时也可用：

```powershell
dotnet run --project Markshot.csproj -- --demo
```

发布目录必须同时保留：

- `publish\models\v5\`：PP-OCRv5 模型；
- `publish\config.json`：用户配置（含 API Key，禁止提交）；
- `publish\cursors\`：运行时生成，可删除，程序会重建。

## 3. 配置和 API Key

`Core/AppConfig.cs`：

- 查找顺序：
  1. 可执行文件旁的 `config.json`；
  2. 可执行文件上级目录中的 `publish/config.json`（开发便利）；
  3. `%APPDATA%\Markshot\config.json`。
- `config.sample.json` 是模板，`ApiKey` 为空。
- 设置窗口保存会写当前 `ConfigPath`；如果 `%APPDATA%` 不可写，会尝试便携配置。
- 发布时不要在源码、README、提交记录中写入真实 Key。

当前翻译配置示例：

```json
"Translation": {
  "BaseUrl": "https://api.deepseek.com/v1",
  "ApiKey": "填写你自己的 key",
  "Model": "deepseek-chat",
  "TargetLanguage": "简体中文",
  "TimeoutSeconds": 60
}
```

> 如果自动翻译提示“尚未配置翻译 API Key”，先确认运行目录的 `config.json` 和 exe 在一起，或在设置里重新填写保存。发布/复制 exe 时只复制 exe 不带配置就会读不到 Key。

## 4. 代码地图

| 路径 | 职责 |
|---|---|
| `App.xaml` / `App.xaml.cs` | 应用入口、托盘、全局热键、截图/贴图生命周期、翻译窗口 |
| `UI/OverlayWindow.xaml(.cs)` | 截图框选、放大镜、批注画布、截图工具栏 |
| `UI/PinWindow.xaml(.cs)` | 贴图窗口、内联批注、缩放/拖动/穿透/历史 |
| `UI/AnnotationToolbar.xaml(.cs)` | 截图/贴图共用工具栏、工具、色板、副栏 |
| `UI/ColorPickerWindow.xaml(.cs)` | HSV 自选色板、色相/透明度、吸管、Hex/百分比 |
| `UI/SettingsWindow.xaml(.cs)` | 全局热键、工具键、保存、OCR、翻译、工具栏顺序数据 |
| `UI/TranslationWindow.xaml(.cs)` | 普通 OCR / 文本翻译窗口 |
| `UI/AutoTranslateWindow.xaml(.cs)` | 自动翻译进度 + 替换后图片预览/复制/保存/贴图 |
| `Annotations/Annotation.cs` | 所有批注模型和绘制：矩形、椭圆、线、箭、画笔、橡皮、马赛克、水印、文字、序号 |
| `Annotations/AnnotationEditor.cs` | 批注画布、选择框、8 手柄、旋转、坐标变换 |
| `Services/OcrService.cs` | RapidOcrNet；`RecognizeBlocksAsync` 返回文字框 |
| `Services/AutoTranslateService.cs` | OCR 分块 + 批量翻译 + 原位背景/文字渲染 |
| `Services/Translation.cs` | OpenAI 兼容 `/chat/completions` provider |
| `Core/AppConfig.cs` | 配置、路径、保存 |
| `Core/ToolCursors.cs` | 十字/IBeam/圆形画笔/旋转光标 |
| `Core/ToolHotkeys.cs` | 单字母工具键映射 |
| `Core/SliderDragBehavior.cs` | 滑槽点击跳转后继续拖动 |
| `Core/ClipboardService.cs` / `PinStore.cs` | 剪贴板、贴图存储/历史 |
| `Assets/Icons/*.png` | 已归一化的 64×64 图标，透明蒙版着色 |

## 5. 已实现的关键交互

### 截图

- 框选、移动、8 方向缩放；
- 截图框线/手柄光标为对应双向箭头；
- 鼠标在选区外、框线、调整边框时显示放大镜；在选区内使用批注工具时不显示；
- 截图工具栏在框选后出现；
- `Space` 在绘制中移动当前元素，未绘制时切换工具栏（截图/贴图点击状态）。

### 批注

- 对象存像素坐标；`AnnotationEditor.Scale` 映射画布坐标；
- 选中对象后 8 手柄缩放；**虚线边框**（非手柄）旋转光标，绕中心旋转；
- 顶部独立旋转圆环已删除；
- `R` 整体缩放选中对象，`E` 绘制中旋转，`Ctrl` 矩形圆角，右键取消；
- 选中对象滚轮调粗细/字号/马赛克程度；
- 同类型悬停高亮并可选；
- 画笔/荧光笔不参与选择；橡皮为像素盖回原图；
- 矩形/椭圆支持填充；马赛克按块采样；水印为可编辑重复文字；
- 文字/水印支持描边颜色、粗细；水印双击改文字。

### 文字（文档约定）

参考 `D:\Practice\AItest\Github_projects\Sketchplayer\docs\技术文档_批注工具.md`：

- 文字工具双击空白开始输入；
- `Enter` 完成；
- `Shift+Enter`、`Ctrl+Enter` 换行；
- `Esc` 结束；
- 文字编辑器带暗色背景和蓝色边框，落地后成为文字批注对象；
- 贴图内文字批注单击进入编辑并全选，之后可拖选；`Alt` 可无视文字拖动贴图。

### 贴图

- 贴图窗口透明、外圈模糊阴影；激活蓝色、未激活黑/灰；
- 内联批注工具栏，不新开编辑器窗口；
- 完成编辑后批注保留在对象层，再次进入可继续编辑；
- `Esc` / `Space` 收起批注栏会先提交当前批注并合成；
- 右键菜单：复制、另存为、翻译、编辑、设置、关闭所有贴图、置顶、锁定、穿透；
- 默认快捷键：`Ctrl+Alt+M` 鼠标穿透，`Ctrl+Alt+Z` 返回上一贴图；
- Q/W/E/F 贴图翻转/逆时针旋转/还原（可在设置改键；E 与橡皮工具键存在默认冲突，工具键优先）；
- 贴图支持滚轮缩放、Ctrl+滚轮透明度、中键重置视图。

### 颜色

- 工具栏色板：自定义（彩色外环 + 内色）+ 红/黄/绿/蓝/黑/白圆形；
- 自定义窗口：HSV 色板、彩虹色相、棋盘格透明度、圆形滑点、Hex、百分比、吸管、确认/取消；
- 吸管：隐藏窗口、十字光标、实时采样，再点一次完成；
- 颜色窗口标题栏可拖动；
- `ColorPickerWindow` 不使用 `DialogResult`，通过 `Accepted` 属性 + `Close()` 关闭，避免非对话框状态异常。

### 自动翻译

流程：

1. `OcrService.RecognizeBlocksAsync` 得到 `OcrTextBlock`（文字 + 像素框）；
2. `AutoTranslateService` 去重，按 30 行一批调用翻译；
3. 用 `[[MarkshotSeg]]` 分隔符还原每一行；分隔失败时回退逐行；
4. 按原文字框绘制译文，背景从文字框左右两侧逐行采样，文字颜色从框内高对比像素取，字号按框高/框宽收缩；
5. `AutoTranslateWindow` 显示进度和最终图片，可复制/保存/贴图。

已知限制：

- 复杂照片背景是水平插值，不是真正 inpainting；
- 远程 API 有延迟，不是 Chrome 本地即时翻译；
- 文字排版未完全复刻参考文档的 letter-spacing/line-height；
- 开启自动翻译后，复制/保存/贴图会打开替换结果窗口，而不是直接在原截图窗口内替换。

## 6. 已知问题 / 下一步优先级

1. **空格退出仍可能丢最后一步**：已加入 `CommitAnnotation()` 和 `_spaceExitGuard`，但需要在真实贴图上复测“绘制→松手→空格退出→再空格进入”的完整链路；重点检查 `ExitEditMode(true)` 的合成时机。
2. **滑条点击后继续拖动**：`SliderDragBehavior` 已直接挂到三个工具栏滑条和颜色窗口滑条，需实机确认点击轨道后不松手拖动值连续变化。
3. **自动翻译**：批量分隔符可能被模型改写；建议增加“逐行回退”进度和失败提示，或改为一次整段翻译 + 按行对齐。
4. **贴图文字拖选**：目前是批注文字；PNG 内烘焙文字需要 OCR 文本层，尚未实现。
5. **像素橡皮**：`EraserAnnotation` 用原图对应区域绘制，能遮挡局部像素，但不是真正的透明蒙版；撤销/再次编辑行为需要验证。
6. **设置面板**：手动输入已隐藏、工具栏排序区域已隐藏，但相关代码/配置仍保留；后续可彻底删除。
7. **按钮排序**：截图/贴图工具栏的七个工具顺序/显隐已由 `CaptureToolOrder`/`PinToolHidden` 等配置驱动；右侧动作按钮（撤销/OCR/保存等）未纳入。
8. **图标**：新素材已归一化到 64×64，但 `矩形/圆形/吸管/旋转` 等来自 `图标/` 的额外素材需要保留在 `Assets/Icons`；不要直接用 200×200 原图，否则视觉尺寸不一致。

## 7. 测试清单

### 截图

- [ ] 框选后拖动边框/8 手柄，光标方向正确；
- [ ] 在选区内画一笔：放大镜不出现；选区外移动：放大镜出现；
- [ ] 选区边框附近光标为缩放箭头；
- [ ] 选中批注：8 手柄缩放、虚线边旋转、顶部无旋转圆环；
- [ ] 调整结束后再次靠近手柄/边框，光标自动恢复；
- [ ] 画笔单击落点；画笔/橡皮光标随粗细改变；
- [ ] 橡皮擦掉局部像素，撤销恢复；
- [ ] 箭头柄粗细、燕尾形状；
- [ ] 文字工具双击创建、Enter 完成、Shift/Ctrl+Enter 换行、Esc 退出；
- [ ] 自动翻译：Key 配置正确时先显示进度，再显示替换图；无 Key 时应显示明确错误。

### 贴图

- [ ] 关闭所有贴图 / 设置菜单；
- [ ] 鼠标穿透快捷键；返回上一贴图快捷键；
- [ ] 按 B/T/S/L/V/E 能打开批注栏并激活工具；
- [ ] 贴图外圈阴影，激活蓝色；
- [ ] 编辑后空格收起，再次空格进入，批注仍在；
- [ ] 贴图文字 IBeam、拖选、Alt 拖动贴图；
- [ ] Q/W/E/F 翻转/旋转/还原（注意 E 工具键冲突）。

### 颜色

- [ ] 自定义圆环色块打开 HSV 窗口；
- [ ] 标题栏可拖动；
- [ ] 吸管：窗口隐藏、十字光标、实时采样、点击完成；
- [ ] 色相/透明度点击轨道后不松手可继续拖动；
- [ ] 确认/取消/右上角关闭都不报 `DialogResult` 错误。

### 设置

- [ ] 全局热键录制；
- [ ] 无“手动输入”选项；
- [ ] 无工具栏排序区域；
- [ ] 输入框宽度一致。

## 8. 参考资料与素材

- 用户提供的原始图标：`D:\Practice\AItest\Deepseek_projects\图标`
  - 已使用并归一化到 `Assets/Icons/`；
  - 旋转光标素材：`Assets/Icons/rotation.png`。
- 批注工具技术文档：
  `D:\Practice\AItest\Github_projects\Sketchplayer\docs\技术文档_批注工具.md`
- OCR 模型：`Markshot/models/v5`（PP-OCRv5）。
- 用户偏好：PixPin 风格、暗色圆角、无白色系统控件、中文 UI、不复制 PixPin 专有素材。

## 9. 常用调试

日志：

```text
publish\logs\app.log
bin\x64\Debug\net8.0-windows10.0.19041.0\logs\app.log
```

OCR/翻译测试入口（源码中）：

```powershell
Markshot.exe --ocr <图片路径>
Markshot.exe --translate <图片路径>
```

如果 `%APPDATA%\Markshot` 不可写，日志会显示回退到程序目录；这是沙箱/权限问题，不一定是程序 bug。

## 10. Git

当前分支 `main`。最后一次功能提交见 `git log --oneline -5`。
提交前不要 `git add` 任何 `config.json`、`publish/logs`、`publish/pins`、`cursors` 或真实 API Key。
