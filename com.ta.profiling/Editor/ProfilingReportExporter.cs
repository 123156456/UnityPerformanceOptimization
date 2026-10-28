using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TA.Profiling.Editor
{
    /// <summary>一段时间监控采样的汇总报告（均值 / 跳帧 / 尖峰等）。</summary>
    [Serializable]
    public sealed class ProfilingSessionReport
    {
        public string schemaVersion = "1.0";
        public string generatedAt;
        public string sceneName;
        public string unityVersion;
        public bool isPlaying;
        public float sampleIntervalHint;
        public float durationSec;
        public int sampleCount;

        public float targetFps = 60f;
        public float frameBudgetMs = 16.67f;

        public float fpsAvg;
        public float fpsMin;
        public float fpsMax;
        public float fpsP1Low;
        public float fpsP5Low;

        public float frameMsAvg;
        public float frameMsMin;
        public float frameMsMax;
        public float cpuMsAvg;
        public float cpuMsMax;
        public float gpuMsAvg;
        public float gpuMsMax;
        public bool hasGpuTiming;

        public int stutterCount;
        public float stutterRatePct;
        public int hitchCount;
        public float hitchRatePct;
        public int severeHitchCount;

        public float drawCallAvg;
        public float drawCallMax;
        public float setPassAvg;
        public float setPassMax;
        public float batchesAvg;
        public float trisAvg;
        public float vertsAvg;

        public float memAvgMb;
        public float memMaxMb;
        public float monoAvgMb;
        public float textureAvgMb;
        public float gcAllocAvg;
        public float gcAllocMax;
        public float gcCollectSum;

        public string summary;
        public string markdownPreview;
    }

    /// <summary>从环形历史生成报告，并导出 Markdown / JSON。</summary>
    public static class ProfilingReportExporter
    {
        public const string ReportsRelativeDir = "TAProfiling/reports";

        public static string GetReportsDirectory()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ReportsRelativeDir));
        }

        public static ProfilingSessionReport Build(
            MetricFrameHistory history,
            float targetFps = 60f,
            float sampleIntervalHint = 0.25f)
        {
            var report = new ProfilingSessionReport
            {
                generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                sceneName = SceneManager.GetActiveScene().name,
                unityVersion = Application.unityVersion,
                isPlaying = EditorApplication.isPlaying,
                sampleIntervalHint = sampleIntervalHint,
                targetFps = Mathf.Max(1f, targetFps),
                frameBudgetMs = 1000f / Mathf.Max(1f, targetFps)
            };

            int n = history != null ? history.Filled : 0;
            report.sampleCount = n;
            if (n <= 0)
            {
                report.summary = "无采样数据。请先进入 Play 并录制一段时间。";
                report.markdownPreview = report.summary;
                return report;
            }

            var fpsList = new List<float>(n);
            double t0 = 0, t1 = 0;
            bool hasGpu = false;
            double sumFps = 0, sumFrame = 0, sumCpu = 0, sumGpu = 0;
            double sumDc = 0, sumSp = 0, sumBatch = 0, sumTris = 0, sumVerts = 0;
            double sumMem = 0, sumMono = 0, sumTex = 0, sumAlloc = 0, sumCollect = 0;
            float minFps = float.MaxValue, maxFps = 0;
            float minFrame = float.MaxValue, maxFrame = 0;
            float maxCpu = 0, maxGpu = 0;
            float maxDc = 0, maxSp = 0, maxMem = 0, maxAlloc = 0;
            int stutter = 0, hitch = 0, severe = 0;
            float budget = report.frameBudgetMs;
            float hitchBudget = budget * 2f;
            float severeBudget = Mathf.Max(50f, budget * 3f);

            for (int i = 0; i < n; i++)
            {
                if (!history.TryGetChronological(i, out var s)) continue;
                if (i == 0) t0 = s.TimeSinceStartup;
                t1 = s.TimeSinceStartup;

                fpsList.Add(s.Fps);
                sumFps += s.Fps;
                sumFrame += s.FrameMs;
                sumCpu += s.CpuMainThreadMs;
                if (s.HasGpuTiming)
                {
                    hasGpu = true;
                    sumGpu += s.GpuFrameMs;
                    if (s.GpuFrameMs > maxGpu) maxGpu = s.GpuFrameMs;
                }

                sumDc += s.DrawCalls;
                sumSp += s.SetPassCalls;
                sumBatch += s.Batches;
                sumTris += s.Triangles;
                sumVerts += s.Vertices;
                float memMb = s.TotalAllocatedBytes / (1024f * 1024f);
                sumMem += memMb;
                sumMono += s.MonoUsedBytes / (1024f * 1024f);
                sumTex += s.TextureBytes / (1024f * 1024f);
                sumAlloc += s.GcAllocBytes;
                sumCollect += s.GcCollectDelta;

                if (s.Fps < minFps) minFps = s.Fps;
                if (s.Fps > maxFps) maxFps = s.Fps;
                if (s.FrameMs < minFrame) minFrame = s.FrameMs;
                if (s.FrameMs > maxFrame) maxFrame = s.FrameMs;
                if (s.CpuMainThreadMs > maxCpu) maxCpu = s.CpuMainThreadMs;
                if (s.DrawCalls > maxDc) maxDc = s.DrawCalls;
                if (s.SetPassCalls > maxSp) maxSp = s.SetPassCalls;
                if (memMb > maxMem) maxMem = memMb;
                if (s.GcAllocBytes > maxAlloc) maxAlloc = s.GcAllocBytes;

                if (s.FrameMs > budget) stutter++;
                if (s.FrameMs > hitchBudget) hitch++;
                if (s.FrameMs > severeBudget) severe++;
            }

            report.durationSec = (float)Math.Max(0, t1 - t0);
            if (report.durationSec < 0.001f && sampleIntervalHint > 0f)
                report.durationSec = n * sampleIntervalHint;

            float inv = 1f / n;
            report.fpsAvg = (float)(sumFps * inv);
            report.fpsMin = minFps;
            report.fpsMax = maxFps;
            report.fpsP1Low = PercentileLow(fpsList, 0.01f);
            report.fpsP5Low = PercentileLow(fpsList, 0.05f);

            report.frameMsAvg = (float)(sumFrame * inv);
            report.frameMsMin = minFrame;
            report.frameMsMax = maxFrame;
            report.cpuMsAvg = (float)(sumCpu * inv);
            report.cpuMsMax = maxCpu;
            report.hasGpuTiming = hasGpu;
            report.gpuMsAvg = hasGpu ? (float)(sumGpu * inv) : 0f;
            report.gpuMsMax = maxGpu;

            report.stutterCount = stutter;
            report.stutterRatePct = stutter * 100f / n;
            report.hitchCount = hitch;
            report.hitchRatePct = hitch * 100f / n;
            report.severeHitchCount = severe;

            report.drawCallAvg = (float)(sumDc * inv);
            report.drawCallMax = maxDc;
            report.setPassAvg = (float)(sumSp * inv);
            report.setPassMax = maxSp;
            report.batchesAvg = (float)(sumBatch * inv);
            report.trisAvg = (float)(sumTris * inv);
            report.vertsAvg = (float)(sumVerts * inv);

            report.memAvgMb = (float)(sumMem * inv);
            report.memMaxMb = maxMem;
            report.monoAvgMb = (float)(sumMono * inv);
            report.textureAvgMb = (float)(sumTex * inv);
            report.gcAllocAvg = (float)(sumAlloc * inv);
            report.gcAllocMax = maxAlloc;
            report.gcCollectSum = (float)sumCollect;

            report.summary = BuildSummary(report);
            report.markdownPreview = ToMarkdown(report);
            return report;
        }

        public static string Export(ProfilingSessionReport report, bool alsoJson = true)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            string dir = GetReportsDirectory();
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string safeScene = string.IsNullOrEmpty(report.sceneName) ? "scene" : SanitizeFileName(report.sceneName);
            string mdPath = Path.Combine(dir, $"report_{safeScene}_{stamp}.md");
            File.WriteAllText(mdPath, ToMarkdown(report), Encoding.UTF8);

            if (alsoJson)
            {
                string jsonPath = Path.Combine(dir, $"report_{safeScene}_{stamp}.json");
                File.WriteAllText(jsonPath, JsonUtility.ToJson(report, true), Encoding.UTF8);
            }

            return mdPath;
        }

        public static string ToMarkdown(ProfilingSessionReport r)
        {
            var sb = new StringBuilder(2048);
            sb.AppendLine("# TA Profiling 性能报告");
            sb.AppendLine();
            sb.AppendLine($"- 生成时间：{r.generatedAt}");
            sb.AppendLine($"- 场景：{r.sceneName}");
            sb.AppendLine($"- Unity：{r.unityVersion}");
            sb.AppendLine($"- Play：{(r.isPlaying ? "是" : "否")}");
            sb.AppendLine($"- 采样数：{r.sampleCount}");
            sb.AppendLine($"- 时长：{r.durationSec:F1} s（采样间隔约 {r.sampleIntervalHint:F2} s）");
            sb.AppendLine($"- 目标帧率：{r.targetFps:F0} FPS（预算 {r.frameBudgetMs:F2} ms）");
            sb.AppendLine();
            sb.AppendLine("## 摘要");
            sb.AppendLine();
            sb.AppendLine(r.summary ?? "");
            sb.AppendLine();
            sb.AppendLine("## 帧率 / 耗时");
            sb.AppendLine();
            sb.AppendLine("| 指标 | 值 |");
            sb.AppendLine("|------|----|");
            sb.AppendLine($"| FPS 均值 | {r.fpsAvg:F1} |");
            sb.AppendLine($"| FPS 最小 / 最大 | {r.fpsMin:F1} / {r.fpsMax:F1} |");
            sb.AppendLine($"| FPS 1% Low | {r.fpsP1Low:F1} |");
            sb.AppendLine($"| FPS 5% Low | {r.fpsP5Low:F1} |");
            sb.AppendLine($"| 帧耗时均值 / 最大 | {r.frameMsAvg:F2} / {r.frameMsMax:F2} ms |");
            sb.AppendLine($"| CPU 均值 / 最大 | {r.cpuMsAvg:F2} / {r.cpuMsMax:F2} ms |");
            if (r.hasGpuTiming)
                sb.AppendLine($"| GPU 均值 / 最大 | {r.gpuMsAvg:F2} / {r.gpuMsMax:F2} ms |");
            sb.AppendLine();
            sb.AppendLine("## 跳帧 / 卡顿");
            sb.AppendLine();
            sb.AppendLine("| 类型 | 次数 | 占比 |");
            sb.AppendLine("|------|------|------|");
            sb.AppendLine($"| 超预算（>{r.frameBudgetMs:F1} ms） | {r.stutterCount} | {r.stutterRatePct:F1}% |");
            sb.AppendLine($"| 卡顿 Hitch（>{r.frameBudgetMs * 2f:F1} ms） | {r.hitchCount} | {r.hitchRatePct:F1}% |");
            sb.AppendLine($"| 严重卡顿（>{Mathf.Max(50f, r.frameBudgetMs * 3f):F0} ms） | {r.severeHitchCount} | — |");
            sb.AppendLine();
            sb.AppendLine("## 渲染均值");
            sb.AppendLine();
            sb.AppendLine("| 指标 | 均值 | 峰值 |");
            sb.AppendLine("|------|------|------|");
            sb.AppendLine($"| DrawCall | {r.drawCallAvg:F1} | {r.drawCallMax:F0} |");
            sb.AppendLine($"| SetPass | {r.setPassAvg:F1} | {r.setPassMax:F0} |");
            sb.AppendLine($"| Batches | {r.batchesAvg:F1} | — |");
            sb.AppendLine($"| Tris | {r.trisAvg:N0} | — |");
            sb.AppendLine($"| Verts | {r.vertsAvg:N0} | — |");
            sb.AppendLine();
            sb.AppendLine("## 内存 / GC");
            sb.AppendLine();
            sb.AppendLine($"- 总内存均值 / 峰值：{r.memAvgMb:F1} / {r.memMaxMb:F1} MB");
            sb.AppendLine($"- Mono 堆均值：{r.monoAvgMb:F1} MB");
            sb.AppendLine($"- 纹理均值：{r.textureAvgMb:F1} MB");
            sb.AppendLine($"- GC.Alloc 均值 / 峰值：{r.gcAllocAvg:F0} / {r.gcAllocMax:F0} B");
            sb.AppendLine($"- GC Collect 累计：{r.gcCollectSum:F0}");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("*由 TA Profiling 自动生成*");
            return sb.ToString();
        }

        private static string BuildSummary(ProfilingSessionReport r)
        {
            string stutterLevel = r.stutterRatePct < 5f ? "良好"
                : r.stutterRatePct < 15f ? "一般"
                : "偏高";
            return
                $"录制 {r.durationSec:F1}s / {r.sampleCount} 点 · FPS 均值 {r.fpsAvg:F1}（1% Low {r.fpsP1Low:F1}）· " +
                $"超预算跳帧 {r.stutterCount} 次（{r.stutterRatePct:F1}%，{stutterLevel}）· " +
                $"Hitch {r.hitchCount} · DC 均值 {r.drawCallAvg:F0}";
        }

        private static float PercentileLow(List<float> values, float ratio)
        {
            if (values == null || values.Count == 0) return 0f;
            values.Sort();
            int count = Mathf.Max(1, Mathf.CeilToInt(values.Count * ratio));
            double sum = 0;
            for (int i = 0; i < count; i++)
                sum += values[i];
            return (float)(sum / count);
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrEmpty(name) ? "scene" : name;
        }
    }
}
