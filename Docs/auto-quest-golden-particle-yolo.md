# 自动任务金色粒子路径：YOLO 分割方案与完整教程

状态：采用本方案，先完成数据集和离线验证，再通过实验开关接入实机导航。

本文档覆盖从录像采集、标注、训练、验证、ONNX 导出，到 BetterGI C# 接入和实机验收的完整流程。C# 与工程代码由开发者负责，素材录制和标注确认需要人工参与。

## 1. 目标与边界

目标是在任务已被追踪时识别地面上的金色粒子路径，并把它作为短程道路指引：

- 任务图标仍是最终目的地；
- 金色粒子只负责告诉角色眼前应该向哪里走；
- 粒子不可见、模型置信度不足或导航没有进展时，立即退回任务图标跟随；
- 第一版不让模型决定任务状态、到达条件、剧情阶段或战斗状态。

模型不是完整导航器。它只输出“哪些像素属于任务粒子路径”，后续的路径排序、转弯判断、按键控制和安全回退仍由 C# 完成。

## 2. 为什么选择 YOLO Segmentation

普通 YOLO Detect 输出矩形框，适合人物、怪物、图标等边界明确的对象。金色粒子路径具有以下特点：

- 由大量离散小亮点组成；
- 可以弯曲、上楼、下坡或被角色遮挡；
- 同一条路径的长宽比变化很大；
- 我们需要的是路径中心线和局部方向，而不是一个大矩形。

因此使用 YOLO Segmentation 输出像素掩膜。训练时把一段可见路径标成一条连续的“路径带”，推理后从掩膜提取中心线、曲率和前视点。

推荐第一版使用 `yolo11n-seg`：

- 模型较小，适合实时推理；
- 可以固定 960×960 输入，兼顾远处小粒子；
- 项目当前使用 YoloSharp 和 ONNX Runtime，接入路径成熟；
- 如果小目标召回不足，再升级到 `yolo11s-seg`，不先盲目增加模型体积。

## 3. 总体架构

```text
游戏截图
  ↓
中央游戏画面裁剪，排除大部分 HUD
  ↓
YOLO Segmentation
  ↓
quest_particle_path 概率掩膜
  ↓
V 键前后差分与连续帧确认
  ↓
去噪、骨架/中心线、近中远控制点、局部切线
  ↓
输出 ScreenOffsetX、曲率、置信度
  ↓
QuestMarkerFollower 转向与前进
  ↓
任务图标兜底与最终到达确认
```

模型与传统视觉不是互斥关系。YOLO 负责语义判断“这是不是任务粒子”，V 键前后差分负责验证“它是不是刚刷新的动态引导”，OpenCV 负责把掩膜变成可执行路径。

## 4. 双方分工

### 4.1 需要人工完成

1. 在不同地区录制任务粒子录像；
2. 录制容易混淆的负样本，例如黄色花朵、灯光和技能特效；
3. 按本文档规则标注路径，或检查自动预标注结果；
4. 实机运行实验开关并反馈录像和日志。

### 4.2 代码与工程工作

开发者负责：

1. 从录像抽帧、去重和按场景拆分数据集；
2. 检查标签格式和数据泄漏；
3. 训练、验证、调参和导出 ONNX；
4. 编写 C# 分割推理、路径提取和时序融合；
5. 接入现有 `QuestMarkerFollower`，保留旧版和实验开关；
6. 编写单元测试、录像回放测试和性能测试；
7. Debug 编译并根据实机日志迭代。

如果采用协作方式，使用者不需要自己编写 Python 或 C#，只需要提供原始录像和完成标注确认。后续章节仍给出完整命令，便于复现和独立训练。

## 5. 数据采集

### 5.1 建议数量

概念验证数据：

- 30–50 段包含任务粒子的正样本录像；
- 20–30 段没有任务粒子但存在黄色干扰的负样本录像；
- 约 500–800 张最终标注图片；
- 至少 150 张来自完全独立场景的验证/测试图片。

正式版本目标：

- 80–150 段正样本录像；
- 50 段以上困难负样本；
- 1,500–3,000 张有效标注；
- 至少覆盖 20 个不同地点。

连续录像中的相邻帧非常相似，不能把“抽了 3,000 帧”等同于 3,000 个独立样本。场景多样性比帧数更重要。

