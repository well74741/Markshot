# Markshot 开发交接文档

> 面向接替开发的智能体/开发者。项目当前是 `0.8.24`，WPF + .NET 8，Windows 专用。
> 版本号约定（0.8 起）：每次修改按两位小数递增 0.8.01 → 0.8.02 …，写在 `Markshot.csproj` 的
> `InformationalVersion`（显示用，`Core/AppVersion.Display`）；`AssemblyVersion/FileVersion` 用对应的数字形式（0.8.02 → 0.8.2.0）。
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
- 画笔/荧光笔/画笔马赛克画完**不**进入选中（连续书写）；其余新建完成的元素 `SelectQuietly`（不切工具）并记为 `_freshSelection`：绘图工具仍激活时它只响应**手柄方块**
  （边线带、旋转带、框内都继续画），序号/画笔/荧光笔/画笔马赛克/橡皮工具下完全不响应（`AnnotationOps.SelectionHit`），
  方便紧挨着画下一个；滚轮/Ctrl+滚轮照常调它；用选择工具或单击选中后恢复完整调整框；为了能接着在刚画的笔迹上继续画，`AnnotationOps.InsideMoves`：
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

## 8.1.1 0.8.03 变更

- 旋转：调整框四角手柄外侧画弯曲双向箭头（`AnnotationEditor.RotateGripGeometry`，半径 13 DIP、±34°），
  命中 `RotateGripCenters` 9 DIP 内即旋转；对刚画完的元素（只响应手柄）也有效。原来的“角外 24 DIP 旋转带”仍保留在完整调整框里。
- 文字输入中的调整框：`InlineTextEditor` 自己处理——`AnnotationEditor.HitEditFrame`（手柄缩放、弯箭头旋转、文字外 4~10 DIP 的框带移动，文字本身留给光标）；
  拖动在 TextBox 上 `CaptureMouse`，宿主窗口在 TextBox 外按下时调用 `TryBeginFrameDrag`；不再结束输入，旧框不会留在原地。
- 水印从马赛克里拆成独立工具按钮（`ToolWatermark`，图标 watermark.png，`ToolFamily.Watermark`）；工具顺序配置新增 `Watermark`，
  旧配置缺它时插在 Mosaic 后面。
- 副批注栏（设置行）放进 `Core/SizeTransitionHost`：尺寸从当前值按统一曲线 240 ms 过渡到目标，内容贴着移动的下边一起滑出/滑回；
  宽度变化同样过渡。工具栏吃掉自己背景上的 MouseDown（以前贴图窗口把它当成点空白处 → 取消选中 → 设置行收起）。
- 恢复贴图：本次运行关闭的贴图存 `PinSnapshot`（原图、底图、可编辑批注、位置、缩放、透明度），`RestoreLastPin` 按栈逐个恢复到原位置；
  更早的（磁盘历史）恢复为图片。恢复过的条目从历史移除，下一次恢复更早的一个。
- 图标：`tools/make_toolbar_icons.py` 生成 arrow.png（加粗、与其它图标同框同笔宽）和 undo.png（参考图：左箭头 + U 形回弯），撤销按钮改用 `IconUndoMask`。
- 文字识别窗口重排：标题区、两张卡片（原文/译文，各带复制）、底部状态点 + 按钮；`FooterButton/PrimaryButton` 移到 App.xaml 共用；
  Ctrl+Enter 翻译；“贴到屏幕”没有译文时贴原文。

## 8.1.2 0.8.04 变更

- 绘制顺序 `AnnotationEditor.DrawOrder`：水印画在最后一个马赛克之后（马赛克不打码水印），其余顺序不变；画布、截图合成、贴图合成共用。
- 水印 `Shift`（图案偏移）：水印工具下按住拖动移动图案（撤销/右键撤回同普通移动），双击编辑文字。水印从独立按钮收进文字按钮展开项。
- 工具按钮显示上次选的样式（`AnnotationToolbar.ShowChoice`，按钮 Tag 也换成该工具）；分段/菜单切换样式后会重新配置设置行（画笔马赛克以前缺粗细就是因为没重配）。
- 橡皮：`Toolbar.EraserOpacity`；`EraseStroke.Strength` < 1 的笔画用 `PushOpacityMask` 擦淡（`PartialMasks`），满强度仍用裁剪；
  只有满强度擦除会让元素点不中。橡皮宽度 = 工具栏粗细（不再 ×2）。
- 调整手柄点击不再“变大”：`AnnotationOps.ResizeGrab` 把按下点换算成元素本身的手柄位置，并有 3 DIP 死区；贴图里画图工具下缩放/旋转以前不记撤销，已修。
- 右键撤回：`AnnotationHistory.CancelGesture`（弹出本次手势提交的快照、不进重做栈）；两个窗口的 `CancelGesture()` 覆盖所有拖动状态，
  截图窗口还会把拖动中的截图区域还原。`InlineTextEditor.CancelFrameDrag` 撤回文字框拖动。
- 贴图：`AnnotationEditor` 有 `Source` 且无截图选区时把元素裁剪到图片内（框和旋转箭头不裁）；阴影 `#48000000`。
  工具栏放在 `ToolbarHostWindow`（无激活、透明、随内容大小，归属贴图），默认在图片下方，拖动后记住相对图片的偏移，贴图移动时跟随。
- 截图区域：拖动时 3.5 DIP 粗线无手柄，松手后 2.6 DIP + 10 DIP 手柄（`AnnotationEditor.SelectionDragging`）。
- `SizeTransitionHost`：隐藏期间的变化在重新显示时直接到位（以前首次弹出“一帧出现”、再次打开从两排收成一排）。
- 文字识别窗：目标语言下拉（自动 + 9 种主流语言），自动 = 识别到中文译英文、其它译简体中文。
- 安装包：`installer/Markshot.iss`（Inno Setup 6，中文向导文本 `installer/ChineseSimplified.isl`，UTF-8 BOM），`tools/build_installer.ps1`
  发布到 `publish_installer/` 后编译到 `dist/`。安装器把运行目录写到 `HKCU\Software\Markshot\SetupDir`，作为默认更新路径。
- 更新：`Core/UpdateService`，找更新路径里 `Markshot-Setup-<版本>.exe` 中比当前新的（数字逐段比较）；启动 8 秒后及每 2 小时检查，
  每个新版本托盘提醒一次；设置版本号旁 ↻ 检查、“更新 x”按钮运行安装包并退出。

## 8.1.3 0.8.05 变更

