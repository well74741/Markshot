# Markshot 开发交接文档

> 面向接替开发的智能体/开发者。项目当前是 `0.8.01`，WPF + .NET 8，Windows 专用。
> 版本号约定（0.8 起）：每次修改按两位小数递增 0.8.01 → 0.8.02 …，写在 `Markshot.csproj` 的
> `InformationalVersion`（显示用，`Core/AppVersion.Display`）；`AssemblyVersion/FileVersion` 用对应的数字形式 0.8.1.0。
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

> 如果原位翻译状态条提示“尚未配置翻译 API Key”，先确认运行目录的 `config.json` 和 exe 在一起，或在设置里重新填写保存。发布/复制 exe 时只复制 exe 不带配置就会读不到 Key。

## 4. 代码地图

| 路径 | 职责 |
|---|---|
| `App.xaml` / `App.xaml.cs` | 应用入口、托盘、全局热键、截图/贴图生命周期、翻译窗口 |
| `UI/OverlayWindow.xaml(.cs)` | 截图框选、放大镜、批注画布、截图工具栏 |
| `UI/PinWindow.xaml(.cs)` | 贴图窗口、内联批注、缩放/拖动/穿透/历史 |
| `UI/AnnotationToolbar.xaml(.cs)` | 截图/贴图共用工具栏、工具、色板、副栏 |
| `UI/ColorPickerWindow.xaml(.cs)` | HSV 自选色板、色相/透明度、吸管（全屏取色层）、Hex/百分比；拖空白处移动 |
| `UI/PinTextLayer.cs` | 贴图图片文字层：OCR 行框 → 按字宽估算字符框，拖选/双击选行/复制 |
| `tools/make_icon.py` | 生成 `Assets/markshot.ico`（16~256 全尺寸，小尺寸按像素对齐单独绘制） |
| `UI/SettingsWindow.xaml(.cs)` | 全局热键、工具键、保存、OCR、翻译、工具栏顺序数据 |
| `UI/TranslationWindow.xaml(.cs)` | 普通 OCR / 文本翻译窗口 |
| `UI/InlineTextEditor.cs` | 截图/贴图共用的原位文字编辑器（透明字形 TextBox 叠在实时渲染的文字上） |
| `UI/AnnotationStyler.cs` | 工具栏样式 ↔ 批注对象的映射（两个窗口共用） |
| `Annotations/CreationGesture.cs` | 绘制中的几何量分离：拉伸 / 空格平移 / E 旋转（支点=起点）/ Ctrl 圆角；笔迹 Shift 直线、空格整体平移 |
| `UI/AnnotationOps.cs` | 两个窗口共用：按工具创建、过小丢弃、橡皮绑定、命中、缩放映射、水印、IME 真实按键、截图→贴图坐标转换 |
| `Annotations/AnnotationHistory.cs` | 整体快照撤销/重做（克隆批注，位图共享），含 Arm/CommitArmed 与合并检查点 |
| `Annotations/Annotation.cs` | 所有批注模型和绘制：矩形、椭圆、线、箭、画笔、橡皮、马赛克、水印、文字、序号 |
| `Annotations/AnnotationEditor.cs` | 批注画布、选择框、8 手柄、旋转、坐标变换 |
| `Services/OcrService.cs` | RapidOcrNet；`RecognizeBlocksAsync` 返回文字框 |
| `Services/ImmersiveTranslator.cs` | 原位翻译：OCR 行测量 → 段落合并 → 跳过已是目标语言 → 并行批量翻译（缓存）→ 逐批重绘 |
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
- 选中对象滚轮调粗细/字号/序号半径/水印大小（区域马赛克调程度），`Ctrl+滚轮` 调该对象透明度（5%/格，最低 5%）；
  共用实现 `AnnotationOps.WheelSize / WheelOpacity`，撤销按 ("wheel"|"opacity", 对象) 合并；
- 激活工具且无选中：滚轮预设粗细，`Ctrl+滚轮` 预设透明度（`AnnotationToolbar.NudgeOpacity`，按工具记忆在 `_toolOpacity`；水印改它自己的透明度滑条）；
- 透明度：`Annotation.Opacity`，`DrawItem` 里统一 `PushOpacity`（水印自己处理）；`AnnotationOps.Create` / `AnnotationStyler.Apply` 写入；
- 新建完成的元素 `SelectQuietly`（不切工具）；为了能接着在刚画的笔迹上继续画，`AnnotationOps.InsideMoves`：
  画笔/荧光笔/画笔马赛克/橡皮工具，或任意绘图工具对直线/箭头/笔迹类选中对象，框内只有点在元素本身上才移动；
