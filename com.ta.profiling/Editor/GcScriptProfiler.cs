using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace TA.Profiling.Editor
{
    public sealed class ScriptCostEntry
    {
        public string Name;
        public float TotalMs;
        public float SelfMs;
        public long GcBytes;
        public int Calls;
    }

    public sealed class GcScriptSnapshot
    {
        public readonly List<ScriptCostEntry> TopByTime = new List<ScriptCostEntry>();
        public readonly List<ScriptCostEntry> TopByGc = new List<ScriptCostEntry>();
        public float GcAllocBytesPerFrame;
        public float GcCollectsPerSecond;
        public int Gen0;
        public int Gen1;
        public int Gen2;
        public bool ProfilerEnabled;
        public string Status;
    }

    /// <summary>
    /// GC 触发频率可视化数据 + 通过 Profiler 层级采样定位耗时脚本 / GC.Alloc 源头。
    /// </summary>
    public sealed class GcScriptProfiler : IDisposable
    {
        public const int HistoryLength = 120;

        private readonly float[] _gcCollectHistory = new float[HistoryLength];
        private readonly float[] _gcAllocHistory = new float[HistoryLength];
        private int _histIndex;
        private int _histFilled;

        private ProfilerRecorder _gcAllocRecorder;
        private int _lastGen0;
        private int _lastGen1;
        private int _lastGen2;
        private float _collectAccum;
        private float _timeAccum;
        private float _collectsPerSecond;
        private float _allocPerFrame;
        private double _lastHierarchySample;
        private GcScriptSnapshot _latest = new GcScriptSnapshot();
        private bool _disposed;

        public float[] CollectHistory => _gcCollectHistory;
        public float[] AllocHistory => _gcAllocHistory;
        public int HistoryIndex => _histIndex;
        public int HistoryFilled => _histFilled;
        public GcScriptSnapshot Latest => _latest;
        public float CollectsPerSecond => _collectsPerSecond;
        public float AllocBytesPerFrame => _allocPerFrame;

        public GcScriptProfiler()
        {
            _gcAllocRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc");
            _lastGen0 = GC.CollectionCount(0);
            _lastGen1 = GC.CollectionCount(1);
            _lastGen2 = GC.CollectionCount(2);
        }

        public void Tick(float dt)
        {
            if (_disposed) return;

            int g0 = GC.CollectionCount(0);
            int g1 = GC.CollectionCount(1);
            int g2 = GC.CollectionCount(2);
            int collects = (g0 - _lastGen0) + (g1 - _lastGen1) + (g2 - _lastGen2);
            _lastGen0 = g0;
            _lastGen1 = g1;
            _lastGen2 = g2;

            _collectAccum += collects;
            _timeAccum += dt;
            if (_timeAccum >= 1f)
            {
                _collectsPerSecond = _collectAccum / _timeAccum;
                _collectAccum = 0f;
                _timeAccum = 0f;
            }

            long alloc = 0;
            if (_gcAllocRecorder.Valid && _gcAllocRecorder.Count > 0)
                alloc = _gcAllocRecorder.LastValue;
            _allocPerFrame = Mathf.Lerp(_allocPerFrame, alloc, 0.2f);

            _gcCollectHistory[_histIndex % HistoryLength] = collects;
            _gcAllocHistory[_histIndex % HistoryLength] = alloc;
            _histIndex++;
            if (_histFilled < HistoryLength) _histFilled++;

            _latest.Gen0 = g0;
            _latest.Gen1 = g1;
            _latest.Gen2 = g2;
            _latest.GcCollectsPerSecond = _collectsPerSecond;
            _latest.GcAllocBytesPerFrame = _allocPerFrame;

            if (EditorApplication.timeSinceStartup - _lastHierarchySample > 0.75)
            {
                _lastHierarchySample = EditorApplication.timeSinceStartup;
                SampleHierarchy();
            }
        }

        public void ForceSampleHierarchy() => SampleHierarchy();

        private void SampleHierarchy()
        {
            _latest.TopByTime.Clear();
            _latest.TopByGc.Clear();

            try
            {
                if (!ProfilerDriver.enabled)
                {
                    ProfilerDriver.enabled = true;
                    ProfilerDriver.profileEditor = false;
                }

                _latest.ProfilerEnabled = ProfilerDriver.enabled;
                int frame = ProfilerDriver.lastFrameIndex;
                if (frame < 0)
                {
                    _latest.Status = "等待 Profiler 帧数据…";
                    return;
                }

                // threadIndex 0 = Main Thread
                using (var view = ProfilerDriver.GetHierarchyFrameDataView(
                           frame, 0,
                           HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                           HierarchyFrameDataView.columnTotalTime, false))
                {
                    if (!view.valid)
                    {
                        _latest.Status = "Profiler 帧无效";
                        return;
                    }

                    CollectTopSamples(view, _latest.TopByTime, _latest.TopByGc);
                    _latest.Status = $"帧 #{frame}";
                }
            }
            catch (Exception ex)
            {
                _latest.Status = "Profiler 采样失败: " + ex.Message;
                _latest.ProfilerEnabled = false;
            }
        }

        private static void CollectTopSamples(
            HierarchyFrameDataView view,
            List<ScriptCostEntry> byTime,
            List<ScriptCostEntry> byGc)
        {
            var timeBuf = new List<ScriptCostEntry>(64);
            var gcBuf = new List<ScriptCostEntry>(64);
            int visited = 0;
            const int maxNodes = 800;

            void Walk(int id, int depth)
            {
                if (!view.valid || id < 0 || visited > maxNodes || depth > 12) return;
                visited++;

                string name = view.GetItemName(id);
                float total = view.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnTotalTime);
                float self = view.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnSelfTime);
                long gc = 0;
                try
                {
                    gc = (long)view.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnGcMemory);
                }
                catch
                {
                    gc = 0;
                }

                int calls = 0;
                try
                {
                    calls = (int)view.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnCalls);
                }
                catch { /* ignore */ }

                bool interesting = IsScriptLike(name) || gc > 256 || (self > 0.05f && depth > 0);
                if (interesting && !string.IsNullOrEmpty(name))
                {
                    var entry = new ScriptCostEntry
                    {
                        Name = name,
                        TotalMs = total,
                        SelfMs = self,
                        GcBytes = gc,
                        Calls = calls
                    };
                    if (self > 0.02f || total > 0.1f)
                        timeBuf.Add(entry);
                    if (gc > 0)
                        gcBuf.Add(entry);
                }

                var children = new List<int>(16);
                view.GetItemChildren(id, children);
                for (int i = 0; i < children.Count; i++)
                    Walk(children[i], depth + 1);
            }

            Walk(view.GetRootItemID(), 0);

            timeBuf.Sort((a, b) => b.SelfMs.CompareTo(a.SelfMs));
            gcBuf.Sort((a, b) => b.GcBytes.CompareTo(a.GcBytes));

            for (int i = 0; i < timeBuf.Count && byTime.Count < 15; i++)
                byTime.Add(timeBuf[i]);
            for (int i = 0; i < gcBuf.Count && byGc.Count < 15; i++)
                byGc.Add(gcBuf[i]);
        }

        private static bool IsScriptLike(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // Behaviour Update / LateUpdate / Invoke 等常见脚本入口
            if (name.IndexOf("Update()", StringComparison.Ordinal) >= 0) return true;
            if (name.IndexOf("LateUpdate()", StringComparison.Ordinal) >= 0) return true;
            if (name.IndexOf("FixedUpdate()", StringComparison.Ordinal) >= 0) return true;
            if (name.IndexOf("Invoke", StringComparison.Ordinal) >= 0) return true;
            if (name.IndexOf(".ctor", StringComparison.Ordinal) >= 0) return false;
            // 命名空间.类型:方法
            if (name.IndexOf(':') >= 0 && name.IndexOf('.') >= 0) return true;
            if (name.StartsWith("Behaviour.", StringComparison.Ordinal)) return true;
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_gcAllocRecorder.Valid)
                _gcAllocRecorder.Dispose();
        }
    }
}
