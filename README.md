# Unity性能优化插件

面向 TA 的 Unity 编辑器性能与渲染调试工具。适用于 **Unity 2022.3** 及以上。

仓库里有两个包，需要一起导入：

- `com.ta.profiling`：性能面板
- `com.ta.editorcore`：面板依赖的编辑器基础库

菜单入口：**Tools → TA → Profiling → Performance Panel**

## 一、导入插件

1. 打开项目。
2. 在 Project 窗口里右键 `Packages`，选择 **Show in Explorer**。
3. 打开 `Packages` 文件夹里的 `manifest.json`。
4. 在 `dependencies` 里加上这两行。路径改成这两个文件夹在你电脑上的实际位置（把本仓库克隆下来后，指向里面的 `com.ta.profiling` 和 `com.ta.editorcore`）：

```json
"com.ta.profiling": "file:D:/你的路径/com.ta.profiling",
"com.ta.editorcore": "file:D:/你的路径/com.ta.editorcore"
```

5. 保存后回到 Unity，等待加载完成。

## 二、插件说明

导入成功后，菜单栏会出现 **Tools**，按上面的路径打开面板。

![菜单入口](docs/images/menu.png)

界面一共五个页：**监控**、**诊断**、**可视**、**合批**、**AI**。

监控和诊断需要进入 Play 模式才会实时演算。可视化不需要运行。

## 三、使用说明

### 监控

监控页可以看见实时的 FPS、CPU、Draw Call、Pass、内存等数据。

![监控页](docs/images/monitor.png)

可以暂停后点采样点，查看每一帧对应的数据。

![查看单帧数据](docs/images/monitor-frame.gif)

也可以点内存，查找当前占用最大的对象，并自动排查、定位。

这些数据比较散。如果想看某一段时间里的帧均值和跳帧，用下面的 **生成报告** 和 **导出报告**。

[生成报告和导出报告的演示](docs/report-demo.mp4)

点击生成和导出后，会在项目下的 `TAProfiling/reports` 里生成两个文件。

![报告文件](docs/images/reports.png)

`.md` 是数据报告，双击即可打开查看。

### 诊断

可以点左侧 **诊断**，也可以在监控页点 GC 跳转过来。

![诊断页](docs/images/diagnose.png)

#### GC / 脚本

默认勾选「仅 Assets 脚本」。关掉之后会看到整个 Unity 里正在跑的脚本。

![GC 与脚本](docs/images/gc.png)

单击列表里的脚本，可以定位到该脚本。

#### 渲染影响

点上方 **渲染影响** 进入。这里会列出光照、阴影、后处理、反射探针等信息，单击可以在场景里定位。

![渲染影响](docs/images/render.png)

#### 资源开销

这里显示场景里的资源开销。可以按材质、贴图、灯光等分类查看。

![资源开销](docs/images/resources.png)

右上角可以按评分排序，方便先看开销大的项。单击条目可以在场景里定位，用来找造成性能问题的资源。

#### Shader

查看 Shader 编译后，在不同平台上的指令数。

![Shader 指令](docs/images/shader.png)

### 可视

可视化不用运行，主要是编辑场景时查看信息。

![可视页](docs/images/visual.png)

#### 场景着色

按需要选一种模式，再点着色，结果会画在 Scene 视图里。

![场景着色](docs/images/scene-color.png)

下面是 Overdraw（叠画）。越红，填充越重。每种模式的具体含义看面板里的提示。

![Overdraw 提示](docs/images/overdraw-hint.png)

#### 批次

开启着色后可以看不同批次。可以换分组方式查看，也可以只画网格或包围盒。点物体旁边的小圆点可以隐藏该合批。

![批次着色](docs/images/batch.png)

![批次分组方式](docs/images/batch-types.png)

#### 视锥 / 遮挡

先选要测试的相机。

![选择相机](docs/images/culling.png)

再选是看视锥，还是看遮挡剔除。

![剔除方式](docs/images/culling-mode.png)

扫描之后，视锥体外的物体会标成红色。

![视锥扫描](docs/images/culling-scan.gif)

下方列表会写出物体是在锥外、锥内，还是其他状态。

![剔除数据](docs/images/culling-data.png)

### 合批

自己定一个命名前缀，把场景里要合批的物体名字开头改成这个前缀，然后点查找，就可以一键合批。

![合批](docs/images/combine.png)

![按前缀查找](docs/images/combine-find.png)

### AI

可以导出全部数据，也可以只导出某一类数据，交给 AI 做整理和分析。

![AI 页](docs/images/ai.png)

点 **打开动作模板** 或 **打开上下文文件**，会在项目里看到这两个文件：

![AI 导出文件](docs/images/ai-files.png)

- `ai-context.json`：当前性能上下文，给 AI 分析
- `ai-actions.json`：分析后要执行的动作

分析结束后会生成动作文件。这里是给 Cursor 分析用的。

![交给 Cursor 分析](docs/images/ai-cursor.png)
