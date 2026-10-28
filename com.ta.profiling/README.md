# TA Profiling Tools (com.ta.profiling)

面向 **TA** 的性能与渲染调试工具。

菜单入口仅保留：**Tools → TA → Profiling → Performance Panel**

## 分区

| 左侧 | 内容 |
|------|------|
| **监控** | 总览：FPS / CPU / DC / 内存 / GC；可暂停查看、单击采样点；可 **生成/导出时段报告**（均值、1% Low、跳帧） |
| **诊断** | 子页：GC·脚本 · 渲染影响 · 资源开销 · **Shader**（多平台编译指令/字节码） |
| **可视** | 子页：场景着色（12 模式）· 批次 · 视锥/遮挡 |
| **合批** | 按命名前缀 Static / Combine |
| **AI** | 导出上下文 / 分块导出 / 执行回写动作 |

## Shader 平台编译（诊断 → Shader）

对场景（及选中）材质调用 `ShaderUtil.GetShaderData` → `Pass.CompileVariant`，对比：

- **Win D3D / Android Vulkan / GLES3 / iOS Metal**
- **指令数**：D3D 优先用本机 `fxc /dumpbin` 解析；其它平台若编译产物含文本统计则解析，否则看 Bytes / Tex / CB
- 可 **复制 Markdown**，或对选中 Shader **打开 Compiled**（等同 Inspector 编译查看）

需安装 Windows SDK（含 fxc）时，D3D 指令数最准。

Scene 叠加着色同时只启用一种（场景 / 批次 / 剔除互斥），避免抢画频闪。

界面为赛博朋克风（霓虹边框 + Unsplash 贴图，见 `Editor/UI/Textures/CREDITS.md`）。

## AI（面板内）

命名空间：`TA.Profiling.Editor`（与面板同包，无独立菜单项）

| API | 作用 |
|-----|------|
| `ProfilingAiBridge.CollectReport()` | 采集完整快照报告 |
| `ProfilingAiBridge.CollectReport(options, history)` | 按分块选项采集（可含时段曲线） |
| `ProfilingAiBridge.ExportContext()` | 写出 `TAProfiling/ai-context.json` |
| `ProfilingAiBridge.ExportContext(options, history)` | 分块写出 `TAProfiling/ai-context-chunk.json` |
| `ProfilingAiBridge.CopyContextToClipboard(options, history)` | 复制分块 / 全量 JSON |
| `ProfilingAiBridge.ApplyActionsFromJson(json)` | 执行 AI 回写动作 |
| `ProfilingAiBridge.ApplyActionsFromFile()` | 读 `TAProfiling/ai-actions.json` 并执行 |
| `ProfilingAiBridge.EnsureActionsTemplate()` | 创建/打开动作模板 |

**分块导出**：AI 页可勾选 `realtime / render / gc / culling / batch / issues / frameWindow`。  
预设「仅帧率波动」会导出最近 N 秒（或指定采样索引）的 `frameWindow` 汇总 + 降采样时序点，便于分析卡顿。

流程：面板左侧 **AI** → 导出全量或分块 → AI 分析 → 写 actions JSON → 执行动作。
