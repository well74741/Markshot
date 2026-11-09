# Markshot 开发交接文档

> 面向接替开发的智能体/开发者。项目当前是 `0.8.70`，WPF + .NET 8，Windows 专用。
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

## 8.1.70 0.8.70 变更

- 帮助：`tools/make_help.py` 生成 `help/index.html`（单文件：文字、按钮图标与 logo 内嵌，动图是 SVG + SMIL，`?demo=名字&t=秒` 定格单个动图便于检查）。
  csproj 以 Content 复制到 exe 旁；`Core/HelpDocument.Open` 用默认浏览器打开（没有本地页面时打开 GitHub README；`OpenOverride` 供测试）。入口：托盘「帮助」、设置标题栏「?」。
  改版本号后要重新运行脚本（round70 检查页面版本）。
- 设置窗口改为自绘标题栏（`WindowChrome`，CaptionHeight 按 SettingsFactor 缩放，GlassFrameThickness 0,0,0,1 保留阴影）：标题栏颜色随主题即时变化，
  修复系统标题栏换主题后保持旧色、拖动时标题像被选中、按钮消失（直到窗口重新激活）；帮助 / 最小化 / 最大化 / 关闭按钮自绘，最大化时 BorderThickness 补上溢出屏幕的边框。
  其他窗口 `Theme.ApplyTitleBar` 设置后 SWP_FRAMECHANGED + RedrawWindow 立即重绘非客户区。
- 窗口识别：本进程带标题栏（WS_CAPTION）的窗口（设置、编辑器、历史、文字识别）也能识别，`WindowTarget.Own` 时只框整个窗口、不做 UI Automation（自己进程的 UIA 可能卡住）。
- 长截图：
  - 精确匹配只认重叠区内大部分变化格子都对得上的距离（`Explained`，≥60%）：原来不一致的列直接不计，列表里每项相同的头像列能撑起一个错误距离 → 重复拼接。
  - 精确匹配不够精确（< 97%，小数滚动时文字中间行上下一行也相同）时用容差匹配细化，最小二乘求小数部分 `_phase`；容差粗扫也用“介于两行之间”判断（半像素混合时原先粗扫就丢掉了正确距离）。
  - 每帧位置按已拼好的内容校准（`_bodyMeans` + `Anchor` ±2 行，误差不累积）；跳得太远时 `LocateLoosely` 按亮度在两屏内找回（有相似的其他位置则放弃，等下一帧）。
  - 固定列判断用“介于两像素之间”（小数滚动时头像列、页边被当成固定侧栏裁掉）；“只有局部在变”只统计有内容的格子；抓帧 100 → 70ms。
  - round70 模拟：整数 / 小数、来回滚、大步长、相似列表项、1100×860 大区域，逐行核对结果（0 错行），大区域约 29ms/帧。
- 文字识别：`Services/OcrLayout.Analyze` → `OcrDocument(Text, Tables)`（`OcrService.Layout` 只取 Text，原有行 / 缩进 / 段落规则不变）。
  行内间隔 > 1.5 行高分单元格；连续 ≥2 行、每行 ≥2 格、按“没有单元格跨过的竖向空隙”切出的列对齐（≥70% 行）= 表格；列 ≤3、每列 ≥8 行高宽、
  行几乎占满列宽、平均 ≥12 字 = 分栏正文（按栏输出）。分数：上下两个框横向重叠 ≥60%、间隔小，旁边有一个框的中线正好在两者之间（表格单元格与行同高，不会误判）→ `(a)/(b)`。
  长图（长边 > 2200 且 > 短边 1.6 倍）按长边切成 max(1400, 短边) 的段、重叠 160，框中线落在本段“自有范围”才保留（识别器检测时会把图缩到约 1000px）。
  PP-OCRv5 中文字典本身含 π ± ≤ ≥ √ ∑ ² ℃ 等，未做替换；上下标在同一行时识别成普通字符（如 r2），多层公式只能读出文字。
  `TranslationWindow.ShowTables`：「表格」开关 + Grid（首行加粗、单元格可选中）；复制时 `ClipboardService.CopyTables`（Unicode 文本 + CF_HTML）。
- 长截图工具条加「文字识别」（`ScrollCaptureAction.Ocr` → `ShowOcr`）。
- 贴图：`AppConfig.PinTextSelect`（默认开；关闭时 `RefreshTextLayer` 不识别、`IsTextBypassHeld` 恒真）；`TextBypassModifier` 改为按键录制框（单独修饰键、组合或普通键都可）。
- 帮助页：箭头头部用 animateMotion rotate=auto 沿线移动；新增套索、文字识别（表格）、录屏、图片编辑器四个动图；特色功能 `<strong class="star">` 加粗高亮；「关于」：作者 guoqing.wu、版权、GitHub、微信二维码（`donate_qr_wechat_light.png`，白底便于扫码）。
- Win + 鼠标：复制 / 贴图沿用 `CaptureCornerRadius`（`ImageOps.RoundCorners`，贴图 cornerRadius 同步），划的框显示同样圆角。

## 8.1.69 0.8.69 变更

- 长截图：小数像素滚动（显示缩放、平滑滚动）时各行被重采样、哈希永远对不上 → 每帧都判"滚动太快"。`HashCells` 另算每格平均亮度（`Cells.Mean`），
  精确匹配失败后走 `FindShiftLoosely`：亮度差 ≤ 6 算相同；粗扫（步长 3，相位 0）取前 6 个候选及各自 d−1，再逐行用"介于上一帧两行之间"判定（前一帧本身也是混合行），
  ≥ 90% 才认；随后在 0/.25/.5/.75 中选最贴合的小数相位 `_phase`，`_viewFraction` 累计小数部分，位置取整不漂移。round69 用左侧固定栏 + 右侧小数滚动模拟验证。
- 截图左上角先黑一块：预建窗口是默认 800×600，显示瞬间先按旧尺寸画。`Load` 里先设 Left/Top/Width/Height 并 `SetWindowPos` 到屏幕物理范围。
- 窗口识别：微信 / QQ 等外面套一层无标题的阴影 / 边框窗口（分层、工具窗口或不激活），`IsWrapper` 识别后继续找，若下一个窗口在它里面（各边内缩 0–48px、面积 ≥ 60%）则取里面那个。
- 悬浮按钮置顶：`SetWinEventHook(EVENT_OBJECT_SHOW)`（跨进程、跳过自身），顶层 TOPMOST 窗口一出现就在 Input 优先级 `KeepOnTop`；定时器降为 3 秒兜底。
- 长截图工具栏：改为透明分层窗口，内容与批注栏一致（圆角 9、底色 `0xF21E1E1E`、内阴影 BlurEffect），放在选区外 10px（下 → 上 → 右 → 左，都放不下才放在选区内并排除截图）。
- 占用：关闭贴图的快照上限改为 20 个 / 300MB；截图结束 4 秒后（空闲且不在截图中）`CompactOnce` + 非阻塞 GC，归还全屏截图的大对象内存。

## 8.1.68 0.8.68 变更

- 截图呼出：实测（3640×1920）新建一个全屏 WPF 窗口到首帧约 300–470ms，大部分是建窗口本身（空窗口也一样）。现在 `OverlayWindow.CreateSpare()` 空闲时预先建好
  （1×1 占位图、`EnsureHandle`，`_spare` 时 SourceInitialized 不显示），`App.TakeSpareOverlay` → `Load(screen, bounds)` 后照常 ShowDialog，窗口部分降到 ~70ms。
  每次截图结束、启动后、`AppConfig.Saved`（设置保存）后在 ApplicationIdle 重建。`ActiveWindow` / `InitialSelection` / `OcrOnSelect` 改为可写属性。
  悬浮按钮 `Suspend` 不再淡出并等待（最多 150ms + 两次 DwmFlush），直接透明并隐藏、一次 DwmFlush。
- 窗口识别：`WindowDetector.Detect` 按 DWM 实际边框（`GetBounds`）命中，跳过 cloaked（挂起的商店应用、其他虚拟桌面）、WS_EX_TRANSPARENT、全透明分层窗口，以及副屏任务栏。
- 悬浮按钮：浮窗按钮的提示 `PlaceTip`（上方，靠近屏幕顶端时下方）；主按钮提示写点击行为；`_topTimer` 每 1.5 秒重新置顶（不在拖动 / 菜单时）。
- 任意程序复制图片：`ClipboardWatcher`（消息窗口 + AddClipboardFormatListener，只读），120ms 后读图 → `NoteScreenshot`（700ms 去重）。测试里不启用。
- 矩形圆角：`AppConfig.RectCornerRadius`，无选中时改圆角即记下，配置工具时读回。
- 长截图工具栏：`Core/RoundedWindow`（DWM 圆角 + 圆角窗口区域，非透明窗口）+ 批注栏底色、圆角 9、无描边。
- 设置窗口拖动：配置路径和底部状态两行 `Marquee` 无限滚动 → 窗口一直以 60fps 重绘。`Marquee` 改为滚两遍后停（悬停再滚），状态提示缩短，有离线包时不提示。
- 设置说明文字全面精简（提示行 ≤ 45 字）。

## 8.1.67 0.8.67 变更

- 弯曲的尖头箭头：`DrawCurvedSharp`——沿曲线采样的箭身两侧（从起点 0 宽渐变到箭头根部 tailHalf）+ 与直箭头相同的后掠倒钩，按曲线末端切线朝向。
- 擦除空洞跟随变形：`Annotation.ReshapeKeepingGaps(path, change)`——改形状前把每个擦除点记成「沿路径的比例 + 侧向偏移」，改后放回新路径同样位置，
  再存回相对新外框的局部坐标。箭头 / 直线 / 折线的 `MovePointHandle` 都经过它（以前蒙版只相对外框，弯曲后空洞留在框里原处）。
- 悬浮按钮拖动：移动放到 `CompositionTarget.Rendering`（每帧最多一次，在绘制前），松手时取最后位置。
- 文字识别窗口：底栏按两栏排：原文下方状态 + 「贴到屏幕」（原文，居右），译文下方「贴到屏幕」（译文）+ 关闭。

## 8.1.66 0.8.66 变更

- 定位点：`Annotation.PointHandles`（(At, Bend)）/ `MovePointHandle` / `Frameless`。箭头、直线：Start、弯曲点、End，`Bend` = 曲线中点相对弦中点的偏移
  （`Curves.Control` = 弦中点 + 2×Bend，二次贝塞尔）；折线：各顶点 + 每段中点（`Bends` 按段号）；序号：箭头头部（Tail）。
  `AnnotationEditor.HitFrame` 先查定位点（`PointHandleBase` + 序号），Frameless 元素只有定位点、点到线身为移动，没有框 / 旋转；`DrawFrame` / 悬停画圆点。
  贴图 / 编辑器 / 截图的缩放分支在 handle ≥ 100 时改调 `MovePointHandle`。拖点前若有旋转先烘进坐标（`Curves.Bake`）。镜像翻转同时翻转弯曲。
