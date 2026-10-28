using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TA.Profiling.Editor
{
    /// <summary>
    /// TA Profiling → AI 接入桥。
    /// 采集报告、导出 JSON、执行 AI 回写的优化动作。
    /// <para>
    /// 典型流程：ExportContext() → AI 阅读 JSON → ApplyActionsFromJson(actionsJson)
    /// </para>
    /// </summary>
    public static class ProfilingAiBridge
    {
        public const string SchemaVersion = "1.1";
        public const string DefaultRelativePath = "TAProfiling/ai-context.json";
        public const string ChunkRelativePath = "TAProfiling/ai-context-chunk.json";
        public const string ActionsRelativePath = "TAProfiling/ai-actions.json";

        public static string GetAbsolutePath(string relativePath)
        {
            string rel = string.IsNullOrEmpty(relativePath) ? DefaultRelativePath : relativePath;
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", rel));
        }

        /// <summary>采集完整 AI 上下文（兼容旧调用）。</summary>
        public static ProfilingAiReport CollectReport()
        {
            return CollectReport(AiExportOptions.Full(), null);
        }

        /// <summary>按分块选项采集；传入 history 时可导出时段曲线。</summary>
        public static ProfilingAiReport CollectReport(AiExportOptions options, MetricFrameHistory history)
        {
            options = options ?? AiExportOptions.Full();
            var report = new ProfilingAiReport
            {
                schemaVersion = SchemaVersion,
                generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                unityVersion = Application.unityVersion,
                sceneName = SceneManager.GetActiveScene().name,
                isPlaying = EditorApplication.isPlaying,
                exportMode = options.IsFullSnapshot ? "full" : "chunk",
                guidance = BuildGuidance(options)
            };

            if (options.Has(AiExportSection.Realtime))
            {
                FillRealtime(report);
                report.includedSections.Add("realtime");
            }

            if (options.Has(AiExportSection.Render))
            {
                FillRender(report);
                report.includedSections.Add("render");
            }

            if (options.Has(AiExportSection.Gc))
            {
                FillGc(report);
                report.includedSections.Add("gc");
            }

            if (options.Has(AiExportSection.Culling))
            {
                FillCulling(report);
                report.includedSections.Add("culling");
            }

            if (options.Has(AiExportSection.Batch))
            {
                FillBatch(report);
                report.includedSections.Add("batch");
            }

            if (options.Has(AiExportSection.FrameWindow))
            {
                FillFrameWindow(report, history, options);
                report.includedSections.Add("frameWindow");
            }

            if (options.Has(AiExportSection.Issues))
            {
                BuildIssues(report);
                report.includedSections.Add("issues");
            }

            if (options.Has(AiExportSection.AvailableActions))
            {
                FillAvailableActions(report);
                report.includedSections.Add("availableActions");
            }

            return report;
        }

        /// <summary>导出到默认路径，返回绝对路径。</summary>
        public static string ExportContext(string relativePath = null)
        {
            return ExportContext(AiExportOptions.Full(), null, relativePath ?? DefaultRelativePath);
        }

        public static string ExportContext(
            AiExportOptions options,
            MetricFrameHistory history,
            string relativePath = null)
        {
            options = options ?? AiExportOptions.Full();
            bool chunk = !options.IsFullSnapshot;
            string rel = string.IsNullOrEmpty(relativePath)
                ? (chunk ? ChunkRelativePath : DefaultRelativePath)
                : relativePath;
            string abs = GetAbsolutePath(rel);
            string dir = Path.GetDirectoryName(abs);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var report = CollectReport(options, history);
            string json = JsonUtility.ToJson(report, true);
            File.WriteAllText(abs, json, Encoding.UTF8);
            AssetDatabase.Refresh();
            Debug.Log($"[TA.Profiling] 已导出 AI 上下文（{report.exportMode}）：{abs}");
            return abs;
        }

        public static string ToJson(ProfilingAiReport report, bool pretty = true)
        {
            return JsonUtility.ToJson(report ?? CollectReport(), pretty);
        }

        public static void CopyContextToClipboard()
        {
            CopyContextToClipboard(AiExportOptions.Full(), null);
        }

        public static void CopyContextToClipboard(AiExportOptions options, MetricFrameHistory history)
        {
            var json = ToJson(CollectReport(options, history));
            EditorGUIUtility.systemCopyBuffer = json;
            Debug.Log("[TA.Profiling] 上下文已复制到剪贴板。");
        }

        private static string BuildGuidance(AiExportOptions options)
        {
            bool fpsFocus = options.Focus == "fps_fluctuation"
                            && options.Has(AiExportSection.FrameWindow)
                            && !options.Has(AiExportSection.Culling)
                            && !options.Has(AiExportSection.Batch);

            if (fpsFocus)
            {
                return
                    "你是 Unity TA 性能助手。本次导出聚焦「时段帧率波动」。" +
                    "请依据 frameWindow.summary / fpsP1Low / stutterRatePct 与 samples 时序，" +
                    "指出卡顿区间、可能原因（CPU/DC/GC）与可执行优化建议。" +
                    "若有 availableActions，可用其 type 生成 ProfilingAiActionBatch JSON。";
            }

            if (options.Sections != AiExportSection.Snapshot && options.Sections != AiExportSection.All)
            {
                return
                    "你是 Unity TA 性能助手。本次为分块导出，仅 includedSections 中的字段有效，" +
                    "请勿假设未包含块存在完整数据。根据已有指标给出可执行建议；" +
                    "若有 availableActions，用其 type 生成 ProfilingAiActionBatch JSON 写入 TAProfiling/ai-actions.json。";
            }

            return
                "你是 Unity TA 性能助手。根据 issues 与指标给出可执行优化建议，" +
                "并用 availableActions 中的 type 生成 ProfilingAiActionBatch JSON，" +
                "写入工程根目录 TAProfiling/ai-actions.json 后执行菜单 Apply，或调用 ApplyActionsFromJson。";
        }

        /// <summary>确保工程根目录存在 ai-actions.json 模板，并在资源管理器中打开。</summary>
        public static string EnsureActionsTemplate()
        {
            string abs = GetAbsolutePath(ActionsRelativePath);
            string dir = Path.GetDirectoryName(abs);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(abs))
            {
                var batch = new ProfilingAiActionBatch();
                batch.actions.Add(new ProfilingAiActionRequest
                {
                    type = "MarkBatchingStaticByPrefix",
                    namePrefix = "P_",
                    enable = true,
                    note = "示例：给 P_ 前缀物体开 Batching Static"
                });
                File.WriteAllText(abs, JsonUtility.ToJson(batch, true), Encoding.UTF8);
            }

            EditorUtility.RevealInFinder(abs);
            return abs;
        }
        /// <summary>从默认 actions 文件执行 AI 动作。</summary>
        public static List<ProfilingAiActionResult> ApplyActionsFromFile(string relativePath = null)
        {
            string rel = string.IsNullOrEmpty(relativePath) ? ActionsRelativePath : relativePath;
            string abs = GetAbsolutePath(rel);
            if (!File.Exists(abs))
            {
                Debug.LogWarning($"[TA.Profiling] 未找到动作文件：{abs}");
                return new List<ProfilingAiActionResult>
                {
                    new ProfilingAiActionResult { ok = false, message = "文件不存在: " + abs }
                };
            }

            string json = File.ReadAllText(abs, Encoding.UTF8);
            return ApplyActionsFromJson(json);
        }

        public static List<ProfilingAiActionResult> ApplyActionsFromJson(string json)
        {
            var results = new List<ProfilingAiActionResult>();
            if (string.IsNullOrWhiteSpace(json))
            {
                results.Add(new ProfilingAiActionResult { ok = false, message = "空 JSON" });
                return results;
            }

            ProfilingAiActionBatch batch;
            try
            {
                batch = JsonUtility.FromJson<ProfilingAiActionBatch>(json);
            }
            catch (Exception ex)
            {
                results.Add(new ProfilingAiActionResult { ok = false, message = "JSON 解析失败: " + ex.Message });
                return results;
            }

            if (batch?.actions == null || batch.actions.Count == 0)
            {
                results.Add(new ProfilingAiActionResult { ok = false, message = "无 actions" });
                return results;
            }

            foreach (var action in batch.actions)
                results.Add(ApplyOne(action));

            return results;
        }

        public static ProfilingAiActionResult ApplyOne(ProfilingAiActionRequest action)
        {
            if (action == null || string.IsNullOrEmpty(action.type))
                return new ProfilingAiActionResult { ok = false, message = "无效动作" };

            try
            {
                switch (action.type)
                {
                    case "MarkBatchingStaticByPrefix":
                        return MarkBatchingByPrefix(action);
                    case "MarkOcclusionStaticByPrefix":
                        return MarkOcclusionByPrefix(action);
                    case "SetCameraOcclusion":
                        return SetCameraOcclusion(action);
                    case "SelectByPrefix":
                        return SelectByPrefix(action);
                    case "RescanAll":
                        BatchVisualization.Rescan();
                        CullingVisualization.Rescan();
                        SceneDebugVisualization.Rescan();
                        return new ProfilingAiActionResult { ok = true, message = "已重新扫描可视化数据" };
                    case "ExportContext":
                        string path = ExportContext();
                        return new ProfilingAiActionResult { ok = true, message = "已导出: " + path };
                    default:
                        return new ProfilingAiActionResult
                        {
                            ok = false,
                            message = "未知动作类型: " + action.type
                        };
                }
            }
            catch (Exception ex)
            {
                return new ProfilingAiActionResult { ok = false, message = ex.Message };
            }
        }

        // ───────── fill ─────────

        private static void FillFrameWindow(
            ProfilingAiReport report,
            MetricFrameHistory history,
            AiExportOptions options)
        {
            var win = report.frameWindow ?? (report.frameWindow = new ProfilingAiFrameWindow());
            win.focus = string.IsNullOrEmpty(options.Focus) ? "fps_fluctuation" : options.Focus;
            win.targetFps = Mathf.Max(1f, options.TargetFps);
            win.frameBudgetMs = 1000f / win.targetFps;
            win.samples.Clear();

            int filled = history != null ? history.Filled : 0;
            if (filled <= 0)
            {
                win.summary = "无采样数据。请先在「监控」页 Play 录制一段时间。";
                return;
            }

            ResolveWindowRange(history, options, filled, out int start, out int end);
            start = Mathf.Clamp(start, 0, filled - 1);
            end = Mathf.Clamp(end, start, filled - 1);
            win.startIndex = start;
            win.endIndex = end;

            int count = end - start + 1;
            win.sampleCount = count;

            var fpsList = new List<float>(count);
            double t0 = 0, t1 = 0;
            double sumFps = 0, sumFrame = 0, sumCpu = 0, sumDc = 0, sumAlloc = 0;
            float minFps = float.MaxValue, maxFps = 0, maxFrame = 0, maxCpu = 0, maxDc = 0, maxAlloc = 0;
            int stutter = 0, hitch = 0;
            float budget = win.frameBudgetMs;
            float hitchBudget = budget * 2f;

            for (int i = start; i <= end; i++)
            {
                if (!history.TryGetChronological(i, out var s)) continue;
                if (i == start) t0 = s.TimeSinceStartup;
                t1 = s.TimeSinceStartup;

                fpsList.Add(s.Fps);
                sumFps += s.Fps;
                sumFrame += s.FrameMs;
                sumCpu += s.CpuMainThreadMs;
                sumDc += s.DrawCalls;
                sumAlloc += s.GcAllocBytes;

                if (s.Fps < minFps) minFps = s.Fps;
                if (s.Fps > maxFps) maxFps = s.Fps;
                if (s.FrameMs > maxFrame) maxFrame = s.FrameMs;
                if (s.CpuMainThreadMs > maxCpu) maxCpu = s.CpuMainThreadMs;
                if (s.DrawCalls > maxDc) maxDc = s.DrawCalls;
                if (s.GcAllocBytes > maxAlloc) maxAlloc = s.GcAllocBytes;
                if (s.FrameMs > budget) stutter++;
                if (s.FrameMs > hitchBudget) hitch++;
            }

            win.durationSec = (float)Math.Max(0, t1 - t0);
            float inv = 1f / count;
            win.fpsAvg = (float)(sumFps * inv);
            win.fpsMin = minFps < float.MaxValue ? minFps : 0f;
            win.fpsMax = maxFps;
            win.fpsP1Low = PercentileLow(fpsList, 0.01f);
            win.fpsP5Low = PercentileLow(fpsList, 0.05f);
            win.frameMsAvg = (float)(sumFrame * inv);
            win.frameMsMax = maxFrame;
            win.cpuMsAvg = (float)(sumCpu * inv);
            win.cpuMsMax = maxCpu;
            win.stutterCount = stutter;
            win.stutterRatePct = stutter * 100f / count;
            win.hitchCount = hitch;
            win.drawCallAvg = (float)(sumDc * inv);
            win.drawCallMax = maxDc;
            win.gcAllocAvg = (float)(sumAlloc * inv);
            win.gcAllocMax = maxAlloc;
            win.summary =
                $"窗口 {win.durationSec:F1}s · 采样 {count} · FPS 均值 {win.fpsAvg:F1}" +
                $"（1% Low {win.fpsP1Low:F1}）· 跳帧 {stutter}（{win.stutterRatePct:F1}%）· Hitch {hitch}";

            if (options.IncludeFrameSamples)
            {
                int maxPts = Mathf.Clamp(options.MaxFrameSamples, 8, 480);
                int step = Mathf.Max(1, Mathf.CeilToInt(count / (float)maxPts));
                for (int i = start; i <= end; i += step)
                {
                    if (!history.TryGetChronological(i, out var s)) continue;
                    win.samples.Add(new ProfilingAiFramePoint
                    {
                        t = (float)(s.TimeSinceStartup - t0),
                        fps = s.Fps,
                        frameMs = s.FrameMs,
                        cpuMs = s.CpuMainThreadMs,
                        drawCalls = s.DrawCalls,
                        setPass = s.SetPassCalls,
                        gcAlloc = s.GcAllocBytes
                    });
                }

                // 保证包含终点
                if (end > start && (end - start) % step != 0
                    && history.TryGetChronological(end, out var last))
                {
                    win.samples.Add(new ProfilingAiFramePoint
                    {
                        t = (float)(last.TimeSinceStartup - t0),
                        fps = last.Fps,
                        frameMs = last.FrameMs,
                        cpuMs = last.CpuMainThreadMs,
                        drawCalls = last.DrawCalls,
                        setPass = last.SetPassCalls,
                        gcAlloc = last.GcAllocBytes
                    });
                }
            }

            // 时段相关 issue 在 BuildIssues 中根据 frameWindow 追加
        }

        private static void ResolveWindowRange(
            MetricFrameHistory history,
            AiExportOptions options,
            int filled,
            out int start,
            out int end)
        {
            if (options.StartIndex >= 0 && options.EndIndex >= 0)
            {
                start = options.StartIndex;
                end = options.EndIndex;
                return;
            }

            end = filled - 1;
            start = 0;
            float seconds = options.WindowSeconds;
            if (seconds <= 0f)
                return;

            if (!history.TryGetChronological(end, out var last))
                return;

            double cutoff = last.TimeSinceStartup - seconds;
            start = end;
            for (int i = end; i >= 0; i--)
            {
                if (!history.TryGetChronological(i, out var s)) continue;
                if (s.TimeSinceStartup < cutoff) break;
                start = i;
            }
        }

        private static float PercentileLow(List<float> values, float ratio)
        {
            if (values == null || values.Count == 0) return 0f;
            var copy = new List<float>(values);
            copy.Sort();
            int count = Mathf.Max(1, Mathf.CeilToInt(copy.Count * ratio));
            double sum = 0;
            for (int i = 0; i < count; i++)
                sum += copy[i];
            return (float)(sum / count);
        }

        private static void FillRealtime(ProfilingAiReport report)
        {
            if (!EditorApplication.isPlaying) return;
            // 临时采样器
            using (var metrics = new PerformanceMetrics(0.2f))
            {
                for (int i = 0; i < 3; i++)
                    metrics.Tick(0.05f);
                var s = metrics.Latest;
                report.realtime.fps = s.Fps;
                report.realtime.frameMs = s.FrameMs;
                report.realtime.cpuMainThreadMs = s.CpuMainThreadMs;
                report.realtime.gpuFrameMs = s.GpuFrameMs;
                report.realtime.hasGpuTiming = s.HasGpuTiming;
                report.realtime.totalAllocBytes = s.TotalAllocatedBytes;
                report.realtime.monoUsedBytes = s.MonoUsedBytes;
                report.realtime.monoHeapBytes = s.MonoHeapBytes;
                report.realtime.textureBytes = s.TextureBytes;
                report.realtime.meshBytes = s.MeshBytes;
                report.realtime.graphicsDriverBytes = s.GraphicsDriverBytes;
                report.gc.gen0 = s.GcGen0;
                report.gc.gen1 = s.GcGen1;
                report.gc.gen2 = s.GcGen2;
            }
        }

        private static void FillRender(ProfilingAiReport report)
        {
            try
            {
                report.render.drawCalls = UnityStats.drawCalls;
                report.render.setPassCalls = UnityStats.setPassCalls;
                report.render.batches = UnityStats.batches;
                report.render.triangles = UnityStats.triangles;
                report.render.vertices = UnityStats.vertices;
            }
            catch { /* ignore */ }

            var impact = RenderImpactAnalyzer.Scan();
            report.render.realtimeLights = impact.RealtimeLights;
            report.render.shadowLights = impact.ShadowCastingLights;
            report.render.activePostVolumes = impact.ActivePostVolumes;
            report.render.postEffects.AddRange(impact.PostEffects);
            int n = 0;
            foreach (var L in impact.Lights)
            {
                if (n++ >= 8) break;
                report.render.topLights.Add(new ProfilingAiNamedScore
                {
                    name = L.Name,
                    detail = L.Impact,
                    score = L.Score
                });
            }
        }

        private static void FillGc(ProfilingAiReport report)
        {
            using (var gc = new GcScriptProfiler())
            {
                for (int i = 0; i < 5; i++)
                    gc.Tick(0.05f);
                gc.ForceSampleHierarchy();
                report.gc.collectsPerSecond = gc.CollectsPerSecond;
                report.gc.allocBytesPerFrame = gc.AllocBytesPerFrame;
                var snap = gc.Latest;
                foreach (var s in snap.TopByTime)
                {
                    report.gc.topScriptsByTime.Add(new ProfilingAiScriptSample
                    {
                        name = s.Name, selfMs = s.SelfMs, gcBytes = s.GcBytes, calls = s.Calls
                    });
                }

                foreach (var s in snap.TopByGc)
                {
                    report.gc.topScriptsByGc.Add(new ProfilingAiScriptSample
                    {
                        name = s.Name, selfMs = s.SelfMs, gcBytes = s.GcBytes, calls = s.Calls
                    });
                }
            }
        }

        private static void FillCulling(ProfilingAiReport report)
        {
            var cull = CullingVisualization.Rescan();
            report.culling.cameraName = cull.CameraName;
            report.culling.cameraOcclusionEnabled = cull.CameraOcclusionEnabled;
            report.culling.hasUmbraData = cull.HasOcclusionData;
            report.culling.inFrustum = cull.InFrustum;
            report.culling.outFrustum = cull.OutFrustum;
            report.culling.visible = cull.Visible;
            report.culling.likelyOccluded = cull.LikelyOccluded;
            report.culling.occluderStatic = cull.OccluderStaticCount;
            report.culling.occludeeStatic = cull.OccludeeStaticCount;

            int a = 0, b = 0, c = 0;
            foreach (var e in cull.Entries)
            {
                if (!e.InFrustum && a < 10)
                {
                    report.culling.topOutFrustum.Add(e.Path);
                    a++;
                }

                if (e.LikelyOccluded && b < 10)
                {
                    report.culling.topLikelyOccluded.Add(e.Path);
                    b++;
                }

                if (e.InFrustum && !e.IsOccludeeStatic && c < 10)
                {
                    report.culling.missingOccludee.Add(e.Path);
                    c++;
                }
            }
        }

        private static void FillBatch(ProfilingAiReport report)
        {
            BatchVisualization.Rescan();
            report.batch.groupCount = BatchVisualization.CurrentGroups.Count;
            report.batch.multiBatchGroups = BatchVisualization.MultiBatchCount;
            report.batch.singleGroups = BatchVisualization.SingleCount;
            report.batch.rendererCount = BatchVisualization.RendererCount;
            int n = 0;
            foreach (var g in BatchVisualization.CurrentGroups)
            {
                if (n++ >= 12) break;
                report.batch.topGroups.Add(new ProfilingAiBatchGroup
                {
                    label = g.Label,
                    count = g.Count,
                    canBatch = g.CanBatch
                });
            }
        }

        private static void BuildIssues(ProfilingAiReport report)
        {
            void Add(string id, string sev, string cat, string title, string detail, string action = null, string payload = null)
            {
                report.issues.Add(new ProfilingAiIssue
                {
                    id = id,
                    severity = sev,
                    category = cat,
                    title = title,
                    detail = detail,
                    suggestedActionType = action,
                    suggestedPayload = payload
                });
            }

            if (report.isPlaying && report.realtime.fps > 0 && report.realtime.fps < 30f)
                Add("fps_low", "error", "realtime", "帧率过低",
                    $"FPS={report.realtime.fps:F1}, Frame={report.realtime.frameMs:F1}ms");

            if (report.render.drawCalls > 500)
                Add("dc_high", "warning", "render", "DrawCall 偏高",
                    $"DrawCalls={report.render.drawCalls}, SetPass={report.render.setPassCalls}",
                    "MarkBatchingStaticByPrefix", "{\"type\":\"MarkBatchingStaticByPrefix\",\"namePrefix\":\"P_\",\"enable\":true}");

            if (report.batch.singleGroups > report.batch.multiBatchGroups && report.batch.groupCount > 0)
                Add("batch_fragmented", "warning", "batch", "合批碎片多",
                    $"可合批组 {report.batch.multiBatchGroups}，单件组 {report.batch.singleGroups}",
                    "MarkBatchingStaticByPrefix", null);

            if (report.render.shadowLights > 3)
                Add("shadow_lights", "warning", "render", "实时投影灯过多",
                    $"投影灯 {report.render.shadowLights}，建议减少 Soft Shadow 或改 Mixed/Baked");

            if (report.gc.collectsPerSecond > 2f)
                Add("gc_freq", "warning", "gc", "GC 触发频繁",
                    $"约 {report.gc.collectsPerSecond:F2} 次/秒，Alloc≈{report.gc.allocBytesPerFrame:F0} B/帧");

            if (report.gc.topScriptsByGc.Count > 0)
            {
                var top = report.gc.topScriptsByGc[0];
                Add("gc_hotspot", "warning", "gc", "高频 GC 源头",
                    $"{top.name} Alloc={PerformanceMetrics.FormatBytes(top.gcBytes)}");
            }

            if (!report.culling.cameraOcclusionEnabled)
                Add("occ_off", "info", "culling", "相机未开 Occlusion Culling",
                    "可开启 Camera.useOcclusionCulling（需 Umbra 数据）",
                    "SetCameraOcclusion", "{\"type\":\"SetCameraOcclusion\",\"enable\":true}");

            if (report.culling.hasUmbraData == false)
                Add("umbra_missing", "info", "culling", "场景无 Occlusion Bake 数据",
                    "请用 Window/Rendering/Occlusion Culling 烘焙");

            if (report.culling.missingOccludee.Count > 5)
                Add("occludee_missing", "info", "culling", "大量物体未标 Occludee Static",
                    $"示例: {report.culling.missingOccludee[0]}",
                    "MarkOcclusionStaticByPrefix",
                    "{\"type\":\"MarkOcclusionStaticByPrefix\",\"namePrefix\":\"P_\",\"occludee\":true,\"enable\":true}");

            var win = report.frameWindow;
            if (win != null && win.sampleCount > 0)
            {
                if (win.stutterRatePct >= 15f)
                    Add("window_stutter",
                        win.stutterRatePct >= 30f ? "error" : "warning",
                        "frameWindow", "时段内跳帧偏多", win.summary);
                else if (win.fpsP1Low > 0f && win.fpsP1Low < win.targetFps * 0.5f)
                    Add("window_fps_1pct", "warning", "frameWindow", "1% Low 帧率过低",
                        $"1% Low={win.fpsP1Low:F1}，目标 {win.targetFps:F0}；窗口 {win.durationSec:F1}s");
            }
        }

        private static void FillAvailableActions(ProfilingAiReport report)
        {
            report.availableActions.Add(new ProfilingAiActionDef
            {
                type = "MarkBatchingStaticByPrefix",
                description = "按名称前缀批量标记/清除 Batching Static",
                payloadExample = "{\"type\":\"MarkBatchingStaticByPrefix\",\"namePrefix\":\"P_\",\"enable\":true}"
            });
            report.availableActions.Add(new ProfilingAiActionDef
            {
                type = "MarkOcclusionStaticByPrefix",
                description = "按名称前缀标记 Occluder/Occludee Static",
                payloadExample = "{\"type\":\"MarkOcclusionStaticByPrefix\",\"namePrefix\":\"P_\",\"occluder\":false,\"occludee\":true,\"enable\":true}"
            });
            report.availableActions.Add(new ProfilingAiActionDef
            {
                type = "SetCameraOcclusion",
                description = "开关主相机或指定相机的 Occlusion Culling",
                payloadExample = "{\"type\":\"SetCameraOcclusion\",\"cameraName\":\"Main Camera\",\"enable\":true}"
            });
            report.availableActions.Add(new ProfilingAiActionDef
            {
                type = "SelectByPrefix",
                description = "按前缀选中场景物体，便于人工检查",
                payloadExample = "{\"type\":\"SelectByPrefix\",\"namePrefix\":\"P_\"}"
            });
            report.availableActions.Add(new ProfilingAiActionDef
            {
                type = "RescanAll",
                description = "重新扫描批次/剔除/场景可视化数据",
                payloadExample = "{\"type\":\"RescanAll\"}"
            });
            report.availableActions.Add(new ProfilingAiActionDef
            {
                type = "ExportContext",
                description = "再次导出 AI 上下文 JSON",
                payloadExample = "{\"type\":\"ExportContext\"}"
            });
        }

        // ───────── actions ─────────

        private static ProfilingAiActionResult MarkBatchingByPrefix(ProfilingAiActionRequest a)
        {
            string prefix = string.IsNullOrEmpty(a.namePrefix) ? "P_" : a.namePrefix;
            var matches = NamePrefixBatchProcessor.FindMatches(prefix, NameMatchMode.Prefix, true, true, false);
            var r = NamePrefixBatchProcessor.SetBatchingStatic(matches, a.enable);
            return new ProfilingAiActionResult { ok = true, affected = r.Affected, message = r.Message };
        }

        private static ProfilingAiActionResult MarkOcclusionByPrefix(ProfilingAiActionRequest a)
        {
            string prefix = string.IsNullOrEmpty(a.namePrefix) ? "P_" : a.namePrefix;
            var cull = CullingVisualization.Rescan();
            var list = new List<CullingEntry>();
            foreach (var e in cull.Entries)
            {
                if (e.GameObject == null) continue;
                if (!e.GameObject.name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                list.Add(e);
            }

            bool occ = a.occluder || a.occludee;
            if (!occ) a.occludee = true;
            int n = CullingVisualization.SetOcclusionStatic(list, a.occluder, a.occludee || !a.occluder, a.enable);
            return new ProfilingAiActionResult
            {
                ok = true,
                affected = n,
                message = $"Occlusion Static 影响 {n} 个（前缀 {prefix}）"
            };
        }

        private static ProfilingAiActionResult SetCameraOcclusion(ProfilingAiActionRequest a)
        {
            Camera cam = null;
            if (!string.IsNullOrEmpty(a.cameraName))
            {
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>())
                {
                    if (c != null && c.name == a.cameraName) { cam = c; break; }
                }
            }

            if (cam == null) cam = Camera.main != null ? Camera.main : CullingVisualization.ResolveCamera();
            if (cam == null)
                return new ProfilingAiActionResult { ok = false, message = "未找到相机" };
            CullingVisualization.SetCameraOcclusion(cam, a.enable);
            return new ProfilingAiActionResult
            {
                ok = true,
                affected = 1,
                message = $"{cam.name}.useOcclusionCulling = {a.enable}"
            };
        }

        private static ProfilingAiActionResult SelectByPrefix(ProfilingAiActionRequest a)
        {
            string prefix = string.IsNullOrEmpty(a.namePrefix) ? "P_" : a.namePrefix;
            var matches = NamePrefixBatchProcessor.FindMatches(prefix, NameMatchMode.Prefix, true, false, false);
            NamePrefixBatchProcessor.SelectMatches(matches);
            return new ProfilingAiActionResult
            {
                ok = true,
                affected = matches.Count,
                message = $"已选中前缀 {prefix} 共 {matches.Count} 个"
            };
        }
    }
}