### 5.2 必须覆盖的正样本

- 直线路径；
- 明显左转和右转；
- S 形连续转弯；
- 上楼梯、下楼梯、斜坡；
- 路径从角色左侧或右侧开始；
- 近处粒子很大、远处粒子很小；
- 粒子只显示一小段；
- 角色、NPC 或场景物件遮住部分路径；
- 白天、夜晚、室内、洞穴和高亮环境；
- 不同角色、不同服装颜色和不同画质设置。

### 5.3 必须覆盖的负样本

负样本图片中不创建任何标签：

- 黄色和橙色花朵；
- 路灯、火把、阳光反射；
- 黄色任务图标和小地图标记；
- 黄色体力弧；
- 武器、角色技能和元素特效；
- 掉落物光柱；
- NPC 头顶图标；
- 只有任务图标、没有粒子路径的画面；
- 按 V 失败或路径已消失的画面。

本次录像中喷泉旁花丛导致的误识别必须作为困难负样本加入。

### 5.4 录制要求

- 保持游戏原始分辨率，优先 1920×1080；
- 30 FPS 已足够，60 FPS 也可以；
- 不要裁掉游戏 HUD，工程会在推理前统一裁剪；
- 不要给录像添加字幕、鼠标高亮或平台水印；
- 每个地点录制 10–30 秒；
- 正样本中至少按一次 V，让粒子从出现到消失的过程完整保留；
- 负样本中要有镜头转动，避免模型只学会固定背景；
- 文件名建议包含地点、昼夜、正负样本和编号。

命名示例：

```text
sumeru_city_day_positive_001.mp4
sumeru_city_night_flowers_negative_001.mp4
fontaine_stairs_right_turn_positive_001.mp4
liyue_skill_effect_negative_001.mp4
```

## 6. 从录像抽帧

推荐每秒抽取 2–4 帧，再进行相似帧去重。不要直接把 30 FPS 的每一帧全部用于训练。

PowerShell 示例：

```powershell
$ffmpeg = "C:\Program Files (x86)\FormatFactory\ffmpeg.exe"
New-Item -ItemType Directory -Force .\frames\sumeru_city_day_positive_001
& $ffmpeg `
  -i .\videos\sumeru_city_day_positive_001.mp4 `
  -vf "fps=3" `
  -q:v 2 `
  .\frames\sumeru_city_day_positive_001\%06d.jpg
```

如果 ffmpeg 已加入 PATH，可把 `$ffmpeg` 换成 `ffmpeg`。

抽帧后要删除：

- 完全重复的画面；
- Alt+Tab、菜单、地图和加载界面；
- 严重运动模糊，人工也无法判断路径的画面；
- 同一静止画面过多的相邻帧。

保留粒子刚出现、最亮、逐渐消失和部分遮挡的不同阶段。

## 7. 标注教程

### 7.1 类别

第一版只有一个类别：

```text
0: quest_particle_path
```

不要把任务图标、目标光柱或黄色体力弧标成这个类别。

### 7.2 推荐标注方式

把同一条可见粒子路径标成一条连续的窄多边形路径带：

1. 从角色附近最靠下的真实粒子开始；
2. 沿着粒子链依次布置多边形顶点；
3. 包住粒子亮点及少量光晕；
4. 在转弯处增加顶点，使路径带贴合弯曲方向；
5. 到最远处仍能确认属于同一条路径的粒子结束；
6. 不要把粒子之间大片无关背景包含进去。

不需要逐颗粒子创建几十个独立实例。我们的目的不是统计粒子数量，而是得到稳定的道路中心线。

### 7.3 遮挡规则

- 只标注实际可见的粒子；
- 不要凭想象穿过角色或墙体补全路径；
- 被角色分成两段时，可以创建两个同类别多边形；
- 如果只剩 1–2 个孤立亮点，无法判断方向，则不标注；
- 与花朵、灯光重叠但仍能明确区分时，只包住粒子部分。

### 7.4 转弯规则

- 多边形要沿真实弯道转向，不要用一个大三角形覆盖；
- 路径横向转弯后，即使远端高度变化不大，也必须标出弯曲形状；
- 楼梯路径按屏幕投影标注，不需要推断三维坡度；
- 多条黄色视觉元素并存时，只标任务追踪生成的那条链。

