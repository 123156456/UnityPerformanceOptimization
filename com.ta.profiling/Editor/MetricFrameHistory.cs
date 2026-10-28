using System;
using System.Text;
using TA.Profiling;
using UnityEngine;

namespace TA.Profiling.Editor
{
    /// <summary>单次监控采样点（供曲线回看）。</summary>
    public struct MetricFrameSample
    {
        public double TimeSinceStartup;
        public float Fps;
        public float FrameMs;
        public float CpuMainThreadMs;
        public float GpuFrameMs;
        public bool HasGpuTiming;

        public int DrawCalls;
        public int SetPassCalls;
        public int Batches;
        public int Triangles;
        public int Vertices;

        public long TotalAllocatedBytes;
        public long MonoUsedBytes;
        public long TextureBytes;
        public long MeshBytes;
        public int TextureCount;

        public float GcCollectDelta;
        public float GcAllocBytes;
        public int GcGen0;
        public int GcGen1;
        public int GcGen2;

        /// <summary>耗时脚本摘要，多行用 \n 分隔。</summary>
        public string TopScriptsByTime;
        /// <summary>GC 脚本摘要。</summary>
        public string TopScriptsByGc;
    }

    public enum MetricSeries
    {
        None = 0,
        Fps,
        DrawCall,
        SetPass,
        GcCollect,
        GcAlloc
    }

    /// <summary>统一环形缓冲，保证各曲线共享同一帧索引。</summary>
    public sealed class MetricFrameHistory
    {
        public const int Length = 480;

        private readonly MetricFrameSample[] _samples = new MetricFrameSample[Length];
        private int _write;
        private int _filled;

        public int WriteIndex => _write;
        public int Filled => _filled;
        public int Capacity => Length;

        public void Clear()
        {
            Array.Clear(_samples, 0, _samples.Length);
            _write = 0;
            _filled = 0;
        }

        public void Push(in MetricFrameSample sample)
        {
            _samples[_write % Length] = sample;
            _write++;
            if (_filled < Length) _filled++;
        }

        /// <summary>按时间从旧到新：visualIndex 0 = 最旧。</summary>
        public bool TryGetChronological(int visualIndex, out MetricFrameSample sample)
        {
            sample = default;
            if (_filled <= 0 || visualIndex < 0 || visualIndex >= _filled) return false;
            int start = _write - _filled;
            sample = _samples[(start + visualIndex) % Length];
            return true;
        }

        public float GetSeriesValue(int visualIndex, MetricSeries series)
        {
            if (!TryGetChronological(visualIndex, out var s)) return 0f;
            switch (series)
            {
                case MetricSeries.Fps: return s.Fps;
                case MetricSeries.DrawCall: return s.DrawCalls;
                case MetricSeries.SetPass: return s.SetPassCalls;
                case MetricSeries.GcCollect: return s.GcCollectDelta;
                case MetricSeries.GcAlloc: return s.GcAllocBytes;
                default: return 0f;
            }
        }

        public float GetSeriesMax(MetricSeries series, float fallback)
        {
            float max = fallback;
            for (int i = 0; i < _filled; i++)
            {
                float v = GetSeriesValue(i, series);
                if (v > max) max = v;
            }

            return Mathf.Max(fallback, max);
        }

        public static string FormatTopScripts(GcScriptSnapshot snap, bool byGc, int take = 3)
        {
            if (snap == null) return string.Empty;
            var list = byGc ? snap.TopByGc : snap.TopByTime;
            if (list == null || list.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            int n = Mathf.Min(take, list.Count);
            for (int i = 0; i < n; i++)
            {
                var e = list[i];
                if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                if (sb.Length > 0) sb.Append('\n');
                if (byGc)
                    sb.Append(e.Name).Append(" · ").Append(PerformanceMetrics.FormatBytes(e.GcBytes));
                else
                    sb.Append(e.Name).Append(" · ").Append(e.SelfMs.ToString("F2")).Append(" ms");
            }

            return sb.ToString();
        }
    }
}
