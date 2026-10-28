# Changelog

本文件遵循 [Keep a Changelog](https://keepachangelog.com/) 与 [SemVer](https://semver.org/)。

## [Unreleased]
### Added
- AI 分块导出：可按块勾选 realtime/render/gc/culling/batch/issues，以及 `frameWindow` 时段曲线（最近秒数或采样索引），便于只分析一段时间内的帧率波动；预设「仅帧率波动 / 仅 GC」。
- 诊断 → **Shader**：多平台 CompileVariant（D3D/Vulkan/GLES3/Metal），提取指令数（D3D+fxc）与字节码/绑定数，支持 Markdown 导出与 Open Compiled。

## [0.1.0] - 2026-09-04
### Added
- TA 性能调试面板：左侧「监控 / 诊断 / 优化」导航 + 顶部常驻状态条。
- AI 作为面板左侧导航页（与监控/诊断/可视/合批并列）：`ProfilingAiBridge` 导出 JSON 并执行回写；菜单仅保留 Performance Panel 入口。
- 场景可视 12 模式：Overdraw、LOD、面密度、纹素密度、Mipmap、阴影级联、Lightmap/Probe、SRP/Instancing、Shader 复杂度、Layer、反射探针、粒子覆盖。
- 视锥体剔除与 Occlusion 可视化（锥内/外着色、遮挡嫌疑、Occluder/Occludee 标记）。
- 实时监控、GC/脚本、渲染影响、资源开销、命名合批、批次可视化。
- 监控曲线支持暂停录制，并单击采样点查看该帧指标、Profiler 脚本/GC 摘要及相对上一采样的变化。
- GC/脚本列表支持「仅 Assets 脚本」过滤，列表项可单击定位。
- 赛博朋克风界面皮肤（Unsplash 霓虹贴图 + 霓虹边框导航/卡片）。
- 时段性能报告：均值 / 1% Low / 跳帧 Hitch 统计，导出 Markdown+JSON 到 `TAProfiling/reports/`。
- 内存大对象与基线泄露点；DrawCall / SetPass 实时曲线。