### 7.5 使用 CVAT

推荐使用 CVAT 的 Polygon 工具：

1. 创建一个图像任务；
2. 创建标签 `quest_particle_path`；
3. 上传按录像分组的抽帧图片；
4. 选择 Polygon，沿路径绘制窄多边形；
5. 对负样本保持空标注；
6. 完成后导出 `Ultralytics YOLO Segmentation` 格式；
7. 保留原始 CVAT 项目或备份文件，后续可以修正标签。

如果使用其他标注工具，必须确认导出的是 YOLO Segmentation 多边形格式，而不是普通 YOLO Detect 矩形框格式。

### 7.6 标签文本格式

每个多边形占一行：

```text
class_id x1 y1 x2 y2 x3 y3 ...
```

坐标必须归一化到 0–1。例如：

```text
0 0.4821 0.8014 0.4910 0.7602 0.5048 0.7065 0.5207 0.6421
```

负样本可以没有同名标签文件，也可以有一个空标签文件。数据检查脚本会统一处理。

## 8. 数据集目录与拆分

推荐目录：

```text
golden-particle-dataset/
├── data.yaml
├── images/
│   ├── train/
│   ├── val/
│   └── test/
└── labels/
    ├── train/
    ├── val/
    └── test/
```

`data.yaml`：

```yaml
path: .
train: images/train
val: images/val
test: images/test

names:
  0: quest_particle_path
```

拆分比例建议：

- 训练集 70%；
- 验证集 15%；
- 测试集 15%。

必须按“整段录像或整个地点”拆分。禁止把同一段录像的前半部分放训练集、后半部分放验证集，否则指标会虚高。

至少把以下内容固定放入测试集：

- 一条从未参与训练的楼梯弯道；
- 一个黄色花朵很多的地点；
- 一段夜间路径；
- 一段技能特效干扰；
- 一段只有任务图标但没有粒子的录像。

## 9. 训练环境

建议使用 Python 3.11 和带 NVIDIA 显卡的环境。只有 CPU 也能训练，但时间会明显增加。

PowerShell：

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
python -m pip install ultralytics onnx onnxruntime
```

确认环境：

```powershell
yolo checks
```

如果 `yolo` 命令不可用：

```powershell
python -m ultralytics checks
```

训练数据和虚拟环境不要提交到 Git。仓库只保留最终 ONNX、模型说明、推理代码和小规模测试样本。

## 10. 第一轮训练

进入数据集父目录后执行：

```powershell
yolo segment train `
  model=yolo11n-seg.pt `
  data=.\golden-particle-dataset\data.yaml `
  imgsz=960 `
  epochs=120 `
  patience=25 `
  batch=8 `
  device=0 `
  workers=4 `
  project=.\runs\autoquest `
  name=golden_particle_y11n_960 `
  seed=20260718 `
  deterministic=True `
  flipud=0.0 `
  fliplr=0.5 `
  degrees=2.0 `
  perspective=0.0 `
  mosaic=0.2 `
  close_mosaic=10
```

说明：

- `imgsz=960`：比 640 更适合远处小粒子；
- `epochs=120`：只是上限，早停会在长期无提升时结束；
- `flipud=0`：游戏画面不能上下翻转；
- `fliplr=0.5`：左右翻转有助于平衡左转和右转；
- `perspective=0`：避免制造不符合游戏摄像机的夸张形变；
- `mosaic=0.2`：只保留少量拼图增强，避免破坏路径连续性；
- 显存不足时先把 `batch` 改为 4，不先降低输入尺寸。

没有 NVIDIA 显卡时把 `device=0` 改为 `device=cpu`。CPU 训练仅适合小规模验证。

训练输出通常位于：

```text
runs/autoquest/golden_particle_y11n_960/
├── args.yaml
├── results.csv
├── results.png
├── confusion_matrix.png
└── weights/
    ├── best.pt
    └── last.pt
```

后续始终使用 `best.pt`，不是 `last.pt`。

## 11. 验证与验收指标

执行独立测试集验证：

```powershell
yolo segment val `
  model=.\runs\autoquest\golden_particle_y11n_960\weights\best.pt `
  data=.\golden-particle-dataset\data.yaml `
  split=test `
  imgsz=960 `
  device=0 `
  plots=True