- 弯曲箭头：`DrawCurved`（沿曲线采样的箭身 + 按端点切线方向的箭头；四种样式）。命中按采样折线。
- 复制即缩略图：`ClipboardService.ImageCopied` → `App.NoteScreenshot`（700ms 内只算一次）；按钮隐藏时 `_pendingThumb`，`FadeIn` 时显示。缩略图与按钮同大、圆角。
  去掉了复制时的右下角气泡（Win+鼠标、长截图、录屏截屏）。
- 悬浮按钮：拖动开始收起改 45ms（`DragFoldMs`）。右键菜单「鼠标穿透」（`PinsClickThrough`，切换所有贴图）。
- F：`ShortestWayBack`（≤ 一次翻转 + 三次旋转）代替逐步撤销；`_quietOrientation` 下 `ApplyPinImageTransform` 不合成、不布局，结束时一次。
- 截图翻译条：进度 / 错误 / 重试移到 `TranslateProgress`（选区内右上角），条本身只有圆点、[原文|译文]（未知时显示“原文 / 译文”）、✕，宽度不变。
- 右键取消划选：截图 `CancelGesture` 在 `DragMode.Creating` 时清掉正在划的区域（不退出截图）；`WinMouseSelect` 画框中按右键丢弃，右键与左键抬起都吞掉。
- 设置页切换慢（批注栏页大量图标蒙版每帧重绘）：`Motion.EnterPage` 动画期间 `BitmapCache`，结束后去掉。实测布局只要 0–22ms。
- 离线翻译确认：Engine=Offline 且 API 地址不可达时，原位翻译照常完成（tests `mtprobe`）。翻译包（Hugging Face 上的 opus-mt 模型）基本不会更新，没加更新按钮。

## 8.1.65 0.8.65 变更

- 悬浮按钮拖动闪帧：拖动开始时浮窗收起动画 / 按下缩放动画与窗口移动同时进行（透明窗口边画边移会在路径上留一帧）。现在先收起（`FoldMs` 100ms 内窗口不动，`_holdMoveUntil`），按下缩放直接到位，移动时没有动画。
- 缩略图：`FloatButtonWindow.ShowThumbnail`（`_thumb` 叠在图标上、同尺寸，500ms 后 220ms 向右滑出，`_main` 内容 ClipToBounds）。`App.NoteScreenshot`：截图结束（Resume 之后，隐藏的按钮淡入时一起）、Win+鼠标、长截图、录屏截屏都调用。
- `FloatHideWhileCapturing`（默认 true，设置「截图时隐藏」）：关闭时截图仍先隐藏再取屏，覆盖窗口 ContentRendered 后 `Resume`。
- 悬浮菜单：`PinsClosedAll`（没有打开的贴图且 `_closedAll` 里还有可恢复的）→「打开所有贴图」（`OpenAllPins` 逐个 `RestorePinEntry`）。`CloseAllPins` 记下这次关掉的条目。
- 贴图：右键「关闭原位翻译」（翻译中 / 已翻译）；语言切换窗口加 ✕（`CloseInPlaceTranslation`：回到原图）。截图：`TranslateClose`（关掉原位翻译开关）。
  截图的语言条固定在选区右上方居右（屏幕顶端放不下时放选区内），不再绕工具栏换位置。「清除批注内容」只在有非图片元素时出现。
- F：`ResetPinImageTransform` 先算出翻转/旋转合成（2×2 整数矩阵：镜像 + 角度），`_quietOrientation` 下无动画地逐步撤销，再一次 240ms 从原外观（含缩放）过渡到原始。`ResetMotion` 供测试。
- 编辑器悬停：`AnnotationEditor.HoverElement` → `DrawHoverFrame` 用与 `DrawFrame` 相同的 `SelectionFrame` + 旋转画（无手柄）；悬停在已选中元素上不画。
- 序号旋转：`NumberAnnotation.Draw` 画箭头时反向旋转（箭头不随序号转），`HitTest` 两种坐标都试。
- 录屏：`RecordFrameWindow.PrepareOpen`（显示前不透明度 0，只淡入一次）。工具栏「截屏」（`SnapButton`，与开始同高同内边距，`IconCaptureMask`）→ `RecorderController.Snapshot`：截录屏区域、复制、`Flash`（线条 170ms 白色）、通知按钮缩略图。
- Logo：`tools/colorize_logo.py` 把 `tools/logo_white.ico`（原白色像素图）每个尺寸按对角渐变（青 → 蓝 → 紫 → 粉 → 橙）上色、保留明暗和透明，写 `Assets/markshot.ico`、`markshot_dark.ico`（同一个）、`markshot.png`。

## 8.1.64 0.8.64 变更

- 色块又糊了：工具栏的模糊默认 12 也传给了色块（`BuildCanvas` 非高斯时仍会给色块做盒式模糊）。现在 `Rebuild` / 贴图翻转的 `PatternFor` 只在 `Gaussian` 时传 Blur。
- 高斯：强模糊在缩小的副本上算（factor = radius/3，最多 8 倍），`DrawPattern(..., smooth: true)` 线性放大；sigma ≈ radius/2。
- 柔边：`MosaicBuilder.SoftMask(geometry, feather)` 把形状（填充 + feather 粗描边）画进位图，alpha 三次盒式模糊（sigma = feather/4），作 ImageBrush 不透明蒙版：
  形状内完整，向外平滑淡出（不再是 10 圈台阶，也不再把画笔里面变半透明 → “盖了一层”）。蒙版和画笔轮廓（`_area`）按形状键缓存在元素里。
- 聚焦柔边：`Spotlights.SoftDarkness` 在所有聚焦外接区域（取整）生成暗度位图（每个洞按自己的羽化模糊，多洞取并），区域外用几何画暗；按键缓存。
- 卡顿：`Annotation.EraseClip()` 在元素自身坐标里缓存“去掉擦除部分”的裁剪（按尺寸 + 笔画数），移动只套平移；`DrawItem` 用它（以前每帧 Combine）。
  `Layered` 没有水印 / 聚焦时不再为已烘焙的洞拼几何；`Cutouts.Exclude` 按洞的键缓存。编辑器：`PictureKey` 计入图片的擦除笔画数；按住左键移动时 `FollowMosaics`（70ms 节流）重建马赛克。
- 离线翻译：`Services/OfflineTranslation.cs`。`OfflinePacks`（en-zh / zh-en，Xenova/opus-mt-*，下载 tokenizer.json + encoder/decoder_merged 量化 onnx 到 %LOCALAPPDATA%\Markshot\translate\<id>，
  hf-mirror.com / huggingface.co，.part 后改名）；`OfflineTranslator`（自写 SentencePiece unigram Viterbi，ONNX Runtime 已随 RapidOcrNet 引入；贪心解码 + KV 缓存，en-zh 前缀 >>cmn_Hans<<，
  中文结果去掉汉字旁的空格）；`OfflineProvider`。`TranslationConfig.Engine`（Auto / Api / Offline）、`OfflineMirror`；`TranslationService.UsesOffline`：自动 = 无 Key 且非本机/局域网地址且已有包。
  设置 → 翻译：「离线翻译包（备用）」卡片（翻译方式、下载源、每个包 下载 / 取消 / 删除 + 进度）。测试连接固定测 API。手测：tests 的 `mtprobe`（MARKSHOT_MT_ROOT / MARKSHOT_MT_DOWNLOAD）。
- 贴图语言切换：`PinLanguageSwitch` 移入自己的 `ToolbarHostWindow`，放在图片右上角上方（顶部没地方时放下方），随贴图移动 / 缩放，贴图隐藏时隐藏。
- 悬浮按钮：拖动开始不再松开按压缩放（松手才回弹）。设置图标预览 62。

## 8.1.63 0.8.63 变更

- 马赛克类型：`Gaussian`（高斯模糊）/ 色块。`MosaicBuilder.BuildCanvas(source, block, blur, gaussian)`：高斯时整幅原图三次盒式模糊（`Gaussian`），不再给色块叠模糊。
  `Feather`（柔边）只对高斯生效：`SoftMask(shapeAt, feather)` 边界内外各半。工具栏：马赛克变体后「色块 / 模糊」两个 PlateToggle（`MosaicKind_Click`）；
  色块显示 LevelGroup，模糊显示 BlurGroup（程度 1–40 + 柔边 0–60）。选中元素时先按元素的类型决定显示哪组。
- 截图工具栏：`PositionToolbar` 改为左对齐选区左边，变宽向右伸，只在屏幕右边不够时左移。贴图工具栏：`PlaceToolbar` 按所在屏幕右边界夹住，宿主窗口 SizeChanged 时重新放置。编辑器仍居中。
- 拉过对边镜像：`AnnotationOps.CrossedSides` / `ResizeThrough(…, ref flipped)` / `Mirror(annotation, horizontal)`（点集、线、箭头镜像；图片 TransformedBitmap 翻转 Image/Origin；
  擦除蒙版 ReorientMasks；文字与对称图形不变）。贴图/编辑器（`_resizeFlip`）和截图都用。编辑器调节框：`EditorOverlay.TransformMirrorX/Y`（预览镜像），
  `TransformTo` / `TransformItems(…, mirrorX, mirrorY)` 应用（组内位置也镜像，旋转取反）。
- 抠除贯穿：抬笔时 `Cutouts.Bake(items, cutout)`——下方每个能带蒙版的元素（`Carries`：非 抠除/水印/聚焦）`AddEraseStroke`，`cutout.Baked = true`。
  `Layered`：已烘焙的洞只裁不能带蒙版的元素（水印、聚焦），未烘焙（正在画）的照旧裁下方一切；底图仍由 `Cutouts.Exclude` 裁全部洞。移动/缩放元素，洞跟着走。
- 默认：`Theme = "Light"`、`UiSize = "Medium"`、`UiSizeVersion = 2`（新配置不再走旧尺寸迁移）。已有配置保持原选择。
- 设置：快速双击复制移到 快捷键 → 贴图窗口 卡片。点击行为：按钮行底下一块 `FloatClickPlate`（Canvas 内 28×28），与滑块同步（`KnobAt` 拖动时即时，吸附时同一动画）。
- 悬浮按钮：`Collapse(animate: true)`（110ms 收窄+淡出，拖动开始和鼠标离开用；截图/执行仍即时）；`Press(down)` 按下缩到 0.86。
- 原位翻译：截图里关了再开，缓存译文时 `UpdateTranslationVisuals` 直接把状态条连同语言切换显示出来。贴图：`PinLanguageSwitch`（右上角 [原文|译文]），
  `_untranslatedBase` / `_translatedBase` 都保留，`ShowPinLanguage`；菜单「显示原文 / 显示译文」。截图翻译中贴图：`PinOriginalBase` + `PinLanguages` → `pin.AdoptTranslation`。

## 8.1.62 0.8.62 变更