- 样式切换统一走 `AnnotationToolbar.PickVariant` → `VariantChanged`：窗口里若选中元素 `AnnotationOps.CanConvert` 则转换
  （绘图工具下转换后继续用新样式、元素保持选中），不同按钮的工具按普通切换处理。`Tab` = `AnnotationOps.NextVariant`。
  工具快捷键经 `Toolbar.ChoiceFor` 取该按钮上次的样式；新增 `ToolShapeHotkey`（默认 M）。
- 元素剪贴板 `AnnotationClipboard`（UI/AnnotationOps.cs）：跨截图/贴图窗口，粘贴每次再偏移 16px，落在画布外则放到中间；马赛克粘贴后按本窗口底图重建。
- 工具栏显示/隐藏：截图窗口 `ShowToolbar(bool)`，贴图退出编辑时 `Motion.PopOut` 后再隐藏工具栏窗口；`Motion.IsHiding` 判断进行中的隐藏，
  再显示时 `PopIn` 从当前值反向（可打断）。设置行固定高 32。
- 贴图：查看模式先处理贴图自己的键（翻转/旋转/还原、T、L），再处理工具字母；拖动贴图时右键还原位置；阴影改为灰色、透明度降低；
  `UI/PinMagnifier`：鼠标在贴图上时 30ms 轮询 Alt（GetAsyncKeyState，不要求贴图激活），在独立穿透窗口里显示截图同款放大镜。
- 右键菜单（贴图）按状态增减；托盘菜单“恢复最近贴图 / 取消所有鼠标穿透”只在可用时出现。
- `ClipboardService.CopyImage`：`CopyImageAsFile`（默认开）时把 PNG 写到 %TEMP%\Markshot\clipboard 并加 FileDrop。
- 目标框架改为 `net8.0-windows`（不再带 25MB 的 Microsoft.Windows.SDK.NET.dll，未使用任何 WinRT API）。
  安装包改为发布文件夹 + Inno LZMA2 固实压缩：约 80MB（以前 105MB）。主要体积：.NET 运行时 + WPF/WinForms、onnxruntime/SkiaSharp、OCR 模型 ~30MB。
- Logo 去掉深色描边（看起来是黑边），32px 以上改为很淡的柔和投影，16~24px 无投影。

## 8.1.4 0.8.06 变更

- `SizeTransitionHost` 改为自己按帧推进（`CompositionTarget.Rendering` + `Motion.Bezier`）：WPF 动画在第一帧前读到的是旧值，
  选工具时同一帧两次布局会让过渡从终点重新开始，首次展开设置栏看起来是一下弹出。
- 画笔类光标 `ToolCursors.Get(..., scale)`：贴图传 `CursorScale = 缩放 × DPI`，光标圈 = 屏幕上实际笔宽；光标位图随尺寸变大（不再 60px 封顶）。
- 截图 → 贴图：`OverlayWindow.FinishWithTransition`：工具栏弹出收起、遮罩/选区框淡出 170ms 后再关闭窗口（期间不接受输入）。
- 悬停放大：App.xaml 里带 `Hl` 悬停底板的按钮/单选/开关模板，内容 `Cp` 160ms 放大到 1.1（文字分段 1.05）；色块在代码里 1.15。
- 贴图：方向键微调（Shift 10px）、拖动时 Shift 锁定横/竖方向（`Straighten`）；托盘中键截图。
- 打包：`tools/package.ps1`（UTF-8 BOM，Windows PowerShell 才能读中文）检查依赖并生成便携 zip + 安装包。

## 8.1.5 0.8.07 变更

- 托盘菜单改为 WPF `ContextMenu`（App.xaml 的暗色圆角样式），右键时在 1×1 透明前台窗口上以 AbsolutePoint 打开（否则点别处不会关闭）。
- 截图圆角：`OverlayWindow._cornerRadius`（像素），`AnnotationEditor.CornerDots / CornerRadiusFor`（圆点在圆弧内侧 12 DIP，拖动映射回半径），
  `DragMode.Rounding`；亮区、选区框、元素裁剪、`Compose` 输出都按圆角裁剪；`PinWindow.CornerRadius` 让贴图阴影/边框/合成保持圆角。
- 截图尺寸框 `SizeBox`：鼠标在框线 ±6 DIP 内出现（离开 14 DIP 外才消失），Enter 应用（左上角不动），Esc 收起。
- 编辑器单位修正：`unit = Scale`（每 DIP 的画布像素），以前 `1/Scale` 在高 DPI / 贴图缩放下手柄、旋转箭头大小与命中范围不一致。
- 贴图阴影：`PinShadowSize / PinShadowColor / PinShadowOpacity`；`Pad` = max(16, 阴影 + 4) 决定贴图窗口四周留白。保存设置后已开贴图立即生效。
- 截图打开动效 `StartDimConverge`：DimLayer 的径向 OpacityMask 半径 320ms 收缩到鼠标处，结束后移除。
- 工具栏点击后把键盘焦点交回画布（贴图还会激活自己），Tab / Esc 不再被吞；工具栏窗口收到的按键转给贴图。
- 更新源默认 GitHub（`UpdateService.DefaultRepository`，releases/latest 的 `Markshot-Setup-*.exe` 资产，下载到 %TEMP% 后安装）；
  私有仓库 API 返回 404，需公开仓库。`.github/workflows/release.yml`：推送 `v*` 标签自动打包并发布 Release。
- 版权：csproj `Copyright/Company/Product`，安装包 `AppPublisher/AppCopyright`；`THIRD-PARTY-NOTICES.md` 随包分发。可选签名 `tools/sign.ps1`。

## 8.1.6 0.8.08 变更

- 截图结束过渡 `OverlayWindow.FinishWithTransition(done, clearFrom, keepAnnotations)`：DimLayer 径向遮罩从一点（取消：鼠标处）或一个区域
  （贴图：选区）向外扩开 240ms 后关闭。取消路径统一 `CancelCapture()`（Esc / ✕ / 无区域时右键）。
- 截图转贴图不再等窗口关闭：App 给 overlay 设 `PinNow` 回调，`PinAndClose` 里立即 `CreatePin(..., fromCapture: true)`（贴图在截图窗口之上、
  像素相同，不淡入，`PlayLightSweep` 斜向光带 520ms，裁剪在贴图轮廓内），然后 overlay 再做变亮过渡；`PinnedDirectly` 告诉 App 不必再建。
- 套索截图：未选区域时按住右键拖动（`_lassoDraft`，Shift = 预览直线段，松开 Shift 落点），松开得到 `_lasso`（相对选区左上角的点 + 原始尺寸），
  选区为外接矩形；亮区 Clip、元素裁剪、`Compose` 都用 `LassoGeometry()`；套索选区只能移动不能缩放（无手柄/圆角点）。
  贴图 `ShapedShadow`：阴影用 Img 的 DropShadowEffect 跟随透明形状。