```

第一轮目标，不作为无条件保证：

- 路径存在判定 Precision ≥ 0.90；
- 路径存在判定 Recall ≥ 0.80；
- Mask mAP50 ≥ 0.80；
- 困难负样本误报率 ≤ 5%；
- 左/直/右转向判断准确率 ≥ 90%；
- 连续 5 分钟离线录像回放中，错误粒子接管不超过 1 次。

不要只看 mAP。导航更关心以下工程指标：

1. 路径不存在时是否误报；
2. 转弯方向是否正确；
3. 路径中心线横向误差；
4. 连续帧方向是否抖动；
5. 实机任务距离是否持续下降。

如果 Precision 很高但 Recall 较低，系统可以安全退回任务图标；如果 Recall 很高但 Precision 较低，错误接管会把角色带向花丛，风险更大。因此第一版优先 Precision。

## 12. 可视化检查

对测试录像运行：

```powershell
yolo segment predict `
  model=.\runs\autoquest\golden_particle_y11n_960\weights\best.pt `
  source=.\test-videos `
  imgsz=960 `
  conf=0.55 `
  device=0 `
  save=True
```

逐段检查：

- 掩膜是否只覆盖真实任务粒子；
- 花朵、体力弧和任务 UI 是否被忽略；
- 转弯处掩膜是否连续；
- 路径变暗时是否仍保留主要方向；
- 粒子完全消失后掩膜是否及时消失。

发现错误时，优先把错误画面加入训练集并修正标注，不先不断降低置信度阈值。

## 13. 导出 ONNX

通过测试后导出固定输入尺寸模型：

```powershell
yolo export `
  model=.\runs\autoquest\golden_particle_y11n_960\weights\best.pt `
  format=onnx `
  imgsz=960 `
  opset=17 `
  simplify=True `
  dynamic=False `
  half=False
```

要求：

- 输入尺寸固定为 960×960；
- 不使用动态尺寸；
- 第一版使用 FP32，接入稳定后再评估 FP16；
- 保留对应的 `args.yaml`、类别名称、训练数据版本和指标报告；
- 不仅交付 `.onnx`，还要保留 `best.pt` 便于继续训练。

最终模型建议命名：

```text
golden_particle_path_y11n_seg_960_v1.onnx
```

## 14. 模型版本记录

每个模型附带一个说明文件：

```yaml
name: golden_particle_path_y11n_seg_960_v1
task: segment
input_size: 960
class_names:
  - quest_particle_path
train_videos: 80
train_images: 1800
val_images: 300
test_images: 300
precision: 0.93
recall: 0.84
mask_map50: 0.86
git_commit: fill_after_integration
known_limitations:
  - very_distant_particles
  - strong_gold_skill_effects
```

模型、数据集和代码必须能对应版本，避免后续不知道应用里加载的是哪次训练结果。

## 15. BetterGI C# 接入设计

以下部分由开发者完成，使用者不需要手工修改。

### 15.1 模型文件与注册

模型目标位置：

```text
BetterGenshinImpact/Assets/Model/AutoQuest/
└── golden_particle_path_y11n_seg_960_v1.onnx
```

在 `BgiOnnxModel` 注册模型，使用 `BgiOnnxFactory` 创建推理器，复用现有 CPU、DirectML、CUDA 或 TensorRT 配置。

项目已经包含：

- `Microsoft.ML.OnnxRuntime`；
- `Microsoft.ML.OnnxRuntime.DirectML`；
- `YoloSharp`；
- `BgiOnnxFactory`；
- 统一的模型缓存和硬件加速配置。

### 15.2 新增文件

计划新增：

```text
GameTask/AutoQuest/Navigation/
├── YoloGoldenParticlePathRecognizer.cs
├── GoldenParticleMaskPathExtractor.cs
├── GoldenParticleTemporalValidator.cs
└── GoldenParticleGuidanceOptions.cs
```

职责：