- 马赛克模糊（Blur ≥ 1）不再 PushClip，而是 `MosaicBuilder.SoftMask`：形状向内 / 向外各扩 0.75×模糊，10 圈逐级透明的 DrawingBrush 作 OpacityMask（矩形用 Inflate，笔刷用 Widen 粗细 ± 2×grow）。
- 贴图抠除后阴影消失：编辑时 `AnnotationEditor` 给 Img 设了按洞裁剪的 Clip，退出编辑后没去掉，Clip 把 DropShadowEffect 也切了。`ExitEditMode` 里 `Editor.ReleaseCutoutTarget()` + `UpdateFrame()`。
- 悬浮按钮默认开启：`FloatButtonEnabled = true` + `FloatButtonChosen`（设置开关 / 托盘 / 菜单关闭时置 true）；`EnsureDefaults` 在未选择过时强制开启（旧配置同样）。
- `FloatClickAction`（默认 Capture）：主按钮单击 `Run(FloatClickAction)`。设置「点击行为」：浮窗按钮行下方等长 Canvas 轨道（每格 28），拖动圆点、松开吸附到最近按钮（隐藏的也可选），对应按钮加强调色底板（`RestPlate`）；跟着按钮走，排序后照旧；计入指纹，保存写入。
- 浮窗里长按排序 → `FloatButtonWindow.OrderChanged` → `SettingsWindow.SyncFloatOrder()`：按新顺序重排瓷砖并用 TranslateTransform 滑动；原本无未保存修改时更新 `_savedPrint`（已由浮窗保存）。
- `Run("TogglePins")` 不收起浮窗。显示贴图开关用 `PinWindow.FadeHide` / `FadeShow`（窗口 Opacity 动画，结束后 Hide）。

## 8.1.61 0.8.61 变更

- 设置「未保存」误报：滚动条（RangeBase.ValueChanged）也算了修改。现在 `HasUnsavedChanges` = 当前表单指纹（`Fingerprint`：带名字的文本框 / 密码 / 开关 / 滑条、
  工具栏行的顺序与隐藏、阴影色、主题、Win+鼠标、悬浮按钮的即时项）与打开 / 上次保存时不同；改了再改回也不算。
- 设置：咖啡图标 24 DIP；二维码拉出后点窗口别处收回（`PutDrawerAwayOnOutsideClick`）。更新窗口固定宽 360，立即更新点后变灰。
- 聚焦暗度：一张图一个暗度，滑条通过 `SpotlightDarknessChanged` → `AnnotationOps.SetSpotlightDarkness` 改所有聚焦（不需选中，可撤销）；选聚焦工具时滑条显示图上现有的暗度。
  以前组的暗度取最大值，又只改新画的 / 选中的，所以调低没效果。
- 马赛克「模糊」0–30：`Blur` 属性，`MosaicBuilder.BuildCanvas(source, block, blur)` 把色块放大到原尺寸后三次盒式模糊（≈高斯）。
- 抠除：以前整块画布按所有洞裁剪，之后画的元素也被挖掉。现在底图（截图 / 贴图图片 / 编辑器背景）按所有洞裁剪，
  元素只被它上面（之后画的）洞裁剪：`AnnotationEditor.Layered` / `DrawLayers`（OnRender、贴图合成、截图合成共用）。
- 编辑器：元素样式行增减的过渡以前不动——第二次 UpdateStylePanel 清掉了动画，且 Measure 拿到旧高度。现在行没变化就不碰动画，新高度用 UpdateLayout 取得；出现的行淡入。
  标题后显示版本号。
- 悬浮按钮「显示贴图」`TogglePins`（图标库「显示图片.png」→ `show_pins.png`）：默认在 FloatActionHidden 里（旧配置迁移时也加进隐藏）；
  `App.TogglePinsShown` 隐藏 / 显示所有可见贴图，新贴图出现时全部恢复；全部隐藏时按钮 0.4 透明。
- 长截图：进入时用 `FinishWithTransition`，从选区向外变亮后再开始。

## 8.1.60 0.8.60 变更

- 聚焦 `SpotlightAnnotation`（`AnnotationTool.Spotlight`，序号按钮的第二种样式，VariantFamily 7，Tab 互换）：Rect / Ellipse / Darkness / Feather，无描边无颜色。
  `Spotlights.Prepare(items)`（在 `AnnotationEditor.DrawOrder` 里调用；画预览时把 Preview 也并进来）把同一画布的聚焦分成一组，第一个画全部：
  一个超大矩形减去所有聚焦形状的并集，羽化分 12 级从形状内缩 f/2 到外扩 f/2 逐级加深（只在边缘）；绘制范围由调用方的裁剪限定（截图里是选区，贴图 / 编辑器是图片）。
  选择按边缘（`HitContent`），调整框 / 旋转 / Alt / Shift 同图形。工具栏：`NumberVariants`、`SpotlightGroup`（矩形 / 椭圆、暗度 10–95 %、羽化 0–80）。
- 工具栏：马赛克样式改为两个图标；`NumberStyleGroup` 移到大小滑条左边；抠除图标 = 图标库「蒙版.png」；序号按钮加 ▾ / 悬停列表。
- 截图区外光标闪：`UpdateAnnotationHover` 末尾总设工具光标，随后 `UpdateCursor` 设箭头。现在区外直接箭头。
- 编辑器：元素样式针对所有选中元素（`StyledElements`）：透明度（Annotation.Opacity）、圆角（图片 / 矩形）、阴影 / 描边 / 描边色（仅图片；选了形状变灰）；
  行的出现 / 隐藏用 `AnimateSectionHeight` 过渡。调色「重置」在 `_adjust.IsNeutral` 时变灰，「重置所有修改」在没有撤销记录时变灰。
  贴到屏幕：整张合成图贴出，不带批注和撤销记录。

## 8.1.59 0.8.59 变更

- Alt / Shift（同 Photoshop）：截图选区 `CreatingRect(start, end, shift, alt)`，按键变化立即刷新（`RefreshCreating`）；画选区或画图形时 Alt 不算辅助线单击（`CaptureKeyDown`）。
  批注：`CreationGesture.Move(..., alt)`，`_centered` 时矩形 / 椭圆 / 马赛克框以按下点为中心，直线 / 箭头向两边长；贴图里画图时 Alt 不出辅助线、不进菜单模式。Win + 鼠标划区域没有加。
- 选择模式按内容：`Annotation.HitContent`（空心矩形 / 椭圆只算线条，图片按 alpha，其余同 HitTest）；`AnnotationOps.HitTest(byContent)`，`HitAnnotation` 在 None / Select 时用它，拿着工具时仍是原判定。
- 长截图：`ScrollStitcher.Outcome`（First / Same / Placed / TooFar / Animating / Gap / Full）+ `PlacedFrames` / `LostFrames`；`ChangedShare` ≥ 50 % 且没有位移 = 滚太快，否则 = 局部动画；
  页面没动的小变化（光标闪烁）算 Same。`ReportState` 每帧更新预览窗；跟丢后停下来，提示一直留到重新对上。
- 编辑器：`Section` 标题左侧折叠按钮（`EditorFoldedSections` 记标题，默认全展开），card.Tag = 内容；元素样式常驻（没选图片时内容禁用变淡），删掉 SlideSection；顺序：画布与尺寸 / 裁切与变形 / 旋转与镜像 / 元素样式 / 调色 / 抠色。
- 其它：选区外光标 = 箭头；有列表的工具按钮不显示 ToolTip（`_listToolTips`，关掉隐藏收纳时恢复）；`Motion.Bar` 70 ms；关闭贴图时批注栏 PopOut；水印去掉透明度滑条（Ctrl+滚轮仍可调，低于 100 % 显示在颜色旁）；
  悬浮按钮右键加「禁用所有快捷键」，托盘加「添加贴图…」；贴图历史右键「编辑图片」（`EditPinEntry`，本次运行关闭的带批注）；咖啡图标下移，提示文字垂直居中。

## 8.1.58 0.8.58 变更