- 序号缩放保持圆心不动（以前圆心跟着手柄走，看起来整体滑动）；画笔/画笔马赛克缩放按比例映射点；
- 同类型悬停高亮并可选；
- 画笔/荧光笔不参与选择；橡皮为像素盖回原图；
- 矩形/椭圆支持填充；马赛克按块采样；水印为可编辑重复文字；
- 文字/水印支持描边颜色、粗细；水印双击改文字。

### 渲染与橡皮（0.8 起）

- `AnnotationEditor.DrawItem` 是唯一绘制入口：画布、截图输出 `Compose()`、贴图合成 `ComposeEditedImage()` 都走它
  （以前合成直接调 `Draw()`，**丢了旋转**，表现为“贴图应用后批注瞬间变化”）。
- 橡皮不再是一个盖回原图的元素：松手时 `AnnotationOps.BindEraser` 把擦痕换算到每个被擦元素自己的未旋转坐标系，
  存进 `Annotation.EraseMasks`；绘制时用 `Geometry.Combine(..., Exclude)` 裁掉。元素移动/缩放/旋转时洞跟着走，
  被擦掉的地方 `IsErasedAt` 命中不到；没擦到任何元素不占撤销步。拖动中用 `LiveEraser` 实时预览。
- 马赛克（框选/笔刷）是“透镜”：整幅画布生成一次块图（只读采样行），元素只负责裁剪；移动后打码的是新位置下的内容。
  块图绘制用 `DrawingGroup` + NearestNeighbor，避免合成时被插值糊掉。
- 水印：每张画布一个，插在最底层，`Rect` 跟随选区/贴图；无调整框；水印工具下悬停字形高亮、双击编辑；
  设置只有大小、透明度、颜色。
- 绘制形状：`CreationGesture` 按文档 §4.3 几何量分离，旋转支点为起点、以“绕中心旋转+平移”存回元素，
  修复“拉伸时轴心漂移”；修饰键结束后按相对位移继续拉伸，不跳。
- 拖动选中元素时按住 `E` 旋转、`R` 缩放（绕中心），期间其它按键都被拖动吞掉，不会切到橡皮。
- 截图/贴图窗口关闭 IME（只给文字框开），否则中文输入法把空格、字母快捷键变成 `ImeProcessed`，
  表现为“拖拽时按空格移动选区失效”；按键统一走 `AnnotationOps.RealKey`。

### 选择 / 调整框（0.8 起）

- `AnnotationEditor.HitFrame`：8 个手柄**和整条边框**都是缩放，框外角落 24px 内是旋转，框内是移动；
  任何工具下只要有选中元素，手柄/边框/旋转区都优先生效。
- 缩放光标 `ResizeCursor` 按元素旋转角取最近的双向箭头；旋转光标为程序生成的浅色弯箭头。
- `AnnotationOps.ResizeFromHandle` 在元素自身（旋转后）坐标系里算，对边/对角固定；Shift 拖角等比。
- 画笔/荧光笔/橡皮光标：白色亮环 + 淡暗边，画笔内圈为当前颜色，直径等于实际笔宽（荧光笔 4×、橡皮 2×）。
- 滑条 `SliderDragBehavior`：点击轨道先跳值，再把这次按下转交给 Thumb（`RaiseEvent(MouseLeftButtonDown)`），
  由 WPF 原生拖动接管，同一次按下可继续拖（工具栏和取色面板的所有滑条）。

### 文字

参考 `Sketchplayer/docs/技术文档_批注工具.md` §4.4、§6：

- **所见即所得**：编辑时文字由 `TextAnnotation.Draw` 实时绘制（与合成输出同一函数），
  上面叠一个字形透明的 `TextBox`（样式 `InlineTextBox`）只负责光标、选区和输入法。
  两者对齐经离屏实测：TextBox 字形比 `FormattedText` 右移 2 DIP、垂直 0，
  `InlineTextEditor.TextBoxInsetDip` 补偿后 125% 缩放下包围盒误差 0px；
  行高两边都用 `LineHeight = 字号 × 1.3` + `BlockLineHeight`。
- 文字工具：单击空白新建（第一行垂直居中在点击处）；单击已有文字 → 光标落在点击位置编辑；
  按住拖动 → 移动（≥3 DIP 才算拖动）。选择工具（贴图里即未选工具）：单击选中，再单击已选中的文字进入编辑，
  双击或 `F2` 直接编辑，拖动移动。