- 已有区域时右键（未拖动）→ `ResetCapture()` 回到选区阶段（批注一并清除）；区域内鼠标中键 → 贴图。
- 尺寸标签就是输入框（`SizeBox` + `SizeLabelField` 样式），编辑器自己不再画尺寸标签。
- 工具栏样式菜单打开期间 `Toolbar.IsMenuOpen`，窗口不抢焦点（以前抢焦点让菜单立刻关闭）。贴图编辑时阴影按“激活”处理，不再闪。
- 全局鼠标快捷键：`HotkeyGesture` 支持 鼠标中键 / 鼠标侧键1 / 鼠标侧键2（VK 4/5/6），`HotkeyManager` 用 WH_MOUSE_LL 钩子，
  命中（修饰键完全一致）时吞掉按下和对应抬起。设置里热键框聚焦时按中键/侧键即录入。
- 保存后提示 `SavedPromptWindow`（`Config.AskOpenFolderAfterSave`），截图保存经 `overlay.SavedPath`，贴图保存直接调用。
- 贴图翻转/旋转 `AnimateOrientation`：ScaleX/ScaleY 从 -1、或旋转从 90° 回到 0（240ms）。阴影设置实时预览 `App.PreviewPinShadow`。
- 设置左栏：更新按钮独占一行；底部“请杯咖啡”抽屉（拖动横条拉出二维码 `Assets/donate_qr.png`，拉满即停，单击收起）。

## 8.1.7 0.8.09 变更

- 更新：`UpdateService.FindNewestAsync(custom)` 总是检查内置 `DefaultRepository`，自定义路径（可空）另外检查，取较新者；
  `EffectivePath` 在未设置时返回空串。启动/定时检查与设置页都走它。
- 咖啡抽屉：横条在上、二维码在下（DrawerHandle 位于 DrawerPanel 之前），拉高 DrawerPanel 时横条一起上移。
- 贴图光带：`PlayLightSweep` 改为倾斜 22° 的整条光带，从左到右平移（220~340ms，随宽度），Loaded 优先级立即开始，裁剪在圆角内。
- 贴图 `OnLoaded` 先 `UpdateFrame()`：`ShapedShadow` / `CornerRadius` 是 init 属性，构造时还未赋值，套索贴图原先会留下矩形阴影。
  套索截图的原位翻译图也按套索形状裁剪。
- 截图变暗聚拢改用 `Motion.Fast` + EaseOut，与悬停窗口边框同步出现；提示条加入“右键拖动手绘截图（Shift 直线）”。
- 翻转/旋转过渡 150ms；旋转时调整 `_left/_top` 让贴图中心不动。
- 批注栏排序 `UI/ReorderBehavior.cs`：子元素用 Tag 作键；按住 420ms 拿起（移动超过 6px 视为普通拖动，取消），面板捕获鼠标，
  被拿起项 ±5° 抖动，按中心点算落位，邻居用 `Motion.SlideX` 让位；松开后重排 `Children` 并触发 `Reordered(keys)`。
  工具按钮和其下拉箭头被包进 `_groupPanels[key]` 一起移动；动作按钮（Ocr/Close/Pin/Save/Copy/Translate、Save/Apply）按 Tag 排序/隐藏。
- 配置新增 `CaptureActionOrder/Hidden`、`PinActionOrder/Hidden`；`UI/ToolbarCatalog.cs` 统一键、名称、图标与 `Order()` 清洗。
- 设置 → 保存与识别 → 批注栏按钮：两行（截图模式 / 贴图模式），每行工具 + 动作两组方块（无模板 Border，避免模板资源问题），
  单击切换隐藏、长按拖动排序；保存时写回四组 order/hidden，打开的贴图经 `ApplyShadowSettings` 同步。

## 8.1.8 0.8.10 变更：录屏（from SketchPlayer）

移植自作者 SketchPlayer 项目的录屏（参考其 `docs/技术文档_录屏.md`），按 WPF 重写；代码文件头都注明 “from SketchPlayer”。

- `Core/Recording/`
  - `FFmpeg`：找 `ffmpeg\ffmpeg.exe`（随附，次选 PATH）；`DetectH264Async` 按 nvenc → qsv → amf → mf 用 lavfi 黑帧试编，失败退 libx264（缓存）；
    `QualityArgs` 三档（crf 28/23/18 等）；`Atempo` 级联；`MuxPreviewAsync`（视频 copy + 各音轨 amix → AAC）；`ExportAsync`（`-ss/-t` 输入裁剪、
    `setpts` 变速、`fps`、偶数 scale；GIF 单命令 palettegen/paletteuse；`-progress pipe:1` 出进度；硬编失败退软编）。
  - `ScreenRecorder`：GDI `BitBlt(SRCCOPY|CAPTUREBLT)` 抓区域（物理像素、虚拟桌面坐标，跨屏天然拼好）到 DIB，`DrawIconEx` 画鼠标，
    按需 GDI+ 画按键药丸，`bgr0` rawvideo 管道写给 ffmpeg（录制时用“高清”档；libx264 时 ultrafast）。帧跟 `RecordClock`：
    来不及抓就重复上一帧补齐，视频时长 = 录制时间（暂停不计）。`Synthetic` 供测试用合成画面。
  - `AudioTrack`：NAudio `WasapiLoopbackCapture` / `WasapiCapture` 各写一个 WAV；按时钟补静音（回环在没声音时不给数据）、暂停丢弃、
    关闭开关写静音（只改“增益”）；`Audible` 决定是否混进成片。
  - `InputDisplay`：`KeyPills`（合并 ×n、最多 5 条、1.5 s + 0.4 s 淡出、排版数值同 SketchPlayer）与 `InputHook`（独立线程上的 WH_KEYBOARD_LL / WH_MOUSE_LL，
    只在“操作录制”开着时运行）。`Monitors`：物理像素的屏幕 / 工作区 / 缩放。