- 设置：版本号悬停弹出 `ReleaseNotes.All` 前三版（Popup，移开 180 ms 后关，可移到卡片上）；「恢复默认设置」DockPanel 靠左；咖啡图标 32 DIP。
- ` 键：`HotkeyGesture.GetKeyName` 把 `Key.Oem3` 写成 "`"，`TryParseKey` 读 "`" / "~"；工具 / 贴图单键用 `ToolHotkeys.Name`（数字也按字符比较）。
- 悬浮菜单：按钮用批注栏的 `IconToolButton`，底板同批注栏（F21E1E1E、圆角 9、内侧窄阴影、Padding 5,3）。
  改菜单按钮时左右闪：可见按钮数变化 → 窗口 SizeToContent 变宽，下一帧才 Place。现在底板放在固定宽度的 `_plateHost`（所有按钮的宽度）里，窗口大小不变；`ApplyLook` 同步 Place、不再每次重排。
- 录屏工具栏：底色 / 圆角 9 / 无描边 / 内侧阴影 / 图标 18 DIP，与批注栏一致；窗口圆角区域 18 px 直径。
  `Reveal` 让录制行 / 预览行 / 设置组 / 暂停 / 提示出现时 PopIn（100 ms），打开 PopIn、关闭 PopOut 后再 Close。
  尺寸菜单加「全屏」（`SetFullScreen`：区域所在显示器整屏）。
  选预设后拖框尺寸跳回：尺寸框有键盘焦点时选预设，之后拖框让尺寸框失去焦点 → `ApplyTypedSize` 把框里的旧数字又应用了。现在只有真的输入过（`_sizeTyped`）才应用，选预设时清掉焦点。

## 8.1.57 0.8.57 变更

- 连续按 F1 时右下角报错（诊断日志：`SettingsWindow.Cancel_Click` → `InvalidOperationException` 在窗口关闭期间调用 Close）：
  设置窗口开着时，截图里的 Esc（或按住不放的 Esc）落到设置窗口上，「关闭」按钮是 IsCancel，Esc = 关闭；有未保存修改时关闭会弹询问，
  询问期间又一个 Esc 再次 Close → 异常。现在：关闭按钮不再 IsCancel，Esc 由 `OnEscape` 处理（重复按键、窗口刚激活 400 ms 内、关闭中都忽略）；
  `OnClosingAsk` 防重入（`_closingNow`），`Cancel_Click` 关闭中直接返回。
- 设置底部「恢复默认设置」：确认后用 `new AppConfig()` 的值覆盖所有属性（保留翻译 Key / 地址 / 模型、LastVersion、最近区域），立即保存应用，
  关闭窗口后 App 重新打开（`ResetDone`）显示默认值。
- 悬浮按钮：浮窗加 `AddPin`（添加贴图，图标库「添加图片.png」→ `add_image.png`，`App.PinFromFiles` 多选，动图照常播放）；
  右键菜单前三项 添加贴图… / 粘贴剪贴板（`PinFromClipboard`）/ 关闭所有贴图（`CloseAllPins`）。

## 8.1.56 0.8.56 变更

- 悬浮按钮点击截图后 Esc 偶尔无效：按钮窗口不激活，Markshot 不是最后收到输入的程序时 Windows 拒绝 SetForegroundWindow，键盘留在原程序。
  `Native.ForceForeground`：先 SendInput 一个无意义键 0xE8 让本进程成为最后输入者，不行再 AttachThreadInput；截图窗口 Loaded 用它，ApplicationIdle 时没激活再补一次。
- 悬浮按钮拖动闪一帧：用 SetWindowPos 移动透明窗口后 WPF 下一帧还按旧 Left/Top 画了一次。改为设置 Left/Top（物理像素 / DPI）。
  浮窗按钮行加 `LayoutTransform = UiScale.Transform`，和批注栏同大，跟随「工具栏大小」。
- 动图贴图：`IsMoving` 时不进入批注（提示一下）；旋转 / 翻转时 `AnimatedImage.Transformed` 把每一帧一起变换，`UpdateAnimation` 重新开始播放。
- 贴图快速点击选中元素时跳动：贴图拖动的松开没收到（快速双击、捕获丢失）时 `_dragging` 还在，之后鼠标移动就从旧按下点拖动贴图。
  现在移动时左键已松开就结束拖动（`EndStaleDrag`），按下 4 px 内不动贴图，点中元素时也结束残留的拖动，LostMouseCapture 收尾。
- 正在输入的文字拖调节框跳动：框随字号变大时 TextBox 先滚动了内容（宽度下一帧才更新）。拖框时 `Refresh` 里立即 UpdateLayout 并把滚动归零。
- Z / Shift+Z：截图（`CaptureKeyDown` 之后的按键处理）和贴图批注里撤销 / 重做；Ctrl+Shift+Z 以前被 Ctrl+Z 抢先当成撤销，现在是重做。
- 贴图历史：`UniformGrid` 按宽度算列数，瓷砖拉伸填满；窗口大小存 `PinHistoryWidth/Height`。
- 录屏工具栏显示不全：圆角区域（SetWindowRgn）在 SizeChanged 时取 GetWindowRect，窗口刚变大时还是旧尺寸，新的部分被裁掉。改为 WM_SIZE 钩子用真实尺寸重做区域。
- 编辑器「清空图片」：来源贴图直接关闭（进贴图历史），不再贴回屏幕。
- 设置：不再即时保存。窗口里任何修改（文本、勾选、滑条、密码、工具栏按钮开关 / 排序、阴影颜色、悬浮按钮图标）标记未保存；
  关闭时问 保存 / 不保存 / 取消。主题、工具栏大小、悬浮按钮各项仍即时预览（写在 App.Config 上），不保存时 `DiscardLive` 按打开时的值还原（`LiveSettings` 列表），
  主题由 App 的 Closed 处理还原。悬浮按钮换图不再立即删旧图，保存后 / 启动时 `FloatIcon.CleanUp` 删除不用的 `float_icon_*`；不保存时删掉新拷贝。
  测试：`SettingsWindow.AskAll`（Program.cs 设为「不保存」），单个窗口可用 `AskOverride`。
- 稳定性（全面检查后改的）：
  - 关闭的图片编辑器被 `UiScale.Changed` 的 lambda 一直引用（连同图片和撤销记录）：存成字段，Closed 里移除。
  - 光标缓存无上限（每个尺寸 / 颜色 / 序号一个 Windows 光标句柄，还写一个 .cur 文件）：LRU 96 个，加载后删除 .cur。
  - 关闭的贴图：历史条目存盘后不再持有整图（`PinEntry.LoadImage` 每次读文件，`Thumbnail()` 320 px 缓存给历史窗口）；
    精确恢复用的 `_closedPins` 另限总大小约 800 MB。撤销记录每个窗口上限 1.5 GB → 500 MB。
  - `TaskScheduler.UnobservedTaskException` 记日志；`AtomicFile`（先写 .tmp 再替换）写 config.json 和贴图历史索引。
  - 录屏结束（StopAsync）异常会回到设置状态并提示，不再卡在「处理中」；保存 / 复制录像的 IO 错误弹提示。
  - 贴图关闭时停止原位翻译和文字层识别；「打开方式」管道监听任何异常后继续监听；OCR 在调用线程冻结图片；
    长截图预览失败不再每 100 ms 重复；鼠标热键钩子线程只摘自己的钩子；原位翻译缓存超过 5000 条清空；悬浮按钮换图的 IO 错误弹提示。

## 8.1.55 0.8.55 变更

- 悬浮按钮：`FloatIconSize`（1～2，设置里 100 %～200 % 滑条，`MainSize = BaseSize × 它`）；浮窗加 `History`（贴图历史，图标库「历史.png」→ `history.png`）。
  - 开关时左右闪：窗口第一次显示时按默认一侧排好，`Place` 再换到另一侧——现在根 Grid 透明度 0，放好位置后（Render 优先级）淡入。
  - 截图：`Suspend` 先 80 ms 淡出并用 `DispatcherFrame` 等它结束（最多 150 ms），再 Hide + DwmFlush；`Resume` 淡入（110 ms）。全屏隐藏也走淡出。
  - 点按钮截图后菜单闪出：回来时鼠标还在按钮上，MouseEnter 立即展开。`Resume` 时鼠标在窗口里就 `_hoverBlocked`，鼠标离开后才解除。
  - 拖入：文件走 `PinFileRequested(file, index)` → `App.PinFile`：动图（`AnimatedImage.Load(maxSide 2400)` 帧数 > 1）带 `Animation` 贴出，其余 `ImageFile.Load`；
    都在悬浮按钮所在屏幕中央（`PinCentered`，过大缩小，多张错开 24 px）。
- 动图贴图：`PinWindow.Animation`，`UpdateAnimation()` 在 Loaded / 进出批注 / 旋转翻转后调用：未批注、没画东西、没旋转时用 `ObjectAnimationUsingKeyFrames` 播放 Img.Source。
  复制 / 保存仍是第一帧。`AnimatedImage` 解码限制总内存约 600 MB（超了按比例缩小），只保留被后续帧依赖的解码帧。
  有透明角的图片（剪贴板贴图、拖入）`ShapedShadow`：阴影沿轮廓（DropShadowEffect 在 Img 上），不会再有蓝色底板。
- 设置：悬浮按钮卡片移到「贴图阴影」下面；「界面大小」改名「工具栏大小」。
- 编辑器：底部「重置所有修改 | 清空图片」（`ClearDocument`：未保存先问，回到空编辑器）；画布尺寸「锁定比例 / 移动吸附」（`EditorSnap`，默认开，关掉 `Snap` 直接返回）；
  删掉双击图片 / 元素打开调节框（选中就有调节框）；浅色主题标题栏用 `markshot_dark.ico`（`Theme.TrackTitleBar` 跟主题换 `Theme.Logo()`）。
- 批注：
  - 选中元素后换工具，元素大小被改：`SyncVariant` 结尾把 `_configuring` 置 false，`ConfigureForTool` 随后设滑条值时触发了 SettingsChanged → 套到选中元素上。现在恢复原值。
    `RememberSize` 只在没有选中元素时记工具大小（选中元素不再改工具的大小）。
  - 选中元素时右键：取消选中并放下工具（截图 `_rightReset` 分支；贴图 `RightClickDeselects`；编辑器在选中元素 / 多选上右键仍是层级菜单）。
  - 文字工具：空白处单击只取消选中，双击才新建（快速双击复制在文字工具下不生效）。
  - 拖动刚画的元素跳开：同类工具点中元素开始移动时 `_pressPoint` 还是画它时的按下点，`MoveSelectedTo` 按它算偏移；现在每次按下都更新。
  - 旋转把手改为框的蓝色（`Accent`）。

## 8.1.54 0.8.54 变更

- 图片拖到悬浮按钮上 = 贴图（见 8.1.53 悬浮按钮一节），`ReleaseNotes` 加 0.8.54。

## 8.1.53 0.8.53 变更

- 通知：`TrayService.ShowBalloon` 不再用 Windows 气泡（大卡片 + 提示音），改为 `UI/Toast.cs`：右下角（鼠标所在屏幕的工作区）一个小气泡，
  主题色、`WS_EX_NOACTIVATE`、一次只一个（新的替换旧的），约 2～4.5 s 后自己收起，鼠标停在上面时不走，单击收起。
- 设置不丢：
  - `AppConfig.Load` 整体读不了时（某一项类型不对）不再回到全部默认值（下次保存会覆盖整个文件）：原文件另存 `config.broken-时间.json`，
    再 `Salvage` 逐项读取，读不了的那一项用默认值。
  - `LastVersion`：新版本第一次启动时把旧配置复制成 `config.before-update.json`，记下版本；不是首次安装时弹 `UI/WhatsNewWindow`（`ReleaseNotes.All` 里每版几句话，
    从上次的版本之后的都列出；旧版没有 LastVersion 时只列当前版）。点窗外（也轮询全局鼠标，后台启动拿不到焦点时也能关）/ Esc 关闭。
  - 主题、界面大小选了就保存（以前要点保存，关窗口或更新后就回去了——用户说的“升级后主题色被重置”多半是这个）；设置里点「更新」先执行一次保存。
  - 测试：`AppConfig.PathOverride` 指向测试自己的临时 config（`tests/Markshot.Tests/Program.cs`），测试再也碰不到用户的配置。
- 悬浮按钮 `UI/FloatButtonWindow.cs`（`FloatButtonEnabled` 等配置项，App 的 `ApplyFloatButton / RefreshFloatLook / SetFloatButton / FloatAction`）：
  - 窗口大小固定（行的位置用 `Visibility.Hidden` 占着），展开不挪窗口；行开在屏幕中间那一侧（按钮在屏幕右半边就往左开）；展开 90 ms 横向拉开，离开立即隐藏；
    展开时整行背景 #01 让间隙也算在里面。
  - 拖动：自己算（物理像素 + `SetWindowPos`），松手 `Inside` 夹回所在屏幕工作区并记住中心点（`FloatButtonX/Y`）；显示器变化 / 启动时也夹回。
  - 单击 = 截图（`Run("Capture")`）；行按钮 `ReorderBehavior` 长按排序（存 `FloatActionOrder`）；右键菜单 设置…（`ShowSettings("Float")` 滚到卡片）/ 全屏时自动隐藏 / 关闭。
  - 截图前 `Suspend` 隐藏并 `DwmFlush` 两次再截屏：透明（layered）窗口不能用 `WDA_EXCLUDEFROMCAPTURE`（实测 SetWindowDisplayAffinity 返回错误 8）。
  - 全屏隐藏：700 ms 查前台窗口是否盖满按钮所在显示器（排除自己进程、桌面、任务栏）。
  - 阴影 = 贴图阴影设置（`DropShadowEffect`，透明度乘图标透明度）。
  - 文字识别：`StartCapture(ocr: true)` → `OverlayWindow.OcrOnSelect`，选好区域（松开左键后）直接 `OcrAndClose`。
  - 图标：`Core/AnimatedImage.cs`（SkiaSharp `SKCodec` 逐帧解码 GIF / WebP / APNG，按帧时长用 `ObjectAnimationUsingKeyFrames` 播放；0 / 10 ms 帧按 100 ms），
    `FloatIcon` 把选的图片复制到配置目录（`float_icon_时间.扩展名`），粘贴的存 PNG；托盘图标用第一帧（`TrayService.SetIcon`）。
  - 设置 → 样式「悬浮按钮」卡片：开关、全屏隐藏、浮窗按钮行（`FillToolbarRow` 复用，现在返回 ReorderBehavior）、预览框（拖入 / Ctrl+V / 选择图片 / 重置）、透明度；都是改了就保存。
  - 图片拖到悬浮按钮上 = 贴图（`PinRequested` → `CreatePinAtCursor`，`DroppedImages` 读文件 / 位图）。
  - 托盘菜单：「悬浮按钮」（勾选）和开着时的「悬浮按钮回中」（鼠标所在屏幕中心）。
- 批注：
  - 选中一个元素时换到别的工具（不同类），工具栏先丢掉选中再配置，工具用自己的大小（以前选中序号再点画笔，画笔拿了序号的大小）。
  - 拿着工具右键：截图里第一次右键 `SetTool(Select)`，贴图 / 编辑器里 `Toolbar.DeactivateTools()`；再右键才是 Esc / 右键菜单。
  - 清空批注后序号计数回 1（贴图 `_number`，截图本来就有），序号光标同时变回 1。
- 图标：录屏 = 图标库「录屏.png」（`screen_record.png`），文字识别 = 「识别文字.png」（`ocr.png`，原来用路径画的大一圈），检查更新按钮 = 「刷新.png」，
  浮窗的截屏 = 「截屏.png」（`capture.png`）；都裁边后缩到 200 px 画布的 162 px，和其它图标一样大。
- 设置导航：「样式」排第一并默认打开。

## 8.1.52 0.8.52 变更

- 鼠标在工具栏上点击后被「定住」：0.8.48 起 Win + 鼠标的低级鼠标钩子装在 UI 线程上，系统里每一次鼠标移动都要等 UI 线程处理钩子，
  UI 线程忙（副工具栏展开的逐帧布局）时指针就停住。现在 `WinMouseSelect` 的鼠标 / 键盘钩子都在自己的线程上（自带消息循环；键盘钩子用线程消息 `WM_USER+1/+2` 装卸）。
- Win + 鼠标：划区域时（可已松开 Win）按住空格 = 整个区域跟着鼠标移动，松开空格后继续从当前位置拉大小（键盘钩子在划区域期间常驻，空格被吞掉不传给前台程序）。
- 剪贴板「被其他程序占用」：WPF 的 `Clipboard.SetDataObject(data, true)` 走 OLE，`OleFlushClipboard` 要把位图转成各种格式并一直开着剪贴板，
  剪贴板监听程序（剪贴板管理器、聊天软件，每次复制都会去读）这时打开就会冲突。现在先在内存里准备好 CF_DIBV5（带 alpha，自上而下）和 PNG（以及「复制为文件」的 CF_HDROP），
  再 `OpenClipboard` → `EmptyClipboard` → `SetClipboardData` → `CloseClipboard`，只开几毫秒；打不开时 15～40 ms 间隔重试 40 次（约 1.3 s），失败时日志记下占用的程序名。
- 任务栏时间区域闪一下：截图窗口铺满屏幕，Explorer 把它当成全屏程序，把任务栏降到后面，截图关闭时再恢复并重绘通知区。截图窗口、录屏遮罩、贴图辅助线窗口设置 `NonRudeHWND` 窗口属性（`Native.NotFullscreenApp`）退出这种判断。
- 序号：拖出箭头期间一直隐藏光标（以前鼠标移动处理会先把隐藏的光标恢复成序号光标，再被隐藏，于是闪烁）；箭头起点离开徽章 0.3 倍半径（至少 3）。
- 选中框：和悬停预览同样的 3 DIP 实线，只多出调节点和旋转把手（原来是 1.4 DIP 双色虚线）。
- 工具栏：去掉 1 px 描边，改为沿圆角内侧的一圈窄阴影（3 DIP 的模糊线，按圆角裁掉外侧一半）；按钮悬停亮起 60 → 30 ms；工具栏出现 / 收起 160 → 100 ms（`Motion.Bar`），副工具栏展开 240 → 150 ms；
  正在使用的工具（其副工具栏已展开）悬停时不再弹出样式列表。
- 长截图图标改为方形（竖向 / 横向各一，自绘）。

## 8.1.51 0.8.51 变更

- 便携版（单文件发布）里 `zxingcpp-native.dll` 被打进 exe，`CodeReader` 的解析器在 exe 旁找不到它，会静默退回 ZXing.Net。现在它 `ExcludeFromSingleFile`，和 `models`、`ffmpeg` 一样放在 exe 旁边。
- THIRD-PARTY-NOTICES 补上 zxing-cpp 与 ZXing.Net（Apache-2.0）。

## 8.1.50 0.8.50 变更

- 自定义颜色窗口在贴图里一打开就消失：0.8.49 用 `Deactivated` 关闭，而贴图打开它之后会把键盘焦点拿回去。改为轮询鼠标键（`GetAsyncKeyState`，30 ms）：
  有新的按下且指针不在窗口内（且没在屏幕取色、没有子窗口）才按取消关闭；打开它的那次按下不算。测试用 `SimulateClick`。
- 序号拖出箭头时隐藏光标（`Cursors.None`，箭头头部就是指针位置；松开后照常恢复工具光标）。
- 贴图半透明时阴影变粗：0.8.48 的「外圈」做法用 BorderThickness 加宽了。改为阴影层外包 `ShadowHost`，半透明时给 host 一个「大矩形减去图片（含圆角）」的 Clip——阴影大小不变，只是不在图片下方；
  模糊结果被父级裁剪，边缘完整。`ImageHost.SizeChanged` 时重算。阴影透明度仍乘以贴图透明度。
- 二维码：
  - 识别换成 zxing-cpp（NuGet `ZXingCpp` 0.5.3，原生 `runtimes/win-x64/native/ZXing.dll`）：多码、格式全、比 ZXing.Net 快 5～50 倍（实测 6 码 1200×800 约 10 ms，4K 约 0.5 s 含首次加载）。
    再用 ZXing.Net 补漏（zxing-cpp 读不出的低对比度码），画面不超过 250 万像素时两者都跑，按文字 + 位置重叠去重。原生库缺失时自动只用 ZXing.Net。
    格式名：zxing-cpp 是 "QR Code"（带空格），`KindOf` 去掉空格 / 下划线再比较。码的位置直接取四个角点。
  - 原生 `ZXing.dll` 与 ZXing.Net 的 `zxing.dll` 文件名只差大小写，发布时冲突：`ZXingCpp` 包 `ExcludeAssets="native"`，原生库以 `zxingcpp-native.dll` 复制到输出 / 发布目录，
    `CodeReader` 静态构造里 `NativeLibrary.SetDllImportResolver` 把 "ZXing" 指向它。测试 `round48` 直接调 `ReadNative` 确认原生库确实在用。
  - 截图打开时 `CodeReader.WarmUp` 预加载原生库；区域停稳 120 ms（原 250）后读；结果回来时区域已变则丢弃（以前可能把旧结果当成新区域的）。
  - 定位只画四个 L 形角标（`CornerMarks`，带一点阴影）；信息卡紧贴角标；离开码 0.4 s 后才隐藏（`_codeHideTimer`），途中进入卡片则保留，离开卡片再隐藏。码的判定范围外扩 6 像素。

## 8.1.49 0.8.49 变更

- 序号拖出箭头时光标换成普通十字（徽章光标会挡住箭头头部），截图和贴图两处。
- Win + 鼠标弹开始菜单：只在按下时补一个空键不够（开始菜单看的是 Win 按下到松开之间有没有别的键，鼠标不算，且 `keybd_event` 发出的键排在当前事件之后）。
  现在按下鼠标时装一个低级键盘钩子，拦下真正的 Win 松开，先发 0xE8 按下 / 松开再重发 Win 松开（与 AutoHotkey 的 Win 掩码同理），然后卸下钩子。
- 自定义颜色窗口：`Deactivated` 时（没有在屏幕取色、没有自己的子窗口）按取消关闭。
- 贴图阴影透明度乘以贴图透明度（之前只把半透明时改成外圈，不变淡）。
- 截图区域内的二维码：`OverlayWindow.Codes.cs`——区域停止变化 250 ms 后在后台读 `_screenPlain` 的这块（`CodeReader.Read`，每个码带 `Area`：QR 由三个定位点中心外扩 0.27 倍跨度，条形码按扫描线给高度），
  鼠标（无工具、无选中元素）移到码上：`UiLayer` 里一个蓝框 + 旁边的信息卡（种类、内容；链接是 `Hyperlink`，点击用浏览器打开并结束截图；「复制」按钮），移开即隐藏，鼠标在卡片上时保留。拖动选区时隐藏、清空选区时清除。

## 8.1.48 0.8.48 变更

- 界面大小三档：小（原「中」，设计尺寸，默认）/ 中（原「大」，1.15）/ 大（1.3）；`AppConfig.UiSizeVersion` 一次性迁移旧值。设置窗口固定用 1.15（`UiScale.SettingsTransform`，`AttachWindow` 对 `SettingsWindow` 特殊处理），切换大小时不跳。
- 悬停样式列表：点击该工具按钮时关闭（`_hoverSuppressed`，鼠标离开前不再弹出），不再盖住设置栏。
- 光标：圆角控制点在 `UpdateCursor` 里先于「选区内 = 移动」判断；选中元素的调整框（手柄 / 旋转）在拿着任何工具时都给对应箭头；
  `UpdateAnnotationHover`（截图 / 贴图）先看正在输入的文字框和选中元素的框，不再被工具光标盖掉。
- 悬停亮起：App.xaml 里所有按钮的 `Hl` 进入动画 160 → 60 ms（离开仍 160 ms）。
- `PlateToggle`（淡底色的开关按钮）：填充、加粗、倾斜、背景、水印开启、序号空心 / 描边、字体按钮。新增主题色键时要同步 `Core/ThemeKeys.g.cs`（本次补了 `T_1AFFFFFF`，已按所有 XAML 重新生成）。
- 颜色色块改为 `Ellipse`（不贴像素，原来是带圆角的 Border 吸附到设备像素，在缩放屏上像锯齿）；自定义颜色按钮、描边色圆、滑块圆钮关闭像素吸附。
- 水印图标换成 ui-icons 的 水印（水滴）。
- 滚动播放 `Core/Marquee.cs`：单行文字放不下时用 `TextEffect` 平移（停 1.6 s → 约 40 DIP/s 滚到尾 → 停 → 滚回），放得下时还原截断方式；用于设置里的状态 / 测试结果 / 配置路径、截图翻译状态、贴图状态、编辑器状态栏。
  注意 TextBlock 的 `ActualWidth` 在被截断时仍是整行宽，可见宽度要取布局槽（`LayoutInformation.GetLayoutSlot`）。
- 序号：`NumberAnnotation.Hollow / Outlined / Tail`，数字加大（1 位 1.42×半径、2 位 1.2×、3 位 0.95×），按数字实际墨迹居中；放置时按住拖动 → `CreationGesture` 固定圆心、`Tail` 跟随鼠标画箭头。
  光标 `ToolCursors.Get(…, number)` = 下一个序号徽章（80% 不透明，实际大小 × 缩放），放下后立即换成下一个号。配置 `NumberHollow / NumberOutlined`。
- 字体：`TextAnnotation.FontName`（`FamilyOf` 缓存），`Core/FontLibrary`（系统字体，中文名优先排序），工具栏「字体」按钮弹出 `FontList` 列表（每项用自身字体显示，虚拟化）；`AppConfig.TextFont`。
- 贴图：透明度 < 1 时阴影改为图片外的一圈（`BorderThickness` + 负边距），不再从图片里透出蓝色底；Shift 多选不显示虚线框；`Delete` = `DeleteWithGroup`（`SkipHistory`，不进历史）；
  「关闭所有贴图」常驻右键菜单，全局快捷键 `CloseAllPinsHotkey`（默认 Shift+Esc）。
- 禁用所有快捷键：`HotkeysDisabled`（设置 + 托盘菜单 `ToggleHotkeys`），`ApplyHotkeys` 直接注销所有热键并停止 Win + 鼠标。
- Win + 鼠标：`UI/WinMouseSelect`（低级鼠标钩子）——Win 按住时左 / 中 / 右键按下被吞掉并开始划区域，只显示一个点击穿透的框窗口（按像素 `SetWindowPos`），松开后 40 ms 截取 `ScreenCapture.CaptureRegion`；
  按下时发送未分配的虚拟键 0xE8 防止松开 Win 弹出开始菜单。动作 `WinMouseLeft / Middle / Right` = Ocr / Pin / Copy / None（设置里每个键一组分段按钮，可重复）。
- 二维码 / 条形码：ZXing.Net 0.16.11（`Core/CodeReader`，多码、自动旋转、反色、小图放大一倍再试），文字识别窗口左栏顶部列出结果，链接是可点击的 `Hyperlink`（浏览器打开），每条可复制。
- 测试：新套件 `round48`。

## 8.1.47 0.8.47 变更

- 新建画布：`NewCanvasDialog`（宽高、常用尺寸 1920×1080 / 1280×720 / 1080×1080 / A4 竖、背景 透明 / 白 / 黑 / 自选，默认取当前画布尺寸和背景）；
  `ImageEditorWindow.NewCanvas` 先 `ConfirmDiscard`，再按 `LoadImage` 的方式重置，但画布上没有图片元素（只有背景位图）。编辑器工具栏新动作 `New`（`IconNewMask` = ui-icons 新建，默认排第一）、Ctrl+N。
- 抠色预览：「抠除颜色」「替换为」不再直接修改，而是 `StartKeyPreview`——图片临时显示结果（大于 1600 px 时在缩小副本上算），容差滑条、两个颜色改动经 30 ms 防抖实时重算；
  画布下方 `_keyBar`（应用 / 取消，Enter / Esc）。应用时在原图全尺寸上算并作为一步撤销；取消、撤销 / 重做（`PinWindow.BeforeHistory` 钩子）、其他图片操作（`GeometryStep`）、保存 / 复制（`ComposeEditedImage`）、打开新图都会先结束预览并还原。
  预览中吸取颜色取原图（`Proxy`）的像素。吸取时 `_stage.ForceCursor` 让十字光标盖过图片和批注自己的光标。
- 测试：录屏计时检查的墙钟改在开始录制之前起算（慢的 CI 机器上录制时钟会超前）；抠色测试的编辑器用 `CloseWithoutAsking`（保存提示在 CI 上卡到超时）；
  `tests/run.ps1` 在 CI 上对超时和无输出的非零退出也写出原因（最后几行）。

## 8.1.46 0.8.46 变更

- 截图滚轮圆角每格 6 DIP（原 2）。
- 悬停样式列表：鼠标进入工具按钮立即打开（无延迟），离开按钮和列表 50 ms 后关闭（只为从按钮移进列表）；列表是 `StackPanel` 里的 `ToolButton`（文字居中，当前样式蓝色加粗），
  `Popup` 用 `PlacementMode.Custom` 居中在按钮下方，卡片四周 8 DIP 阴影边距（上边距与按钮重叠，列表紧贴按钮）。长截图按钮也纳入（▾ 同样隐藏，悬停显示竖向 / 横向）。
- 打开图片图标 `folder_open.png` 线条按 5 px 膨胀加粗（原线条细，小尺寸显得暗）；`IconMask` 统一 `BitmapScalingMode.HighQuality`（大图标缩到 18 DIP 时线条更干净）。
- 贴图闪一下：非截图来的贴图在 `SourceInitialized` 先设 `Opacity = 0`（第一帧在 Loaded 之前绘制，以前会先满不透明画一帧再从 0 淡入）；
  恢复 / 编辑器贴回的缩放与透明度经 `CreatePin(view:)` 在显示前设置（以前显示后再 `RestoreView`，先按 100% 出现再跳到原大小）。
- 编辑器无图片时右侧面板仍可滚动：只禁用面板内容（`_panelStack`）和底部「重置所有修改」，不再禁用整个面板（连滚动条一起禁用了）。
- 抠色与替换颜色：`Core/ColorOps.cs`（`Weight` 加权 RGB 距离，容差内为 1，之后 4 + 容差/2 的范围线性衰减；`Key` 乘透明度；`Replace` 在 HSL 里按 源→目标 的差值平移色相、饱和度、亮度，按权重混合，灰色源直接取目标色相）。
  面板新分区：颜色色块（选色）+「吸取」（`_pickingKey`，在画布上单击取 `EyedropSource` 颜色，Esc 取消）、容差滑条、「抠除颜色」「替换为」+ 目标色块；作用于选中图片（`Target`），`GeometryStep(EditGroup.Colour)` 一步撤销，结果成为新的调色起点。
- 录屏工具栏圆角：DWM 圆角只在 Windows 11 生效，Windows 10 用 `SetWindowRgn(CreateRoundRectRgn)` 裁成 8 DIP 圆角（不用透明窗口：分层窗口不能设 `WDA_EXCLUDEFROMCAPTURE`，会被录进视频）；`Root` 同 8 DIP 圆角。
  图标：操作录制换成 键盘、延时换成 倒计时（秒表）、重录换成 刷新（ui-icons）。
- 贴图 Alt 辅助线：按住鼠标左 / 右键时不显示（Alt + 拖动贴图）。

## 8.1.45 0.8.45 变更

- 截图：选好区域、未拿批注工具（也没选中元素）时滚轮调圆角（`WheelCorner`，每格 2 DIP，0 ～ 短边一半，`RememberCorner` 记住，`WheelTip` 显示「圆角：N」；手绘截图提示没有圆角）。
  拖动移动选区不显示放大镜（`ShouldShowMagnifier` 与 `UpdateSelectionVisuals` 只在 Resizing 时显示）。
- 贴图关闭：缩放中心 `RenderTransformOrigin = (0.5, 0.72)`，缩到 0.55，看起来往中心偏下沉下去。
- 隐藏 ▾（`AppConfig.HideToolChevrons`，默认开）：`AnnotationToolbar.ApplyChevrons` 收起六个工具的 ▾；鼠标停在工具按钮上 140 ms 后 `OpenHoverMenu`
  在按钮下方弹出 `Popup`（`StaysOpen = true`，不抓鼠标，工具按钮仍可直接点）里的竖排 `Menu`（同 ContextMenu 的 MenuItem 样式、勾选当前样式），
  鼠标离开按钮和列表 260 ms 后关闭（`WatchHoverMenu` 轮询）。设置 → 批注栏 的开关，保存后贴图 / 编辑器 / 下次截图生效。
- 红色预设改为 #FF1E1E（原 #D94B38 偏灰）。
- 文字识别图标改为 `IconOcrMask`（`DrawingBrush` 画同一几何的蒙版）：原来是 Stretch 的 `Path`，工具栏弹出时的缩放动画里每帧对齐像素，看起来在抖。

## 8.1.44 0.8.44 变更

- 贴图：`PinWindow.Sweep`（`CreatePin(sweep: true)`）——恢复贴图（托盘 / 快捷键 / 贴图历史）和编辑器「贴到屏幕」淡入后约 90 ms 播放 `PlayLightSweep`；
  关闭时 `RootGrid` 以中心缩到 0.6（160 ms，加速），与淡出同时。
- 贴图上按住 Alt：`PinCrosshair` 取代 `PinMagnifier`（已删除）——鼠标所在显示器上一个全屏、点击穿透、不激活的透明窗口画两条线（与截图 `CrossH/CrossV` 同色）；
  窗口用 `SetWindowPos` 按物理像素铺满显示器，线位置按该窗口 DPI 换算。
- 截图提示：`FillHint` 生成带 `InlineUIContainer` 鼠标图标的提示（`Assets/Icons/mouse_left|right|middle|wheel.png`，来自 ui-icons 的 鼠标左键 / 右键 / 中键 / 滚轮，裁边后 64 px），
  多显示器副本同样生成；`Motion.PopOnShow(Hint)` 弹出。滚轮提示 `WheelTip` 固定在鼠标所在显示器底部居中（离底 72 DIP），不再跟随鼠标。
- 亮区渐暗：`BrightFade`（XAML，`BrightImage` 之上）——悬停亮区从有到无时（按下开始截图、离开元素）复制原区域并 240 ms 淡出，`_brightShown` 记录上一帧亮区。
- 拖动选区：Creating / Moving / Resizing 的移动里改为 `RequestSelectionVisuals`（`CompositionTarget.Rendering` 前合并为一次，松开时取消排队由松开自己刷新）；
  截图期间 `GCSettings.LatencyMode = SustainedLowLatency`（关闭时恢复）；画面分块的后台线程降为 BelowNormal 优先级。这些针对「偶尔不流畅」：后台识别分配大量内存触发的阻塞 GC、高回报率鼠标的重复刷新。
- 剪贴板：`ClipboardService.CopyImage` 在独立 STA 线程写入，UI 最多等 2.5 秒。原因：WPF 的 `SetDataObject` 本身对 `OleSetClipboard` / 刷新各重试约 1 秒，
  外层再重试 10 次时剪贴板被占用可让窗口卡十几秒（日志里有「剪贴板被其他程序占用」）；现在外层 3 次，且不卡 UI。图片先冻结（`Frozen`）以便跨线程。
- 图片编辑器：元素样式分区 `SlideSection` 高度 / 外边距 / 透明度 240 ms 动画（展开时先测量目标高度，结束后恢复 `Height = NaN`）；「重置所有修改」移出滚动区固定在面板底部（`_resetAllFooter`）；
  反色改为 `FooterToggle` 样式（App.xaml，选中时蓝底蓝边），与曲线、一键增强同一 `WrapPanel`、同尺寸边距。
- 工具栏默认顺序「复制」在最右：截图 `…Save,Translate,Edit,Copy`、贴图 `Close,Save,Clear,Edit,Apply,Copy`、编辑器 `…Clear,Pin,Copy`；`EnsureDefaults` 把仍是旧默认值（没调整过）的顺序换成新默认。
- 橡皮擦软边：`EraseStroke.Softness`（有软边或不满强度的笔画走 `PartialMasks`），`PushErase` 用 5 层由宽到窄的环做不透明度蒙版（smoothstep 坡度）；
  `AppConfig.EraserSoftness`，工具栏「软边」滑条在擦除模式下显示并写这个值（抠除没有），`Shift + 滚轮` 同样可调；实时擦除 `LiveEraserSoftness`。
- 去掉设置里的「更新路径」：只从线上 Release 检查（`AppConfig.UpdatePath`、`UpdateService.EffectivePath` 删除，`FindNewestAsync(null)`）。

## 8.1.43 0.8.43 变更

- 所有批注工具（含水印、文字、橡皮、抠除、马赛克笔、序号…）拿在手里时，选区边框都可调整：`DrawingTool` 只排除 无 / 选择。
  光标：`UpdateAnnotationHover` / `UpdateWatermarkHover` 之前会用工具光标盖掉 `UpdateCursor` 给的调整箭头，现在在边框上先用 `FrameCursorAt`。
  正在输入文字时点在选区边框上：先结束输入，再开始调整选区（以前只结束输入）。测试逐个工具检查命中和光标。

## 8.1.42 0.8.42 变更

- 滚动条统一：细滚动条样式（`SlimThumb` + 隐式 `ScrollBar` 样式，竖 / 横两种，悬停 / 拖动时变亮）移到 `App.xaml`，所有窗口共用；
  图片编辑器右侧面板、贴图历史等代码创建的 `ScrollViewer` 不再是系统白色滚动条。设置窗口和翻译窗口里各自的那份删掉了（同一个外观）。
- 截图里拿着画笔 / 形状 / 橡皮等工具时，选区的边和角仍可拖动调整：`HitHandle` 在 `DrawingTool` 时内侧只留 5 DIP 的窄带（外侧照旧 12 DIP），
  鼠标按下时先于开始绘制判断（`OverlayWindow.Input` 里 Text 分支之前）；光标同样在窄带里变成调整箭头。水印工具不算（它拖动的是水印）。

## 8.1.41 0.8.41 变更

- 滚轮到整个屏幕：识别链在窗口之上追加屏幕框（`HoverCandidate.Screen`，`WithScreens`）：鼠标所在显示器「整个屏幕」，多显示器时再加「全部屏幕」；
  与窗口重合（窗口就是全屏）时不加。屏幕框只能用滚轮到达：`StartLevel` 只在非屏幕框里选起点；到屏幕框、从屏幕框回来都不改 `DetectRange`（提示「整个屏幕（临时，不记住档位）」）。
  只框窗口时滚轮向下仍交给 `WheelInWindow` 进入窗口内部（`StepHoverLevel` 在最低层且是窗口时返回 false）。
- 原位翻译排版（`Render` → `Layout` + `Placement`）：先给所有段落排版，再统一盖掉原文，最后统一写字（前一段延伸的文字不再被后一段的底色盖住）。
  可延伸范围按原图的空白背景算（`FreeBelow` / `FreeRight`，与背景色距离 ≤ 28 算空白，并留 0.35 em 间距），所以译文停在下一段、边框或图片之前；照片背景不延伸。
  候选依次尝试：多行段落 原字号 → 行距收紧 → 向右空白处加宽 → 逐级缩小到 65%；单行标签 原字号单行（可向右空白处延伸）→ 92% → 原字号折行 → 90% / 80% / 70% 折行。
  `SharedEm`：测得字号相差 ≤ 12% 的段落统一用该组的典型字号（OCR 对同一正文每段测得略有不同，之前同一页译文大小不一）。
- 截图里右键 = Esc：选好区域后右键松开（未拖动）调 `HandleEscape`，与 Esc 共用「按一次 Esc 或右键直接退出截图」设置；正在批注时也一样（以前右键在批注时无效、否则重新框选，需要两次右键）。
  逐步退出时「取消选区」改为 `ResetCapture`（连同画在里面的批注一起清掉）。
- `fontprobe page [en]`：画一页中文（标题 + 折行的条目）存 `fontprobe_page.png`，再按英文示例重绘；`FONTPROBE_LONG=1` 用 4 倍长度的译文压力测试。

## 8.1.40 0.8.40 变更

- 原位翻译字号：原来多行段落「缩小直到放进原高度」（最低 60%），英文译成中文时每行字数变少、行数变多，几乎总被缩到很小。
  现在字号就是原文的 em（`ImmersiveTranslator.OutputSize`，跨文字体系时微调 ±8%），多行段落不缩小，放不下就向下延续并把延伸区域也盖上底色；
  单行标签仍可略缩（最低 85%）。`fontprobe`（不在默认列表）：对一张图片做 OCR，打印每行测得的 em，并用示例中文重绘到 `fontprobe_render.png`（不发翻译请求）。
  测试 batch2 用合成三行英文段落 + 更长的中文验证字形高度接近原 em 且向下延续。
- 设置窗口不再总在最前（去掉 `Topmost`，从托盘打开时只置前一次）；更新窗口以设置窗口为 owner，只在设置之上，不压住其他软件。
- 滚轮选的识别范围被记住：滚轮换层后按所框元素换算成 4 档（`RangeOf`，与 `AppConfig.StartsAt` 同一规则）写入 `DetectRange`，下一个元素、下次截图都从这一档开始，
  截图结束时若有变化就保存配置（`OverlayWindow.RangeChanged`）。提示改为「识别范围：细小元素（已记住）· 按钮 · 滚轮：上 变大，下 变小」。
  鼠标下没有窗口（无法换算）时不记。第 1 档时滚轮进入窗口内部会把档位记为 2，进入时按记住的档位选内层。

## 8.1.39 0.8.39 变更

- 识别更快：去掉换框前的等待（`_hoverDwell`），框立即出发，滑动 150 → 80 ms（`AppConfig.HoverSlide`）；探针实测从鼠标移到新元素到框到位约 190 → 110 ms，一次鼠标移动占 UI 线程约 0.1 ms。
  截图窗口打开时预热（`WarmUpDetection`）：提前算好前台软件窗口的画面分块、预先接触一次 UI Automation。分块在后台线程构建，构建期间 UI 线程不碰它（`_preparing`，否则会等锁）。
- 滚轮提示：截图提示条加「滚轮 调整识别范围」；滚动时鼠标旁 `WheelTip` 显示「识别范围 2 / 4：卡片（滚轮：上 变大，下 变小）」，到头时说「已是最大 / 最小范围」，1.5 秒后消失。
  只框整个窗口时（第 1 档或其他软件的窗口）滚轮向下进入窗口内部（`WheelInWindow`，`_detailWindow`），向上提示已最大。
  `WindowDetector.ActiveProgram`：前台是桌面、任务栏或 Markshot 自己时不算（此时所有窗口都识别元素）。
- 元素样式跟随形状：`ImageAnnotation.IsShaped`（`ImageOps.HasShape`：有透明部分，路径裁切、网格形变的结果、透明 PNG 都算；按 `Origin ?? Image` 缓存）。
  有形状时隐藏圆角（已设的圆角不再生效），描边用距离变换（`ShapeOutline`，画在形状外），阴影把透明度模糊（`ShapeShadow`），按参数缓存；大图在最长边 1200 的副本上算（4000×3000 描边约 275 ms）。
  `StyleReach` 把形状外的描边也算进「适应内容」。
- 测试：更新链接检查容忍 GitHub 的 5xx（实际遇到过 504），404 才算坏链接。

## 8.1.38 0.8.38 变更

- 截图识别：「识别灵敏度」改为「识别范围」4 档（`DetectRange`，默认 3）：1 窗口 / 2 大区块（≥ 窗口面积 1/12 且短边 ≥ 120 DIP）/ 3 常规元素（短边 ≥ 40 DIP 或宽行文字）/ 4 细小元素。
  档位决定识别链里第一个框（`StartLevel` + `AppConfig.StartsAt`），滚轮照样往外 / 往里；滚轮选过的层在鼠标不离开时保持。延迟统一 100 ms、滑动 150 ms（`HoverDelay` / `HoverSlide`）。
  只识别截图前在前台的软件窗口内部元素（`OverlayWindow.ActiveWindow` = 截图前的 `GetForegroundWindow`，按 `GA_ROOT` 比较），其他窗口整个框住。
  旧配置的 `DetectSensitivity` 读入时映射一次（1→2、2～4→3、5→4），之后只写 `DetectRange`。第 4 档还降低画面识别的边缘门槛。
- 编辑器「元素样式」分区（选中图片时出现，可多选）：圆角、阴影（多层渐隐，略向下）、描边宽度和颜色（`ImageAnnotation.Radius / ShadowSize / BorderWidth / BorderColor`），拖动滑条算一步撤销。
- 画布尺寸分区：背景 透明 / 白 / 黑 / 自选（就是画布位图的颜色：改尺寸、撤销、保存、贴到屏幕都跟着；存 JPG 时用它垫底），「适应内容」加边距（记住，`EditorFitMargin`，阴影也算进内容）。
- 调色：色温（红蓝通道）、锐化（反锐化掩模）、模糊（三次盒式模糊≈高斯，半径按图片尺寸，预乘透明度避免黑边），都在 `ImageAdjust` 里随图片走；「一键增强」= 按直方图（去两端 0.5%）拉伸的曲线 + 饱和度 10。

## 8.1.37 0.8.37 变更

- 自动识别框也带上记住的截图圆角：`AnnotationEditor.HoverCornerRadius`（蓝框外缘贴合圆角）和 `BrightClip` 的圆角由 `UpdateHoverBright` 一起设置，不超过元素短边一半；
  有选区后归零（悬停框此后用于批注）。单击识别框后的选区走原有的 `ApplyRememberedCorner`。

## 8.1.36 0.8.36 变更

- 编辑器拖入图片：已有图片时拖入的图片（可多张）作为图片元素加入，落在松开处（多张依次错开 24 px），一起选中，一步撤销（`AddPictures`）；空编辑器时第一张照旧打开。
- 画布尺寸分区「适应内容」：画布正好包住所有元素（含拖出画布的部分、批注线宽），`FitCanvasToContent` → `ChangeCanvas`，一步撤销。
- 吸附：拖动元素 / 多选时，边缘和中线靠近画布边缘、中线或其他元素的边缘、中线（6 屏幕像素内）就吸过去，并画洋红参考线（`Editor.Guides`），按住 Alt 不吸附。
  计算在 `Core/Snapping.cs`（纯几何，可单测）；移动改为「按下时的位置 + 指针位移」再吸附（`PinWindow.SnapMove` 虚方法，贴图不吸附）。
- 多选右键「对齐与分布」：左 / 水平居中 / 右、顶 / 垂直居中 / 底、横向 / 纵向等距分布（≥3 个），`Snapping.Align`，一步撤销。
- 保存窗口：JPEG / WebP 显示「质量」滑条（10～100，记住上次，`SaveQuality`）和实时估算的文件大小（按当前图片实际编码）；
  图片有透明部分且选 JPEG / BMP 时提示透明处会变白。`ImageFile.Quality` 由窗口设置，各保存入口都把要保存的图片传给窗口。

## 8.1.35 0.8.35 变更

- 中文输入法：`InputMethod.IsInputMethodEnabled` 不继承，批注时拿键盘的 `InputLayer` 之前开着输入法（字母 / 空格被输入法吃掉）；现在贴图、截图的 `InputLayer`，
  设置里的快捷键框 / 单键框（`HotkeyField` / `KeyField` 样式）、尺寸数字框（`SizeLabelField`、编辑器的宽高框）都关掉输入法。
  其余直接看 `e.Key` 的 Enter / Esc / Space 判断（取色器、录屏条、截图尺寸框、编辑器）改用 `AnnotationOps.RealKey`。
- 贴图「应用」后工具栏呼不出：工具栏窗口不激活，点应用时贴图若不是活动窗口（中间切过别的窗口 / 弹过对话框），键盘没有去处，Space 等快捷键全部失效。
  现在应用后 `Activate()` + 键盘回到贴图；贴图每次激活都确保有焦点（`EnsureKeyboard`）；`_spaceExitGuard` 只挡退出那次 Space 的自动重复（松开事件丢了也不会卡住）。
- 双击贴图空白处（未批注、或批注时没拿工具）= 复制并关闭（`CopyAndClosePin`，剪贴板被占用时提示不关）。设置「快速双击复制并关闭」（`QuickDoubleClickCopy`，默认关）：
  截图 / 贴图在 PreviewMouseLeftButtonDown 里处理双击，撤回第一下画出的东西（记下单击时的 `_history.Depth`），立即复制关闭。
- 贴图缩放时左下角 `PinZoomLabel` 显示百分比，停止 0.8 s 后淡出（含 F 复位）。
- 文字贴图（剪贴板文本 / 识别结果卡片）`TextCardRenderer` 按当前主题配色。
- 截图圆角记忆：松开圆角点时写 `CaptureCornerRadius`，之后的选区（拖出、点选窗口 / 元素、Ctrl+A、R 上次区域）都套用（不超过短边一半，套索除外）；截图结束和退出时有变化才写盘。
- 右键菜单弹出动画 160 → 70 ms。
- 贴图批注栏：应用按钮去掉强调色；应用右边加「复制」（`PinActions` 多了 Copy，旧配置自动补在应用后面）。
- 画笔软边：`PenAnnotation.Softness`（0…1），多层同心描边叠出平滑的透明过渡（宽的先画，每层补一点覆盖度，带透明度的颜色整体再乘）；笔压笔迹同样适用。
  画笔设置栏「软边」滑条 0～100，Shift+滚轮每格 5%（截图、贴图、编辑器；选中笔迹时改它）；新笔迹用上次的值（`PenSoftness`）。
- 测试：剪贴板可由 `ClipboardService.CopyOverride` 接管（测试不碰用户剪贴板）；录屏暂停检查改为直接看暂停期间计时不走（以前和墙钟比，慢机器上偶尔失败）。

## 8.1.34 0.8.34 变更

- 设置「保存」立即生效且不关闭窗口（`SettingsWindow.SavedChanges` → App 保存配置、刷新贴图 / 编辑器 / 快捷键）；不再弹托盘气泡（没有提示音），改为底栏短暂显示「✓ 设置已保存并生效」。
  「取消」改名「关闭」：关窗时恢复到已保存的主题 / 字号 / 阴影（未保存的修改丢弃）。
- 设置里所有 CheckBox 用隐式样式改成小号药丸开关（28×16，原位翻译的 `Switch` 样式缩小版）。
- 识别灵敏度同时决定换框前的停留时间和滑动时长（`AppConfig.HoverDelayFor`：520/340/200/90/0 ms，`HoverSlideFor`：260/210/170/130/100 ms）：
  鼠标移到另一个元素后要停稳这么久框才过去，扫过页面时不再乱跳；第一次出现和仍在当前元素上时立即响应（`OverlayWindow.SetHoverChain` → `ApplyHoverChain`）。

## 8.1.33 0.8.33 变更

- 画笔 / 线条粗细滚轮：每格 ±1（以前 ±0.5，界面取整显示且四舍六入五成双，看上去要滚 2～3 格才变）；选中元素的滚轮粗细同样每格 ±1。
- 画笔光标圈最小直径 6 → 2 像素（细笔刷也跟着变小），小于 8 像素时不画中心点。
- 截图自动识别：鼠标框住的元素区域是亮的（`UpdateHoverBright` 用 `BrightClip` 跟随悬停框，含过渡动画），其余变暗。
- 设置「保存与识别 → 截图识别」：总开关「自动识别截图区域」（`DetectAuto`，关掉后元素 / 窗口子项变灰）和「识别灵敏度」1～5（`DetectSensitivity`，默认 3 = 以前的效果）：
  灵敏度决定画面识别的边缘对比度门槛（`AppConfig.EdgeThresholdFor`：44/36/28/20/14）和最小元素边长（`MinSideFor`：16/10/4/4/3，无障碍元素同样过滤）。

## 8.1.32 0.8.32 变更

- 截图中 Esc：新设置 `EscExitsCapture`（默认开，设置「快捷键 → 截图中」的复选框）。开：按一次 Esc 直接退出截图；关：逐步退出（先取消选中 / 放下工具，再取消选区，最后退出）。逻辑在 `OverlayWindow.HandleEscape`。
  文字输入、折线绘制中的 Esc 仍由各自处理（先结束输入 / 折线）。

## 8.1.31 0.8.31 变更

- 设置窗口打开时全局快捷键照常工作；只有快捷键输入框获得键盘焦点期间暂停（`SettingsWindow.RecordingHotkey`，失去焦点 / 切走窗口即恢复）。以前一打开设置就全部注销。
- 设置抽屉第二张二维码（微信）：`Assets/donate_qr_wechat(_light).png`，从原图抠出模块区，和第一张同尺寸（497×495，内容 486×486）、同灰度（深色 168 / 浅色 64，透明底）。
  抽屉拉出时鼠标进入二维码区域（`DrawerQrHost` 透明底，翻转中不丢悬停）水平翻转到第二张，离开翻回（`TurnQr`：ScaleX 1→0 换图 0→1，各半个 Motion.Base；中途改变则接着翻）；收起抽屉时复位。

## 8.1.30 0.8.30 变更

- 编辑器 `ImageHost` 最底层加一块向四周延伸 20000 DIP 的透明矩形：选中元素的框线 / 手柄超出画布边缘或在画布外时也能触发光标并拖动（以前 `ImageHost.IsMouseOver` 为假，点击被丢掉）。画布外空白处双击不再打开调节框。
- 修复：多选后网格形变，合并出的图片被留在 `Hidden` 状态，应用后整块看不见；现在合并图片从不隐藏（隐藏的是原元素），`CancelSession` 也总会取消隐藏。
- 梯形校正、网格形变必须先选中元素（没选中时状态栏提示，不再默认作用于第一张图片）。
- 「旋转与镜像」去掉角度滑条和「应用角度」（`RotateBy` 仍保留给代码 / 测试）。

## 8.1.29 0.8.29 变更

- 编辑器默认「选择」（浮动栏最左的 `ToolSelect`，仅编辑器显示；没有工具时就是它）。空白处（画布外也行）拖动框选（`Editor.Marquee`，碰到即选），
  Shift 单击 / 框选加选，Ctrl 单击 / 框选减选，Ctrl+A 全选；多选 `_group`（≥2 个，各画虚线框 `Editor.Group`），框内拖动一起移动，方向键微调，Delete 删除，右键置顶 / 置底 / 删除。
- 裁切与变形作用于选中元素：单张图片照旧；多选或批注先 `Merge` 成一张图片（会话结束应用时 `Realize` 替换原元素，取消则不变）；
  矩形裁切有选中时裁选中的元素（`CropPicture`，画布不变），没选中时裁画布；双击调节框对多选 / 批注逐个移动缩放旋转（`TransformItems`，仍可编辑）。
- `Annotation.Hidden` 移到基类（`DrawItem` 跳过，克隆时清掉）。
- 粘贴（Ctrl+V / 粘贴图片 / 粘贴元素）后处于「待放置」：Esc 取消（`AnnotationHistory.TrimTo` 去掉这一步），Enter 或点击空白处完成。

## 8.1.28 0.8.28 变更

- 修复：0.8.25 / 0.8.26 从发布页找到的安装包链接漏了 `/<owner>/<repo>`（`https://github.com/releases/download/...` → 404），自动更新必然失败。
  现在拼成 `https://github.com/{owner}/{repo}/releases/download/...`；新增 `UpdateService.LatestOnPageAsync`，测试对最新安装包链接做 HEAD 请求，必须能下载。