- 输入结束后文字保持选中；输入中拖调整框的手柄/边框/角外 → 结束输入并像形状一样缩放/旋转。
- 拖动开始时清掉悬停高亮框（以前高亮框留在原地，看起来“外圈分离”）。
- `Enter` 完成，`Shift/Ctrl+Enter` 换行，`Esc` 完成（不再丢弃输入），清空内容即删除；`Ctrl+B` 粗体，滚轮改字号。
- 点击工具栏（滑条已设 `Focusable=False`）不会结束输入，样式实时作用到正在编辑的文字。
- 工具栏按工具分别记忆大小（文字默认 20，不再继承画笔的 3px 变成 8px 字）。
- 字段：`Bold`、`Fill`（=背景标签，文字自动取反差色）、描边先画外圈再填字，细描边不吃字形。
- 撤销：开始编辑时 `Arm` 快照，结束时只有内容/样式变了才 `CommitArmed`；空的新文字不占步数。

### 贴图

- 贴图窗口透明、外圈模糊阴影；激活蓝色、未激活黑/灰；
- 内联批注工具栏，不新开编辑器窗口；
- 完成编辑后批注保留在对象层，再次进入可继续编辑；
- `Esc` / `Space` 收起批注栏会先提交当前批注并合成；
- 右键菜单：复制、另存为、翻译、编辑、设置、关闭所有贴图、置顶、锁定、穿透；
- 默认快捷键：`Ctrl+Alt+M` 鼠标穿透，`Ctrl+Alt+Z` 返回上一贴图；
- Q/W/E/F 贴图翻转/逆时针旋转/还原（可在设置改键；E 与橡皮工具键存在默认冲突，工具键优先）；
- 贴图滚轮缩放、Ctrl+滚轮透明度：只要没激活绘图工具，编辑模式里也生效（激活工具时滚轮调工具/选中元素大小）；
  按住中键拖动移动贴图，中键单击（未移动）重置视图。
- `F`：只撤销大小/透明度和记录在 `_orientation` 里的翻转/旋转（逐个逆操作），**不清批注**；
  翻转/旋转作用在 `_baseImage` 上再重新合成（以前作用在已合成图上，再编辑会重影），擦痕随 `ReorientMasks` 镜像/转向。
- 右键“清除批注内容”（编辑模式下可撤销）。
- 图片文字层 `PinTextLayer`：贴图显示、应用编辑、翻转、原位翻译后在后台 OCR；查看模式下文字上为 IBeam，
  拖动选中（蓝色高亮）、双击选整行、Ctrl+A 全选、Ctrl+C / 右键复制；空白处拖动移动贴图；按住辅助键（默认 Alt）无视文字。
- 截图时画的批注：`PinAndClose` 把不含批注的底图和 `AnnotationOps.ToPinSpace` 转换后的批注一起交给贴图，
  进入编辑后可继续修改（马赛克图案按贴图底图重建，水印铺满贴图）。

### 颜色

- 工具栏色板：自定义（彩色外环 + 内色）+ 红/黄/绿/蓝/黑/白圆形；
- 自定义窗口：HSV 色板、彩虹色相、棋盘格透明度、圆形滑点、Hex、百分比、吸管、确认/取消；
- 吸管：面板不隐藏；吸管按钮激活时为强调色、全局 `Mouse.OverrideCursor = Cross`；铺一层 alpha=1 的全屏取色层，
  移动实时取色；按下+松开都由取色层吃掉（松开时才确定，避免松开事件落到下面的窗口），单击 = 确认颜色并关闭面板；Esc/右键取消；
- 自选色板的 alpha 即元素透明度（水印工具下即水印透明度）；
- 面板高度随内容（`SizeToContent`），拖动任意空白处移动面板；
- 颜色窗口标题栏可拖动；
- `ColorPickerWindow` 不使用 `DialogResult`，通过 `Accepted` 属性 + `Close()` 关闭，避免非对话框状态异常。

### 原位翻译（Chrome 式）

入口：截图工具栏翻译开关（状态存 `ImmersiveTranslate`，设置页同一开关）；贴图右键“原位翻译 / 显示原文”。

1. `OcrService.RecognizeBlocksAsync` 得到行框；
2. `ImmersiveTranslator.Measure` 逐行量像素：外圈背景色（中位数）及是否纯色、墨色、
   **墨迹的垂直范围**（只取与框中心相连的墨迹行，PP-OCR 的框约为字高 2 倍且会压到上下行）、
   由墨迹高度按字形推算的字号（CJK ÷0.88；拉丁按有无大写/升部、降部估算）；
3. `GroupParagraphs`：上下紧邻、左对齐、字号相近、墨色相同、同文种、且上一行“写满”（换行）才合并成段；
   菜单/列表等短行保持独立；