- `YoloGoldenParticlePathRecognizer`：截图裁剪、缩放、YOLO 分割推理和坐标还原；
- `GoldenParticleMaskPathExtractor`：掩膜去噪、中心线、路径起点、曲率和前视点；
- `GoldenParticleTemporalValidator`：V 键前后差分、连续帧确认和短暂丢帧容忍；
- `GoldenParticleGuidanceOptions`：阈值、推理频率、前视距离和实验开关。

### 15.3 保持接口兼容

第一版尽量继续输出：

```csharp
public sealed record GoldenParticlePathDetection(
    bool Found,
    Rect Bounds,
    float ScreenOffsetX,
    float Confidence,
    int ParticleCount,
    float VerticalSpan,
    int BrightCoreCount = 0);
```

如果需要曲率和多控制点，新增可选字段，避免改动现有调用方。`QuestMarkerFollower` 不直接依赖 YOLO 类型，只依赖 `IGoldenParticlePathRecognizer`。

### 15.4 推理区域

默认只处理游戏中央走廊：

- 横向：屏幕宽度 18%–82%；
- 纵向：屏幕高度 2%–92%；
- 排除右上角任务 UI 和角色列表；
- 裁剪后等比例填充到 960×960；
- 输出掩膜再映射回原始屏幕坐标。

不直接把整张 1920×1080 拉伸成正方形，否则路径会变形，转弯角度也会产生误差。

### 15.5 掩膜后处理

模型输出后执行：

1. 过滤低于置信度阈值的实例；
2. 合并同类别、空间上连续的掩膜；
3. 去除小面积噪点和 HUD 边缘；
4. 保留靠近角色下方中央区域的候选路径；
5. 对掩膜做骨架化或分层中心点提取；
6. 从角色附近向远处排序中心点；
7. 拟合平滑折线或样条曲线；
8. 输出自适应前视点。

前视距离：

- 急弯或低置信度：缩短前视距离；
- 直线或高置信度：增加前视距离；
- 不再只追逐单个最亮粒子。

### 15.6 时序策略

即使使用 YOLO，也必须保留时间证据：

- 按 V 前记录基准帧；
- 按 V 后 0.1–1.0 秒采集短序列；
- 模型掩膜必须与新出现/闪烁区域重合；
- 连续 3–5 帧方向一致后才接管；
- 已接管时允许短暂丢失 2–3 帧；
- 路径方向突然跨越屏幕时重新确认；
- 每次接管保持最短锁定时间，避免任务图标与粒子之间逐帧切换。

### 15.7 性能策略

- YOLO 分割不必每个游戏帧都运行；
- 初版目标 5–10 次推理/秒；
- 中间帧使用上一条路径和平滑结果；
- 优先使用 `n` 模型和 960 输入；
- 显卡较弱时每 2–3 个导航循环推理一次；
- 模型推理失败或设备不可用时自动退回原有识别器/任务图标。

DirectML、CUDA 和 TensorRT 的实际耗时必须在目标电脑上测量，不能用训练显卡的速度推断实机速度。

## 16. 导航融合与安全回退

粒子接管条件建议全部满足：

- 分割置信度达到阈值；
- 掩膜面积和纵向跨度合理；
- 路径起点靠近角色附近；
- 连续帧方向一致；
- 位于按 V 后的有效时间窗口；
- 当前任务距离大于最终接近阈值；
- 没有处于粒子回退冷却期。

立即退出粒子接管的条件：

- 连续多帧无掩膜；
- 方向出现不合理跳变；
- 任务距离明显增加；
- 10–15 秒任务距离没有改善；
- 地图坐标不变并被现有 `StuckDetector` 判定卡死；
- 到达最终接近距离；
- 进入剧情、交互或其他到达证据出现。

退出后使用现有任务图标跟随，不写另一套 WASD 脱困逻辑。

## 17. 测试计划

### 17.1 单元测试

至少增加：

- 掩膜在左侧时输出左转；
- 掩膜在右侧时输出右转；
- 直线路径输出接近零偏移；
- 弯道使用局部切线而不是远端包围框中心；
- 多个实例时选择与角色相连的路径；
- 黄色花朵掩膜为空时不接管；
- 模型短暂丢失 2 帧时不抖动回任务图标；
- 长时间丢失时释放 W 并回退；
- 距离倒退和无进展时禁用粒子；
- 取消或异常时释放全部输入。