## 8.1.27 0.8.27 变更

- 图片编辑器改为「透明画布 + 图片元素」：打开的图片是 `ImageAnnotation`（`Image` / `Origin` / `Adjust` / `Rotation` / `Hidden`），`_baseImage` 只是透明画布（画布尺寸）。
  图片可与粘贴的图片、批注任意排层（右键直接有 上移一层 / 下移一层 / 置于顶层 / 置于底层）；橡皮擦不擦图片（`Erasable=false`），抠除照样穿透。
- 面板操作对象 `Target` = 会话中的图片 → 选中的图片 → 第一张图片。角度、调节框（`TransformTo(Rect, angle)`）、调色、路径裁切、梯形校正、网格变形只改该图片；
  画布尺寸（`CanvasTo`）、矩形裁切（`CropTo`）、左右转 / 镜像改整个画布（元素原地平移，保持同一对象）。画布永远不会被调节框 / 角度撑大或裁掉。
- 马赛克 / 吸色的取样源 `MosaicSource` = 画布上图片的渲染（缓存，`PictureKey` 变了重建）；水印画在最上面一张图片 / 马赛克之上。
- 贴到屏幕：最上面一张图片及其下方的内容合成为贴图底图，其上的批注（和抠除）仍可编辑。截图的撤销步骤交给编辑器时补上图片元素（`AdoptHistory` 覆写）。
- 浮动栏新增「粘贴图片」（原打开按钮的图标）、「打开图片」（ui-icons 的 打开.png → `folder_open.png`）、「清空批注」（图片保留）；旧的顺序配置自动补入新按钮。
- 画笔 / 荧光笔：按住 Alt 显示吸色十字（`PenEyedropper`，截图、贴图、编辑器通用；截图里此时 Alt 不切换辅助线，贴图的放大镜暂停），单击取色（`AnnotationToolbar.PickColor`）。
- 数位板笔压：`CreationGesture.Pressure`（`StylusPoint.PressureFactor`）→ `PenAnnotation.Pressures`，有笔压时按点变宽绘制（同向绕行的圆 + 四边形，Nonzero 填充）。

## 8.1.26 0.8.26 变更

- 检查更新连不上 / 被限流时不再显示「已是最新版本」，而是「检查更新失败（原因）」并给出手动下载地址（`UpdateService.LastFailure`）。

## 8.1.25 0.8.25 变更

- 检查更新先读 GitHub 发布页（`/releases/latest` 跳转到的 tag + `releases/expanded_assets/<tag>` 里的 `Markshot-Setup-<版本>.exe` 链接），不占匿名 API 每 IP 每小时 60 次的额度；
  页面读不到或格式变了才退回 `api.github.com/.../releases/latest`。此前公司网络共享出口 IP 额度用完时一律 403，被当成「没有更新」。

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