4. `NeedsTranslation` 跳过数字/符号和已是目标语言的文字（不重绘）；
5. 按 24 条 / 2400 字一批，最多 3 批并发；`TranslateBatchAsync` 发送 JSON 数组、要求等长 JSON 数组返回，
   格式不符时逐条回退；结果按（目标语言, 原文）缓存；
6. 每批返回立即重绘整图并通过 `IProgress` 上报，截图选区上方的状态条显示进度，可“显示原文/显示译文”；
7. 绘制：纯色背景整块填充，渐变/照片按行左右插值；单行标签居中并可向右略扩，段落首行对齐原位置向下重排；
   原文较粗时用 SemiBold。

覆盖层：`TranslateLayer` 在截图与批注之间；`Compose()` 把译文像素合成进复制/保存/贴图结果；
橡皮/马赛克取色源为 `EffectiveScreen`（含译文）。选区移动/缩放后 350ms 自动重译（同一选区命中缓存）。
WPF 绘制必须在 UI 线程：`TranslateAsync` 不用 `ConfigureAwait(false)`，OCR/HTTP 本身在后台。

本地验证可运行 `python tools/mock_llm.py`，Base URL 填 `http://127.0.0.1:8123/v1`（本机地址免 Key），
它支持批量 JSON 数组格式并做小词典伪翻译。

已知限制：复杂照片背景是行内插值，不是真正的 inpainting；竖排文字、旋转文字不处理。

## 6. 已知问题 / 下一步优先级

1. **空格退出仍可能丢最后一步**：已加入 `CommitAnnotation()` 和 `_spaceExitGuard`，但需要在真实贴图上复测“绘制→松手→空格退出→再空格进入”的完整链路；重点检查 `ExitEditMode(true)` 的合成时机。
2. **滑条点击后继续拖动**：`SliderDragBehavior` 已直接挂到三个工具栏滑条和颜色窗口滑条，需实机确认点击轨道后不松手拖动值连续变化。
3. **原位翻译**：需要用真实 Key 在多种网页/应用截图上实测段落合并阈值与配色；复杂背景考虑真正的 inpainting。
4. **贴图文字拖选**：OCR 只给行框，字符位置按字宽比例估算，长英文行的选区边界可能偏一两个字符。
5. **像素橡皮**：`EraserAnnotation` 用原图对应区域绘制，能遮挡局部像素，但不是真正的透明蒙版；撤销/再次编辑行为需要验证。
6. **设置面板**：已重做为左侧导航（快捷键 / 翻译 / 保存与识别）；手动输入、工具栏排序界面已删除，
   `CaptureToolOrder` 等配置项仍由工具栏读取，只能改 `config.json`。
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
- [ ] 文字工具单击创建、单击已有文字编辑、拖动移动；Enter 完成、Shift/Ctrl+Enter 换行、Esc 完成；
- [ ] 中文输入法输入文字（组合串应实时显示）；编辑中改颜色/字号/粗体/背景/描边、滚轮改字号；
- [ ] 撤销/重做：移动、缩放、改样式、改文字、删除各占一步；
- [ ] 原位翻译：开关后选区内文字被替换，状态条显示进度，可切换原文；移动选区后自动重译；复制结果含译文；
- [ ] 无 Key 时状态条显示明确错误与“重试”。
- [ ] 画完元素即为选中；滚轮调粗细、Ctrl+滚轮调透明度；激活工具后 Ctrl+滚轮预设透明度，颜色预览与百分比同步；
- [ ] 刚画完的笔迹框内继续画笔不会拖动它；序号缩放圆心不动；
- [ ] 马赛克：类型 区域/画笔/水印 + 程度；水印：大小/倾斜/透明度/颜色；
- [ ] 所有滑条：点击轨道跳到该处后不松手继续拖；
- [ ] 吸管：按钮高亮、十字光标、实时取色、单击即确认并关闭、下方窗口不受点击影响；
- [ ] 下拉小菜单紧凑；菜单/工具栏/状态条弹入，按钮悬停渐变。

### 贴图

- [ ] 关闭所有贴图 / 设置菜单；
- [ ] 鼠标穿透快捷键；返回上一贴图快捷键；
- [ ] 按 B/T/S/L/V/E 能打开批注栏并激活工具；
- [ ] 贴图外圈阴影，激活蓝色；
- [ ] 编辑后空格收起，再次空格进入，批注仍在；
- [ ] 贴图图片文字 IBeam、拖选、双击选行、Ctrl+C；空白处拖动贴图；Alt 拖动无视文字；
- [ ] 截图批注 → 贴图后进入编辑仍可修改；F 不清批注；滚轮/中键在编辑模式下也缩放/移动；
- [ ] 打开设置时贴图仍可操作；
- [ ] Q/W/E/F 翻转/旋转/还原（注意 E 工具键冲突）。