### 17.2 离线录像回放

为每一帧保存或绘制：

- 模型掩膜；
- 选择的路径实例；
- 中心线；
- 近、中、远控制点；
- 当前前视点；
- 输出偏移；
- 任务图标方向；
- 接管/回退状态；
- 推理耗时。

先在录像上稳定后再进入实机。不能仅凭单张截图测试转弯。

### 17.3 实机测试

分三阶段：

1. 只识别和记录，不发送输入；
2. 允许转动镜头，不按 W；
3. 允许完整粒子跟随，并保留 F10 紧急停止。

每次实机测试必须保存：

- 完整日志；
- 屏幕录像；
- 模型版本；
- 推理设备；
- 游戏分辨率和画质；
- 起始/结束任务距离；
- 是否发生错误接管和卡死。

## 18. 常见问题

### 18.1 验证集指标很高，实机却很差

最常见原因是数据泄漏：同一录像的相邻帧同时进入训练和验证。重新按整段录像/地点拆分。

### 18.2 远处粒子经常漏检

依次尝试：

1. 增加远处粒子训练样本；
2. 保持中央 ROI，避免把分辨率浪费在 HUD；
3. 从 960 提升到 1280；
4. 从 `n` 升级到 `s`；
5. 最后再考虑带小目标检测层的结构。

### 18.3 花朵和灯光仍然误报

- 增加对应地点的空标签负样本；
- 加入按 V 前后的时间验证；
- 提高接管置信度而不是只调分割阈值；
- 检查标注是否把过多背景包进路径带。

### 18.4 掩膜正确但转弯错误

这是后处理问题，不是模型准确率问题。检查路径起点、中心线排序、前视距离和弯道曲率，不应重新训练模型解决所有问题。

### 18.5 推理太慢

- 确认实际启用了 DirectML/CUDA/TensorRT；
- 使用 `yolo11n-seg`；
- 固定 960 输入；
- 降低推理频率而不是降低导航循环频率；
- 只处理中央 ROI；
- 接入稳定后再测试 FP16。

### 18.6 ONNX 在 Python 正常，C# 失败

检查：

- 是否导出为固定输入尺寸；
- opset 是否为 17；
- 是否使用了当前 YoloSharp 不支持的动态输出；
- ONNX 输入/输出名称和 shape；
- 分割原型张量是否被正确读取；
- 模型是否包含 Ultralytics 元数据。

## 19. 里程碑

### M1：数据集可用

- 至少 500 张有效标注；
- 正负样本齐全；
- 按录像拆分 train/val/test；
- 标签可视化检查通过。

### M2：离线模型可用

- Precision ≥ 0.90；
- Recall ≥ 0.80；
- 关键转弯录像可稳定输出掩膜；
- 花朵和 UI 困难负样本不过度误报；
- 导出固定 960 ONNX。

### M3：C# 离线回放可用

- ONNX 推理结果与 Python 基本一致；
- 中心线和转向输出正确；
- 推理耗时达到目标；
- 单元测试和录像回放通过。

### M4：实验实机可用

- 旧版默认行为不变；
- 实验开关可独立启用；
- 任务图标始终负责最终目标；
- 错误识别能自动回退；
- 连续多场景测试无失控输入。

## 20. 下一步所需素材

为了开始第一轮模型训练，需要提供：

1. 10 段以上不同地点的正样本录像；
2. 5 段以上黄色环境干扰负样本；
3. 保留本次喷泉、花丛和墙边错误运行录像；
4. 至少包含直线、左转、右转和楼梯各一段；
5. 录像不要压缩到看不清远处粒子。

素材到位后，先由开发者抽帧并生成一批待标注图片，再进行第一轮标注，不建议使用者提前手工截取大量重复图片。

## 21. 参考资料

- [Ultralytics Instance Segmentation](https://docs.ultralytics.com/tasks/segment/)
- [Ultralytics 模型导出](https://docs.ultralytics.com/modes/export/)
- [YoloSharp：C# ONNX 推理与 Segment 支持](https://github.com/dme-compunet/YoloSharp)
- [CVAT 图像标注文档](https://docs.cvat.ai/docs/manual/advanced/annotation-with-polygons/)