- `UI/Recording/`
  - `RecorderController`：唯一状态（区域、阶段 Setup/Countdown/Starting/Recording/Paused/Finishing/Preview、选项、Take），
    `Open/Start/TogglePause/StopAsync/Retake/RequestClose/Close/ExportAsync/CopyAsync/SetUiHidden/Toggle`；`_generation` 让过期的异步回调作废。
    偏好存 `AppConfig.Recorder`（操作录制不记）。临时目录 `%TEMP%\markshot-rec-*`，关闭后 1.5 s 删除，打开时清理一天前的残留。
  - `RecordFrameWindow`：每像素透明窗口（比区域大一圈 12 DIP），线画在区域外；准备阶段外圈 + 内侧 8 DIP 用 alpha=1 接鼠标（拖动 = 移动，
    八把手 = 缩放，对边钉住），其余透明穿透；倒计时数字、“正在生成预览…”。
  - `RecordBarWindow`：**普通不透明窗口**——WPF 的 AllowsTransparency 窗口 `SetWindowDisplayAffinity` 会失败（实测返回 false），
    只有普通窗口能在录制期间设 `WDA_EXCLUDEFROMCAPTURE`。放置：区域下方 → 上方 → 区域内底边；兼任任务栏按钮（加 WS_MINIMIZEBOX：
    点任务栏 = 最小化 = 收起录制框和预览；关闭 = 退出录屏，没保存先问）。
  - `RecordPreviewWindow`：区域原位的 MediaElement 预览（有效区循环、倍速、静音），单击播放、按住 300 ms 或挪动 > 5 px 拖动整套界面；右键菜单。
  - `TrimBar`：时间条（琥珀三角剪头尾、白色播放头）。