### 颜色

- [ ] 自定义圆环色块打开 HSV 窗口；
- [ ] 标题栏可拖动；
- [ ] 吸管：面板不消失、实时取色、点击完成且不选中批注、Esc 取消；拖空白处移动面板；
- [ ] 色相/透明度点击轨道后不松手可继续拖动；
- [ ] 确认/取消/右上角关闭都不报 `DialogResult` 错误。

### 设置

- [ ] 全局热键录制；
- [ ] 三个页面都不出现白色系统控件，快捷键页无需滚动；
- [ ] API Key 默认掩码、可显示；测试连接结果显示在按钮旁；
- [ ] 原位翻译开关与截图工具栏开关同步。

## 8. 参考资料与素材

- 用户提供的原始图标：`D:\Practice\AItest\Deepseek_projects\图标`
  - 已使用并归一化到 `Assets/Icons/`；
  - 旋转光标素材：`Assets/Icons/rotation.png`。
- 批注工具技术文档：
  `D:\Practice\AItest\Github_projects\Sketchplayer\docs\技术文档_批注工具.md`
- OCR 模型：`Markshot/models/v5`（PP-OCRv5）。
- 用户偏好：PixPin 风格、暗色圆角、无白色系统控件、中文 UI、不复制 PixPin 专有素材。

## 8.1 设置 / 翻译条 / 图标（0.8 起）

- 设置窗口改为非模态（`App.ShowSettings` 用 `Show()`，`SettingsWindow.Saved` 代替 `DialogResult`），打开时贴图可继续操作；
  打开期间全局热键仍暂停（录制快捷键需要），关闭后恢复。
- 截图翻译条：`[原文语言 | 译文语言]` 两段切换（高亮=当前显示），像 Chrome 翻译栏；内容本来就是中文时自动译成英文
  （`ImmersiveTranslator.TranslateAsync(..., switchWhenSame: true)`），语言判断 `DetectLanguage` 按“1 个汉字 ≈ 3 个拉丁字母”加权。
- 工具栏翻译按钮激活后只变色，不再追加文字。
- Logo：`Assets/markshot.ico`（exe `ApplicationIcon`：任务栏、资源管理器、任务管理器；托盘从资源加载小尺寸；
  设置/翻译窗口 `Icon`）。修改样式改 `tools/make_icon.py` 后重新运行。

## 8.2 动效（0.8.01 起，参照 Sketchplayer《技术文档_动效》总原则）

- `Core/Motion.cs`：唯一曲线 `StandardEase` = cubic-bezier(0.4,0,0.2,1)（XAML 里 `<core:StandardEase />`），
  时长 `Fast 160 / Base 240 / Slow 320`，滑动 220；只动 Opacity / RenderTransform；不写 From 的动画从当前值接着走。
- `PopIn / PopOut`：透明度 + 6 DIP 位移 + 0.97 缩放，160 ms；`PopOut` 播完再 Collapse（中途又显示则作废）。
- `PopOnShow`：元素每次变为可见时弹入（可见性本身立即生效，布局不等动画）——截图工具栏、工具栏设置行、翻译状态条、贴图编辑工具栏。
  `FadeOnShow`：译文层、贴图状态提示。`EnterPage`：设置页切换淡入 + 8 DIP 横移。
- App.xaml：按钮悬停改为底板淡入淡出（`Hl` 层 Opacity 160 ms），分段按钮（类型/语言）选中底板交叉淡化；
  ContextMenu 打开弹入；MenuItem 为紧凑模板（18 DIP 勾选列 + 文本 + 快捷键，无图标槽），分隔线 `MenuItem.SeparatorStyleKey`。
- 贴图：出现时淡入；关闭时先取消 Closing、淡出 160 ms 后再真正关闭（应用退出时不拦）。取色面板：弹入，关闭前弹出。
- 前景色（图标变蓝）不做颜色动画：样式里的画刷是冻结的，Storyboard 克隆后会变成本地值盖掉选中态触发器。

## 9. 常用调试

离屏验证（不动桌面、不抢鼠标）：可以建一个引用 `Markshot.csproj` 的临时 WPF 控制台项目，
用普通 `Application` 加载 `App.xaml` 的资源字典（**不要** `new App()`：它会排队执行真实的
`OnStartup`，注册托盘和全局热键并读取用户配置），再用 `RenderTargetBitmap` 渲染控件/窗口检查布局和文字对齐。


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
