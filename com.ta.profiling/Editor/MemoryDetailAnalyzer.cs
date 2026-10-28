using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace TA.Profiling.Editor
{
    public enum MemoryObjectKind
    {
        Texture,
        Mesh,
        Material,
        Audio,
        RenderTexture,
        Animation,
        Other
    }

    public sealed class MemoryObjectEntry
    {
        public Object Target;
        public string Name;
        public string TypeName;
        public MemoryObjectKind Kind;
        public long Bytes;
        public long BaselineBytes;
        public long DeltaBytes;
        public bool IsNew;
        public string Hint;
    }

    public sealed class MemoryLeakSuspect
    {
        public string Title;
        public string Detail;
        public MessageType Severity;
        public Object PingTarget;
        public long Bytes;
    }

    public sealed class MemoryDetailReport
    {
        public readonly List<MemoryObjectEntry> LargeObjects = new List<MemoryObjectEntry>();
        public readonly List<MemoryLeakSuspect> LeakSuspects = new List<MemoryLeakSuspect>();
        public long TotalBytes;
        public long HeapUsed;
        public long HeapSize;
        public long TextureBytes;
        public long MeshBytes;
        public int SampledObjectCount;
        public double CapturedAt;
    }

    /// <summary>
    /// 扫描大内存对象，并基于基线对比给出可疑泄露点（增长、重复实例、未释放趋势）。
    /// </summary>
    public static class MemoryDetailAnalyzer
    {
        private static readonly Dictionary<int, long> Baseline = new Dictionary<int, long>(1024);
        private static readonly Dictionary<string, int> BaselineTypeCounts = new Dictionary<string, int>();
        private static bool _hasBaseline;
        private static long _baselineTotalAllocated;
        private static long _baselineMonoUsed;

        public static bool HasBaseline => _hasBaseline;

        public static void CaptureBaseline()
        {
            Baseline.Clear();
            BaselineTypeCounts.Clear();
            _baselineTotalAllocated = Profiler.GetTotalAllocatedMemoryLong();
            _baselineMonoUsed = Profiler.GetMonoUsedSizeLong();

            foreach (var obj in EnumerateTrackedObjects())
            {
                if (obj == null) continue;
                int id = obj.GetInstanceID();
                long size = SafeSize(obj);
                Baseline[id] = size;
                string key = TypeKey(obj);
                BaselineTypeCounts.TryGetValue(key, out int c);
                BaselineTypeCounts[key] = c + 1;
            }

            _hasBaseline = true;
        }

        public static void ClearBaseline()
        {
            Baseline.Clear();
            BaselineTypeCounts.Clear();
            _hasBaseline = false;
        }

        public static MemoryDetailReport Scan(int largeObjectLimit = 40, long minBytes = 64 * 1024)
        {
            var report = new MemoryDetailReport
            {
                CapturedAt = EditorApplication.timeSinceStartup,
                TotalBytes = Profiler.GetTotalAllocatedMemoryLong(),
                HeapUsed = Profiler.GetMonoUsedSizeLong(),
                HeapSize = Profiler.GetMonoHeapSizeLong()
            };

            var current = new List<MemoryObjectEntry>(512);
            var typeCounts = new Dictionary<string, int>();
            var typeBytes = new Dictionary<string, long>();
            var nameDupes = new Dictionary<string, List<MemoryObjectEntry>>();

            long texSum = 0, meshSum = 0;

            foreach (var obj in EnumerateTrackedObjects())
            {
                if (obj == null) continue;
                long size = SafeSize(obj);
                report.SampledObjectCount++;

                var kind = Classify(obj);
                if (kind == MemoryObjectKind.Texture || kind == MemoryObjectKind.RenderTexture)
                    texSum += size;
                else if (kind == MemoryObjectKind.Mesh)
                    meshSum += size;

                string typeKey = TypeKey(obj);
                typeCounts.TryGetValue(typeKey, out int tc);
                typeCounts[typeKey] = tc + 1;
                typeBytes.TryGetValue(typeKey, out long tb);
                typeBytes[typeKey] = tb + size;

                int id = obj.GetInstanceID();
                long baseline = 0;
                bool isNew = false;
                if (_hasBaseline)
                {
                    if (!Baseline.TryGetValue(id, out baseline))
                        isNew = true;
                }

                var entry = new MemoryObjectEntry
                {
                    Target = obj,
                    Name = string.IsNullOrEmpty(obj.name) ? $"<{obj.GetType().Name}>" : obj.name,
                    TypeName = obj.GetType().Name,
                    Kind = kind,
                    Bytes = size,
                    BaselineBytes = baseline,
                    DeltaBytes = size - baseline,
                    IsNew = isNew,
                    Hint = BuildHint(obj, kind, size)
                };
                current.Add(entry);

                string dupeKey = kind + "|" + entry.Name;
                if (!nameDupes.TryGetValue(dupeKey, out var list))
                {
                    list = new List<MemoryObjectEntry>();
                    nameDupes[dupeKey] = list;
                }

                list.Add(entry);
            }

            report.TextureBytes = texSum;
            report.MeshBytes = meshSum;

            current.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            for (int i = 0; i < current.Count && report.LargeObjects.Count < largeObjectLimit; i++)
            {
                if (current[i].Bytes < minBytes && report.LargeObjects.Count >= 10)
                    break;
                report.LargeObjects.Add(current[i]);
            }

            BuildLeakSuspects(report, current, typeCounts, nameDupes);
            return report;
        }

        private static void BuildLeakSuspects(
            MemoryDetailReport report,
            List<MemoryObjectEntry> current,
            Dictionary<string, int> typeCounts,
            Dictionary<string, List<MemoryObjectEntry>> nameDupes)
        {
            if (_hasBaseline)
            {
                long totalDelta = report.TotalBytes - _baselineTotalAllocated;
                long heapDelta = report.HeapUsed - _baselineMonoUsed;
                if (totalDelta > 8 * 1024 * 1024)
                {
                    report.LeakSuspects.Add(new MemoryLeakSuspect
                    {
                        Title = "总分配内存持续上涨",
                        Detail = $"相对基线 +{PerformanceMetrics.FormatBytes(totalDelta)}（当前 {PerformanceMetrics.FormatBytes(report.TotalBytes)}）",
                        Severity = totalDelta > 64 * 1024 * 1024 ? MessageType.Error : MessageType.Warning,
                        Bytes = totalDelta
                    });
                }

                if (heapDelta > 4 * 1024 * 1024)
                {
                    report.LeakSuspects.Add(new MemoryLeakSuspect
                    {
                        Title = "托管堆 Used 上涨",
                        Detail = $"相对基线 +{PerformanceMetrics.FormatBytes(heapDelta)}，可能存在托管对象滞留或频繁分配",
                        Severity = MessageType.Warning,
                        Bytes = heapDelta
                    });
                }

                // 新增的大对象
                int newLarge = 0;
                long newBytes = 0;
                Object sample = null;
                foreach (var e in current)
                {
                    if (!e.IsNew || e.Bytes < 256 * 1024) continue;
                    newLarge++;
                    newBytes += e.Bytes;
                    if (sample == null) sample = e.Target;
                }

                if (newLarge > 0)
                {
                    report.LeakSuspects.Add(new MemoryLeakSuspect
                    {
                        Title = $"新增大对象 ×{newLarge}",
                        Detail = $"基线之后新出现的 ≥256KB 对象，合计 {PerformanceMetrics.FormatBytes(newBytes)}",
                        Severity = newBytes > 16 * 1024 * 1024 ? MessageType.Error : MessageType.Warning,
                        PingTarget = sample,
                        Bytes = newBytes
                    });
                }

                // 单对象相对基线暴涨
                foreach (var e in current)
                {
                    if (e.IsNew || e.DeltaBytes < 1024 * 1024) continue;
                    report.LeakSuspects.Add(new MemoryLeakSuspect
                    {
                        Title = $"对象膨胀：{e.Name}",
                        Detail = $"{e.TypeName} 相对基线 +{PerformanceMetrics.FormatBytes(e.DeltaBytes)} → {PerformanceMetrics.FormatBytes(e.Bytes)}",
                        Severity = MessageType.Warning,
                        PingTarget = e.Target,
                        Bytes = e.DeltaBytes
                    });
                    if (report.LeakSuspects.Count > 24) break;
                }

                // 某类型实例数相对基线明显增加
                foreach (var kv in typeCounts)
                {
                    BaselineTypeCounts.TryGetValue(kv.Key, out int baseCount);
                    int delta = kv.Value - baseCount;
                    if (delta < 8) continue;
                    report.LeakSuspects.Add(new MemoryLeakSuspect
                    {
                        Title = $"类型实例增多：{kv.Key}",
                        Detail = $"基线 {baseCount} → 当前 {kv.Value}（+{delta}），可能未释放或重复加载",
                        Severity = delta >= 30 ? MessageType.Error : MessageType.Warning,
                        Bytes = delta
                    });
                }
            }
            else
            {
                report.LeakSuspects.Add(new MemoryLeakSuspect
                {
                    Title = "尚未建立内存基线",
                    Detail = "点击「记录基线」后继续 Play，再扫描可对比增长与新增对象。",
                    Severity = MessageType.Info
                });
            }

            // 同名同类型重复（即使无基线也有价值）
            foreach (var kv in nameDupes)
            {
                if (kv.Value.Count < 4) continue;
                long sum = 0;
                foreach (var e in kv.Value) sum += e.Bytes;
                if (sum < 512 * 1024) continue;
                var first = kv.Value[0];
                report.LeakSuspects.Add(new MemoryLeakSuspect
                {
                    Title = $"重复实例：{first.Name}",
                    Detail = $"{first.TypeName} ×{kv.Value.Count}，合计 {PerformanceMetrics.FormatBytes(sum)}（疑似重复加载）",
                    Severity = kv.Value.Count >= 10 ? MessageType.Error : MessageType.Warning,
                    PingTarget = first.Target,
                    Bytes = sum
                });
            }

            report.LeakSuspects.Sort((a, b) =>
            {
                int sa = SeverityRank(a.Severity);
                int sb = SeverityRank(b.Severity);
                if (sa != sb) return sb.CompareTo(sa);
                return b.Bytes.CompareTo(a.Bytes);
            });
        }

        private static int SeverityRank(MessageType t)
        {
            switch (t)
            {
                case MessageType.Error: return 3;
                case MessageType.Warning: return 2;
                default: return 1;
            }
        }

        private static IEnumerable<Object> EnumerateTrackedObjects()
        {
            foreach (var o in Resources.FindObjectsOfTypeAll<Texture>()) yield return o;
            foreach (var o in Resources.FindObjectsOfTypeAll<Mesh>()) yield return o;
            foreach (var o in Resources.FindObjectsOfTypeAll<Material>()) yield return o;
            foreach (var o in Resources.FindObjectsOfTypeAll<AudioClip>()) yield return o;
            foreach (var o in Resources.FindObjectsOfTypeAll<AnimationClip>()) yield return o;
        }

        private static MemoryObjectKind Classify(Object obj)
        {
            if (obj is RenderTexture) return MemoryObjectKind.RenderTexture;
            if (obj is Texture) return MemoryObjectKind.Texture;
            if (obj is Mesh) return MemoryObjectKind.Mesh;
            if (obj is Material) return MemoryObjectKind.Material;
            if (obj is AudioClip) return MemoryObjectKind.Audio;
            if (obj is AnimationClip) return MemoryObjectKind.Animation;
            return MemoryObjectKind.Other;
        }

        private static string TypeKey(Object obj) => obj.GetType().Name;

        private static long SafeSize(Object obj)
        {
            try { return Profiler.GetRuntimeMemorySizeLong(obj); }
            catch { return 0; }
        }

        private static string BuildHint(Object obj, MemoryObjectKind kind, long bytes)
        {
            if (obj is Texture tex)
                return $"{tex.width}×{tex.height}";
            if (obj is Mesh mesh)
                return $"{mesh.vertexCount} verts";
            if (kind == MemoryObjectKind.Audio && obj is AudioClip clip)
                return $"{clip.length:F1}s";
            return PerformanceMetrics.FormatBytes(bytes);
        }
    }
}