- 入口：托盘「录屏」、截图工具栏 `Record` 按钮（`CaptureAction.Record` → 用选区打开）、`AppConfig.RecordHotkey`（打开 → 开始 → 停止）。
- 打包：`tools/get_ffmpeg.ps1` 把 ffmpeg 放进 `tools/deps/ffmpeg`（gitignore；优先 `MARKSHOT_FFMPEG`，否则下载 ffmpeg-static b6.1.1），
  csproj 有它时作为 Content 输出到 `ffmpeg\`（单文件发布时也在 exe 旁边）；`package.ps1` 依赖检查会调用它，CI 同样自动下载。
- 测试：harness `reccore`（合成画面 → 编码 → 混音 → 导出 / GIF / 按键规则）、`recflow`（控制器全流程：打开、改尺寸、录制 + 暂停、预览、导出、
  未保存询问、倒计时、重录、取消）。真实抓屏、系统声音、麦克风未进自动测试。

另：设置新增「批注栏」页；长按批注栏按钮拖动修复（按钮自身的鼠标捕获丢失事件会冒泡到面板，原先误判为拖动结束）；
截图尺寸输入框点别处即退出编辑；套索绘制时放大镜跟随；贴图「应用」改用图标库的“确认”图标。

## 8.1.9 0.8.11 变更

- 截图尺寸框：窗口是焦点范围（focus scope），`Keyboard.Focus(window)` 会把焦点还给范围里记住的元素（就是那个输入框），
  所以点外面、Enter、Esc 都退不出来。改成 `FocusWindow()`：先 `FocusManager.SetFocusedElement(this, this)` 再 `Keyboard.Focus(this)`（overlay 里所有交还焦点处）。
- 录屏工具栏只在**压在录制区域上**时才设 `WDA_EXCLUDEFROMCAPTURE`（`UpdateProtection`，按窗口矩形与区域相交判断）：被排除的窗口在远程控制 / 屏幕共享里也看不见，
  用户远程操作时“录屏时工具栏不见了”。区域外的工具栏本来就录不进去。
- 操作录制改为**画在录制框窗口里**（区域左下角，SketchPlayer keyPills 同一套数值），录像通过 BitBlt(CAPTUREBLT) 直接录到它，屏幕和视频一致；
  `ScreenRecorder.DrawKeys` 保留但不再使用。录制框窗口在非调框阶段加 `WS_EX_TRANSPARENT`，药丸 / 倒计时不挡点击。`KeyPills.Changed` 触发重绘 + 100 ms 刷新淡出。
- 拖录制框时工具栏闪：框和工具栏每次移动都 `HWND_TOPMOST` 重新插入 z 序互相压。现在只在创建时定一次层级（工具栏在上，预览出现后 `BringToFront`），
  移动用 `SWP_NOZORDER`；尺寸输入框固定宽度，拖动时工具栏不再改变大小。
- 打开录屏时 `RecordDimWindow`：全屏透明点透窗口，区域外压暗 820 ms（0→1 占 20%，保持到 50%，淡出）。暂停时暂停按钮琥珀色闪。
- 任务栏右键菜单 = 跳转列表（`RecorderCommands.UpdateJumpList`，随阶段变：开始 / 取消 / 停止 / 暂停 / 继续 / 退出）。条目运行
  `Markshot.exe --recorder <start|stop|pause|exit>`：第二个实例在单实例检查前把命令用命名事件 `Local\Markshot.Recorder.<cmd>` 交给正在运行的 Markshot 后退出；
  主实例 `RecorderCommands.Listen` → `RecorderController.Command`。

## 8.1.10 0.8.12 变更

- 录屏窗口在显示前就放好位置（框 / 预览在 SourceInitialized 里 Place；工具栏先放在屏幕外，ContentRendered 后归位），不再从别处闪过来。
- 拖动录制框时三个窗口用 `WindowMoves`（BeginDeferWindowPos / DeferWindowPos / EndDeferWindowPos）一次挪完，工具栏不再闪；
  工具栏 Refresh 只在值真的变了才赋值（拖动时每次鼠标移动都会刷新）。
- 鼠标变慢 / 漂移：① 抓屏去掉 `CAPTUREBLT`（它每次抓屏都隐藏再显示鼠标指针）；② 鼠标键快捷键的 WH_MOUSE_LL 钩子从 UI 线程挪到独立线程
  （`HotkeyManager.StartMouseHook`，SketchPlayer 文档 8.1 #11 的教训：钩子在忙碌的主线程上会拖慢全系统鼠标）；③ 抓屏线程不再提高优先级。
  ⚠ 去掉 CAPTUREBLT 后，录像里的按键药丸靠 DWM 合成后的屏幕读到分层窗口——需要真机确认药丸仍在录像里。
- 重录 = 回到调框，不自动开始；从截图进入录屏（`Open(area, fromCapture: true)`）不压暗、不做缩放入场。
- 托盘菜单录屏项随阶段变化（`App.RecorderMenuItems`）；跳转列表命令新增 `show`。`RecorderCommands.Prefix` 可替换（测试用，避免发到正在运行的 Markshot）。
- 操作录制药丸底色更透明（alpha 0.84 → 0.59，描边 0.22 → 0.16）。
- 套索贴图的光带用贴图本身（ImageBrush）做 OpacityMask，只划过抠出的形状。
- 截图顶部提示在每块显示器顶部各一份（副本绑定主提示的 Visibility / Opacity）。
- 画笔圈光标改用 `LoadImage(..., size, size, LR_LOADFROMFILE)` 按真实尺寸加载：`LoadCursorFromFile` 会缩到系统光标尺寸，圈超过 64px 后比实际擦除范围小；
  旋转光标仍按系统尺寸（`trueSize: false`）。
- 文字工具设置栏加「文字 / 水印」切换（`TextVariants`）；水印设置加开关（`WatermarkGroup`，关 = 去掉水印、可撤销，`_watermarkOff` 期间不自动重建）。
- 取色器：吸管遮罩层不再激活（WS_EX_NOACTIVATE）——颜色面板是模态的，焦点被抢回导致吸色立刻结束；Esc / Enter 由面板处理（Esc 先退出吸色，再按关闭面板）。
- 批注按钮旁的 ▾：菜单打开时再点同一个箭头 = 关闭（300 ms 内刚关的菜单不再立即重开）。

## 8.1.11 0.8.13 变更：主题与界面大小

- `Core/Theme.cs`：XAML 里的灰色 / 白色叠加色都改成 `{DynamicResource T_AARRGGBB}`（键 = 暗色主题下的颜色，`Core/ThemeKeys.g.cs` 列出全部键），
  浅色由 `Theme.ToLight` 推出：白色叠加 → 黑色叠加（alpha ×0.7），浅灰文字 → 深灰，深色背景 → 浅色背景（保持灰阶顺序）；有色相的颜色不变。
  命名画刷（PanelBackground、TextPrimary、Hover…、菜单系统色）也由 Theme 设置，引用改成 DynamicResource，所以切换时已打开的窗口实时变色。
  代码里新建的颜色用 `Theme.Brush(dark)`（一次性）或 `Theme.Bind(element, prop, dark)`（跟随切换）。跟随系统读
  `HKCU\…\Themes\Personalize\AppsUseLightTheme`，`SystemEvents.UserPreferenceChanged` 时重新应用。标题栏 `Theme.TrackTitleBar`。
  不跟主题的：截图画面上的元素（选区框、手柄、放大镜）、录制框、按键药丸、取色器色板、强调色。
- 浅色主题二维码 `Assets/donate_qr_light.png`（灰点 168 → 64，中间 logo 不变），设置窗口随主题切换。
- `UiScale`：共享的 `ScaleTransform`（中 1.0 / 大 1.15）作为 LayoutTransform 挂在批注栏内容、设置 / 翻译 / 取色窗口内容（`AttachWindow` 同时按比例改窗口尺寸）、
  截图提示 / 尺寸框 / 翻译条、录屏工具栏与弹出面板、保存提示；菜单和提示气泡用类处理器在打开时挂上。
- 设置新增「样式」页：主题、界面大小、贴图阴影（从快捷键页挪来）。选择即预览；未保存关闭时 App 重新应用已保存的主题和大小。
- 修复：录屏预览不再用 MediaElement 报的时长替换 ffmpeg 的（Windows 播放器有时报短，导致出点提前、导出少了结尾）。

## 8.1.12 0.8.14 变更

- 批注按钮旁 ▾ 的开 / 关：菜单打开时点箭头，菜单的 Closed 与按钮的 Click 先后顺序不固定，先 Click 时会立刻开出第二个菜单再被关掉（闪一下）。
  现在箭头的 PreviewMouseLeftButtonDown 记下“菜单开着 / 刚关”（`_suppressMenuOpen`），随后的 Click 只关不开；`_openMenu` 记当前菜单。harness `menu` 覆盖两种顺序。

## 8.1.13 测试进仓库

- `tests/Markshot.Tests`：原先放在 %TEMP% 的 harness，按模式运行（`Markshot.Tests.exe batch1` …），路径经 `Repo.Root` 找仓库根目录。
  `Markshot.csproj` 里 `Compile/Page/None Remove="tests\**"`，测试代码不会编进程序。
- `tests/run.ps1`：每个套件单独进程运行，出现 `FAIL` 行、崩溃或非零退出即失败；截图与日志写到 `tests/out/`（gitignore）。可只跑部分：`tests
un.ps1 batch1 menu`。
- CI：`.github/workflows/ci.yml` 在 push main / PR 时跑全部套件；`release.yml` 打包前先跑测试，不通过就不发布。
- 不进默认列表的：translate（要本地 mock LLM，`tools/mock_llm.py`）、真实抓屏 / 系统声音 / 麦克风（会录到真实内容）。

## 8.1.14 大文件拆分（只搬代码，行为不变）

- `OverlayWindow`：`.xaml.cs`（字段、构造、生命周期）+ `.Selection.cs`（选区 / 套索 / 尺寸框 / 提示 / 工具栏摆放 / 放大镜）+ `.Input.cs`（鼠标键盘）
  + `.Annotations.cs`（批注工具、元素选择、文字水印、撤销）+ `.Finish.cs`（合成与关闭：复制 / 保存 / 贴图 / 翻译 / 录屏 / OCR）+ `.Translation.cs`（原位翻译）。
- `PinWindow`：`.xaml.cs`（显示、缩放、拖动、阴影、快照）+ `.Editing.cs` + `.Toolbar.cs` + `.Orientation.cs`（翻转旋转、光带）+ `.Translation.cs` + `.Menu.cs`。
- 拆分前后逐个标识符计数一致（只多了新文件的类声明）。

## 8.1.15 0.8.15 变更

- 长截图：`Core/ScrollStitcher`（逐行哈希；先找上下不动的行 = 页头页尾，再在中间部分找使“新帧第 i 行 = 旧帧第 i+d 行”匹配最多的位移 d，
  单色行不参与判断；匹配率 ≥ 92% 才认，回滚 / 不动不追加；最长 30000 px）。`UI/ScrollCaptureSession`：选区外一圈点透的蓝框 +
  旁边的预览面板（放不下时压在角上并排除出抓屏），每 100 ms `CopyFromScreen`，预览 ≤ 3 次/秒重建；完成 → 复制 + 贴图（按屏高缩放）。
  入口：批注栏动作 `Scroll`（`CaptureAction.Scroll`，图标 = 图标库「双向箭头」转 90°）。测试 `stitch`：合成页面带页头页尾、重复帧、不规则位移，逐像素比对。
- 贴图历史：`PinHistory`/`PinStore` 上限 10 → 30，`PinStore.Delete`；`UI/PinHistoryWindow`（托盘「贴图历史…」），App 的 `RestorePinEntry`（本次运行内关闭的按快照恢复）。
- `Core/Support.cs`：`Secrets`（DPAPI，`TranslationConfig.ApiKeyProtected`，旧明文 `ApiKey` 读入后不再写出）、`AutoStart`（HKCU Run，取消时也删安装器的启动快捷方式）、
  `Diagnostics.Export`（桌面 zip：logs、去掉 Key 的 config、系统 / 显示器信息）。
- 录屏：保存的录像提示框多一个「播放」；托盘「录屏区域回中」（`RecorderController.Recenter`）。

## 8.1.16 0.8.16 变更：图片编辑器

- `UI/ImageEditorWindow : PinWindow`：直接继承贴图窗口的编辑逻辑，全部批注工具共用一份代码。贴图侧的扩展点：`IsEditor`、`MinZoom`、`EditorKey`、`KeysBelongToUi`，
  虚方法 `ComposeEditedImage / Copy / Save / BuildContextMenu`，部分字段改 `private protected`。`IsEditor` 为真时不移动窗口、不淡入淡出、不画阴影、不退出编辑模式。
  构造时把 `RootGrid` 从窗口搬进 `ScrollViewer`，工具栏放顶部（`AnnotationToolbarMode.Editor`），右侧面板放图片操作。
- 撤销：`AnnotationHistory` 新增 `CaptureExtra / RestoreExtra / ExtraSize`，每步可带附加状态；编辑器存 `State(Origin, Base, Adjust)`（位图冻结后共享），图片总量超 1.5 GB 时丢最旧步骤。
  `ChangeCount` 判断有无未保存修改。
- 调色永远从 `_origin` 计算（滑条拉回去能完全还原）；拖动时屏幕用不超过 1400 px 的代理图预览，停手 350 ms 或需要结果时 `BakeAdjust` 算真实像素。
  几何操作把当前结果变成新的 `_origin`，调色归零。镜像 / 90° / 矩形裁切时批注跟随且可编辑；缩放、任意角度、路径裁切、梯形校正会把批注合并进像素（可撤销）。
- `Core/ImageOps`：`Adjust / Crop / Resize / Rotate / PathCrop / Keystone`（8 元线性方程解透视变换，双线性采样，透明不渗色）；`ImageFile`：WIC 读取（含 EXIF 方向），失败回退 SkiaSharp（WebP）；WebP 用 SkiaSharp 写，JPEG / BMP 铺白底。
- `UI/EditorOverlay`：裁切 / 路径 / 梯形校正的交互层，坐标为图片像素。
- 从贴图进入：原贴图隐藏（`_parked`，`ApplyBounds` 不再 SHOWWINDOW），「贴到屏幕」替换它；未贴就关闭编辑器则恢复显示。
- 长截图：`ScrollStitcher` 改为在已拼内容里定位当前帧（上一位置前后一屏内逐行比较），来回滚动不重复追加；横向 = 帧转置后用同一算法；窗口显示前定位（`PlaceBeforeShow`）。
  `ScrollCaptureSession` 期间注册全局 Tab / Esc（`RegisterHotKey`，结束即注销）。取消 = `StartCapture(selection: area)` 回到截图。
- 测试：`imageops`（像素运算、文件往返）、`imageeditor`（集成：打开、调色、撤销、几何、裁切、贴图交接）、`stitch`（来回滚动）。

## 8.1.17 0.8.17 变更：标注补齐、元素识别、贴图多选

- 折线 `PolylineAnnotation : PenAnnotation`（直线段绘制；移动、缩放、镜像、裁切、橡皮都按画笔处理，但能被选中 / 复制）。
  放置过程是 `PolylineDraft`（单击放拐点、拖动也成段、Shift 45°、Backspace 撤回、Finish 去掉跟随点）；截图窗口和贴图窗口各有 `_polyline`，
  `FinishPolyline / DropPolyline / PolylineKey`，换工具、写回、合成前都会先结束折线。折线是箭头按钮副栏里的小类型，`Tab` 只在箭头 ↔ 直线间切换。
- 箭头样式 `ArrowStyle { Sharp, Open, Triangle, Double }`，副栏「样式」分段按钮（`ArrowStyleGroup`），选中箭头时改样式会改该箭头。
- 撤销交接：`AnnotationHistory.Convert / Adopt / ForEachStored`。截图转贴图 / 编辑器时把每一步换算到贴图坐标（`OverlayWindow.PinHistory`），
  `App.WithHistory` / `ImageEditorWindow.AdoptHistory` 接上；贴图退出编辑不再清空撤销；贴图翻转 / 旋转时同步映射已存的步骤（编辑器里几何操作本身是撤销步骤，不映射）。
- 元素识别 `Core/ElementDetector`：UI Automation 从窗口往下逐层找包含鼠标的最小子元素（带缓存请求，400 ms 预算，后台线程），结果由小到大；
  截图窗口 `_hoverChain / _hoverLevel`，滚轮切层级，框位置用 `CompositionTarget.Rendering` 做 140 ms 过渡。`AppConfig.DetectElements / DetectWindows`。
- 显示鼠标 `Core/CursorCapture`：抓屏时同时取指针（黑底 / 白底各画一次求透明度），截图窗口 `SetShowCursor` 在原图和带指针图之间切换
  （屏幕、高亮、放大镜、马赛克、合成都用同一张）。`AppConfig.CaptureShowCursor / CaptureCursorKey`（默认 Ctrl+M）。
- 辅助线：`CrossH / CrossV` 两条 1 物理像素线，`CrosshairKey` 默认 Alt。`CaptureKey`：组合键按下生效；单个 Alt / Ctrl / Shift 在松开且期间没按别的键时生效。
- 截图窗口拦截 `WM_SYSCOMMAND / SC_KEYMENU`：以前单按 Alt 会让窗口进入菜单模式，鼠标移动事件停掉，放大镜不跟手。
- 贴图多选 `UI/PinWindow.Group.cs`：静态 `Group`，Shift 单击加选（连同上一次点的贴图）、Ctrl 单击减选，`GroupMark` 虚线框；拖动 / 方向键 / Esc / 右键关闭作用于整组。
- OCR：`OcrService.Layout` 按文字框重建版面（行聚类、列间距 > 1.5 行高用 Tab、缩进、段落空行），`RecognizeAsync` 默认走它。
- 测试：`annotate`（折线、箭头样式、撤销交接、贴图多选、OCR 版面）、`capture`（元素识别、悬停层级、显示鼠标、辅助线按键）。

## 8.1.18 0.8.18 变更：画面识别、编辑器画布、主题化保存、打开方式

- 0.8.17 的元素识别在真实桌面上基本看不到效果：Electron / Chromium 应用关掉了无障碍（UIA 只给一个铺满窗口的文档，0 个子元素），
  Qt 等应用每次查询 0.3–3.7 秒、旧的 400 ms 预算走不到文字层。现在：
  - `Core/ScreenSegmenter`：从截图本身找元素。亮度边缘 → 三档间距（13 / 22 / 40 DIP 横向，按缩放比放大）用前缀和做膨胀 → 洪水填充标号，
    盒子取原始边缘的范围 + 2 px。每个窗口区域算一次（约 80 ms，后台线程），之后每次查找 O(1)；鼠标不在边缘上时取包含它的最小盒子（图片中间、卡片留白）。
  - 截图窗口 `RunElementDetection`：先画面分块（快）再 UIA（慢，预算 1.5 s，从拿到窗口根元素后开始计时），两次结果都用 `Dispatcher.BeginInvoke` 送回界面线程；
    `BuildHoverChain` 把两类结果按面积排序、去掉肉眼分不出的重复框，窗口放最后。
  - 测试：`capture` 用合成画面验证文字段 / 图片 / 段落；`hover` 起一个子进程显示窗口，跨进程验证悬停框；`deskprobe`（不在默认列表）只读地看真实桌面上的识别结果。
- 编辑器：`ScrollViewer` 换成自由画布 `_stage`（Canvas + `_pan`）。滚轮在鼠标处缩放，中键拖动，`F` = `Fit()`（适应并居中），至少留 48 DIP 在视野内。
  分区「重置」：`GeometryStep(EditGroup, …)` 在一组操作第一次发生前记下 `State` + 批注；换了另一组操作或撤销 / 重做就清掉（按钮变灰）。灰度按钮去掉（`ImageAdjust.Gray` 仍在）。
- `UI/SaveImageDialog`（主题化保存窗口，四处保存都用它）和 `ThemedMessage`（编辑器未保存提示、保存失败）。截图 / 贴图保存也支持 WebP / BMP / TIFF（统一走 `ImageFile.Save`）。
- 打开方式：`Core/FileAssociation`（HKCU 下 ProgID `Markshot.Image`、`Applications\Markshot.exe`、各扩展名 `OpenWithProgids`；启动时按 `AppConfig.RegisterOpenWith` 刷新）；
  安装包也写同样的键、卸载时删除。`OpenRequests`：第二次启动带图片参数时经命名管道交给已运行的 Markshot；`App.OpenFileInEditor`。
  设为默认只能由用户在 Windows 的选择窗口里确认（`SHOpenWithDialog`）。测试 `files` 用自己的注册表键和管道名，不碰真实设置。

## 8.1.19 0.8.19 变更

- 悬停识别：0.8.18 在鼠标仍在“最小框”内时不再查找，离开一张图片到空白处后最小框变成整页，之后再也不重新判断。
  现在每次移动都重算：画面分块（区域准备好后 O(1) 查表；没准备好时后台 `ScreenSegmenter.Prepare`，完成后刷新）+ 上次 UIA 结果里仍包含鼠标的元素；
  UIA 查询在后台排队（只查最新位置）。框没变时不重画。预选框改为 3 DIP 蓝色实线（画在区域内侧）。测试 `capture` 里有图片墙来回移动的用例（`_windowFinder` 注入窗口）。
- 长截图 `ScrollStitcher` 重写：每帧切成 行 × 16 竖条的格子，只有变化且非纯色的格子参与；每个竖条内多数一致才计票（固定侧栏 / 滚动条 / 动画不干扰）；
  在 ±一屏内找位移，负位置 = 往上滚，新行插到最前；跳太远时在已拼内容里 `Locate`。固定页眉 / 页脚 = 位移时没参与且基本没变的边缘行。
  `KeptColumns` 按列统计“变化且跟随滚动”的比例，去掉两侧固定面板和滚动条（只在那一侧确实有内容时裁）。
- 编辑器：工具栏改为底部悬浮（`_toolbarLayer` Canvas，`_toolbarAnchor` = 中心比例 + 距底，拖手柄后 `DragFinished` 记住位置）；状态栏 `UpdateHint` 按工具 / 选中 / 会话显示操作。
  `Alt + 滚轮` 大小、`Ctrl + 滚轮` 透明度；编辑器不建 `PinMagnifier`，并拦截 SC_KEYMENU。中键平移先释放别人的鼠标捕获、用 `Mouse.OverrideCursor`，丢失松开时自动结束。
  放大卡顿的原因是棋盘格画刷铺在整张放大后的图上（几万像素见方）；改为只铺在可见部分（`_checker`）。最大缩放 3200%，≥300% 用最近邻。
- `ImageAdjust` 增加 `Hue`（CSS hue-rotate 矩阵）和 `Curve`（x,y 对；`ToneCurve.Table` = Fritsch–Carlson 单调三次）；`SameAs` 按值比较曲线。`UI/CurvesWindow`（直方图、加点 / 拖动 / 拖出删除 / 右键删除，取消还原）。
- 网格形变：`EditorOverlayMode.Mesh`（5×5 点），`ImageOps.MeshWarp`（每格逆双线性映射 + 双线性采样），预览用 ≤700 px 的小图实时算；应用时批注合并进像素（裁切组，可重置 / 撤销）。
- 更新：`UI/UpdateWindow`（主题化，下载进度），`UpdateService.InstallQuietly` 用 `/VERYSILENT … /RESTARTAFTERUPDATE` 启动安装包；
  安装脚本 `[Code] RestartAfterUpdate` 在该参数下装完重新打开 Markshot，`WizardStyle=modern dynamic`。其余 `MessageBox` 全部换成 `ThemedMessage`。

## 8.1.20 0.8.20 变更

- 截图：调整选区大小（`DragMode.Resizing`）时按住空格整体移动选区，松开后 `_dragOrigin = _selection`，继续按住的边 / 角调整（`ResizeRect` 是绝对坐标，不跳）。
- 编辑器「应用角度」：连续旋转（中间没有别的撤销步骤：`_history.ChangeCount == _rotationChange`）从 `_rotationBase` 按累计角度转，不再转上一次的结果（透明角越转越大）。
- 网格形变改为 PS 式变形：`Core/WarpMesh`（交点 `Anchors`、横边手柄 `Across`、竖边手柄 `Down`，每格是四条三次贝塞尔边的 Coons 面）。
  `SplitAt(u, v)` 交叉拆分（被切的边 de Casteljau 分割，新线过 1/3、2/3 处的点求手柄）；`MoveAnchor` 带手柄，`MoveHandle` 在内部交点镜像对侧手柄；`Locate` 采样 + 细化求逆。
  `ImageOps.Warp` = 64×64 细网格 + `MeshWarp`（加了输出尺寸），画布取变形后的外接框。`EditorOverlay` 的 Mesh 模式画曲线边、手柄、交点和虚线十字预览。

## 8.1.21 0.8.21 变更

- 网格形变每次只交叉拆分一次（`EditorOverlay.WarpSplit`）；之后在空白处按下 = 抓最近的交点拖动（`PressWarp` / `DragWarpTo`，记住抓取偏移，不跳到指针），光标变手形，不再显示拆分十字。

## 8.1.22 0.8.22 变更

- 稳定性：调整框缩放 / 旋转结束时贴图窗口没有释放鼠标捕获（编辑器整体点不动），现在 `OnMouseLeftButtonUp` 释放；
  未处理 UI 异常不再弹模态框（布局循环里连弹模态框会卡死闪退），改为节流的托盘气泡；截图编辑中（有批注 / 工具 / 选中元素）右键不再重置截图。
- `CutoutAnnotation : PenAnnotation`（抠除）：不绘制、不可选，`Cutouts.Exclude` 把所有洞从可见区域剔除；`AnnotationEditor` 的裁剪和合成（贴图、截图、编辑器 Flattened）都用它，
  `CutoutTarget`（贴图的 Img、截图的 BrightImage）同步打洞。按下时 `Arm + CommitArmed` 生成一步撤销，取消时 `CancelGesture` 还原。
- 马赛克块取平均色（预乘 alpha）；序号缩放按手柄方向取尺寸；截图框选后线宽不变；悬停框动画 70 ms；翻译状态条避让工具栏。
- 工具栏：`Clear`（清空批注，`SetCanClear`）加入截图 / 贴图动作，贴图加 `Close`；旧配置的顺序会自动补上新按钮。橡皮擦设置行 `EraserModes`（擦除 / 抠除）。
- 编辑器：`EditorOverlayMode.Transform`（双击图片，`ResizeFromHandle` + 框外旋转，`TransformTo` 先缩放后旋转）；开始任何会话前清掉预览角度；裁切尺寸标签 `_cropSize`（同截图 SizeBox 样式）；
  `ResetAll`；`CheckerBrush` 跟主题；贴到屏幕后 `CloseWithoutAsking`；分区「裁切与变形」；角度 ±180。
- 录屏：按键图标留边；`ShowMenu` 同一按钮再点关闭菜单。设置抽屉横条上方加咖啡图标，悬停变回文字。

## 8.1.23 0.8.23 变更

- 抠除改成橡皮擦按钮的一个工具 `AnnotationTool.Cutout`（同一变体族，▾ 菜单 / Tab / 设置栏最左侧 `EraserModes`，`Variant` 组的单选）。
- 贴图上抠除的洞透明：有洞时阴影改为沿形状（与套索贴图相同的 `DropShadowEffect`，`AnnotationEditor.CutoutsChanged` 触发 `UpdateFrame`），不再露出背后的阴影底板。
- 编辑器：矩形裁切清掉 `_rotationBase`（之后的角度从裁切结果算，不会还原到裁切前的图）；调节框会话中把 `Img` 透明、批注层隐藏（不再整体压暗），没盖住的地方显示棋盘格；
  会话开始后把键盘焦点给窗口（Enter / Esc 直接生效）；尺寸标签 `_cropSize` 同时服务裁切和调节框（调节框按中心改尺寸，放在旋转后外接框上方）。
- 截图悬停框动画回到 140 ms。设置抽屉的咖啡图标区域也响应悬停 / 拖动。

## 8.1.24 0.8.24 变更

- 键盘焦点：`InputLayer` 可获得焦点，`ReclaimKeyboard` 拿不到时退回窗口；编辑器窗口 `PreviewMouseDown` 点到非输入框的地方就收回键盘（尺寸标签点空白退出、复制粘贴批注在输入过尺寸后也能用）。
- 编辑器：开始裁切 / 路径 / 网格变形 / 调节框前，若有预览中的角度先 `ApplyAngle` 应用（不再丢掉）。
- 调节框：四角默认等比（Shift 自由），边手柄自由；`TransformHit` 新增 "outside"（框外 36 DIP 以内仍是旋转），点得更远触发 `CommitRequested` → 应用并退出（未改动则取消）。
- 右侧「尺寸」改为「画布尺寸」：`CanvasTo` / `ImageOps.Canvas`，图片居中不缩放，多出部分透明，批注随之平移仍可编辑；`ResizeTo`（缩放）保留给调节框和测试。
- 图片元素 `ImageAnnotation`：Ctrl+V / 右键「粘贴」时，系统剪贴板比上次复制的批注新（`GetClipboardSequenceNumber`）就把图片作为元素贴进画布（最多占画布 80%）；可移动、缩放、旋转、复制。
- 编辑器右键在元素上：复制元素 / 删除元素 / 调整上下层（置于顶层、上移一层、下移一层、置于底层，`PinWindow.MoveLayer`，可撤销）。
- 编辑器浮动工具栏有自己的排序与显示：`EditorToolOrder/Hidden`、`EditorActionOrder/Hidden`，`EditorActions` 按钮带 Tag 且可长按拖动排序；设置「工具栏」页新增「图片编辑器」一行；保存设置后 `ImageEditorWindow.ApplyToolbarOrder` 刷新。
- 设置抽屉：咖啡图标换成新样式；按目标状态 `_drawerOpen`（而非动画中的高度）决定显示，收回二维码后咖啡图标重新出现。

## 8.2 动效（0.8.01 起，参照 Sketchplayer《技术文档_动效》总原则）

- `Core/Motion.cs`：唯一曲线 `StandardEase` = cubic-bezier(0.4,0,0.2,1)（XAML 里 `<core:StandardEase />`），
  时长 `Fast 160 / Base 240 / Slow 320`，滑动 220；只动 Opacity / RenderTransform；不写 From 的动画从当前值接着走。
- `PopIn / PopOut`：透明度 + 6 DIP 位移 + 0.97 缩放，160 ms；`PopOut` 播完再 Collapse（中途又显示则作废）。
- `PopOnShow`：元素每次变为可见时弹入（可见性本身立即生效，布局不等动画）——截图工具栏、工具栏设置行、翻译状态条、贴图编辑工具栏。
  `FadeOnShow`：译文层、贴图状态提示。`EnterPage`：设置页切换淡入 + 8 DIP 横移。
- App.xaml：按钮悬停改为底板淡入淡出（`Hl` 层 Opacity 160 ms），分段按钮（类型/语言）选中底板交叉淡化；
  ContextMenu 打开弹入；MenuItem 为紧凑模板（18 DIP 勾选列 + 文本 + 快捷键，无图标槽），分隔线 `MenuItem.SeparatorStyleKey`。
- 贴图：出现时淡入；关闭时先取消 Closing、淡出 160 ms 后再真正关闭。超出贴图上限和退出程序时用
  `PinWindow.CloseImmediately()`（不走淡出：`CreatePin` 的循环会阻塞 UI 线程，等不到淡出计时器）。取色面板：弹入，关闭前弹出。
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
