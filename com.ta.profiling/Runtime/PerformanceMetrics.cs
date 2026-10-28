using System;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace TA.Profiling
{
    /// <summary>
    /// 运行时性能采样结果（只读快照）。
    /// </summary>
    public readonly struct PerformanceSnapshot
    {
        public readonly float Fps;
        public readonly float FrameMs;
        public readonly float CpuMainThreadMs;
        public readonly float GpuFrameMs;
        public readonly bool HasGpuTiming;

        public readonly long TotalAllocatedBytes;
        public readonly long TotalReservedBytes;
        public readonly long MonoUsedBytes;
        public readonly long MonoHeapBytes;
        public readonly long GraphicsDriverBytes;
        public readonly long TextureBytes;
        public readonly long MeshBytes;
        public readonly long MaterialBytes;
        public readonly long AudioBytes;
        public readonly int TextureCount;
        public readonly int MeshCount;

        public readonly int GcGen0;
        public readonly int GcGen1;
        public readonly int GcGen2;
        public readonly int GcGen0Delta;
        public readonly int GcGen1Delta;
        public readonly int GcGen2Delta;

        public PerformanceSnapshot(
            float fps,
            float frameMs,
            float cpuMainThreadMs,
            float gpuFrameMs,
            bool hasGpuTiming,
            long totalAllocatedBytes,
            long totalReservedBytes,
            long monoUsedBytes,
            long monoHeapBytes,
            long graphicsDriverBytes,
            long textureBytes,
            long meshBytes,
            long materialBytes,
            long audioBytes,
            int textureCount,
            int meshCount,
            int gcGen0,
            int gcGen1,
            int gcGen2,
            int gcGen0Delta,
            int gcGen1Delta,
            int gcGen2Delta)
        {
            Fps = fps;
            FrameMs = frameMs;
            CpuMainThreadMs = cpuMainThreadMs;
            GpuFrameMs = gpuFrameMs;
            HasGpuTiming = hasGpuTiming;
            TotalAllocatedBytes = totalAllocatedBytes;
            TotalReservedBytes = totalReservedBytes;
            MonoUsedBytes = monoUsedBytes;
            MonoHeapBytes = monoHeapBytes;
            GraphicsDriverBytes = graphicsDriverBytes;
            TextureBytes = textureBytes;
            MeshBytes = meshBytes;
            MaterialBytes = materialBytes;
            AudioBytes = audioBytes;
            TextureCount = textureCount;
            MeshCount = meshCount;
            GcGen0 = gcGen0;
            GcGen1 = gcGen1;
            GcGen2 = gcGen2;
            GcGen0Delta = gcGen0Delta;
            GcGen1Delta = gcGen1Delta;
            GcGen2Delta = gcGen2Delta;
        }
    }

    /// <summary>
    /// 采集帧率、主线程/GPU 耗时、总内存 / 堆 / 纹理等与 GC 信息。
    /// </summary>
    public sealed class PerformanceMetrics : IDisposable
    {
        private readonly float _sampleInterval;
        private readonly FrameTiming[] _frameTimings = new FrameTiming[1];

        private ProfilerRecorder _mainThreadRecorder;
        private ProfilerRecorder _cpuFrameTimeRecorder;
        private ProfilerRecorder _gpuFrameTimeRecorder;

        private float _accumTime;
        private int _accumFrames;
        private float _smoothedFps = 60f;
        private float _smoothedFrameMs = 16.67f;
        private float _smoothedCpuMs;
        private float _smoothedGpuMs;
        private bool _hasGpuTiming;

        private int _lastGc0;
        private int _lastGc1;
        private int _lastGc2;
        private int _gc0Delta;
        private int _gc1Delta;
        private int _gc2Delta;

        private long _textureBytes;
        private long _meshBytes;
        private long _materialBytes;
        private long _audioBytes;
        private int _textureCount;
        private int _meshCount;
        private float _assetSampleCooldown;

        private PerformanceSnapshot _latest;
        private bool _disposed;

        public PerformanceSnapshot Latest => _latest;

        public PerformanceMetrics(float sampleIntervalSeconds = 0.25f)
        {
            _sampleInterval = Mathf.Max(0.05f, sampleIntervalSeconds);
            BeginRecorders();
            FrameTimingManager.CaptureFrameTimings();

            _lastGc0 = GC.CollectionCount(0);
            _lastGc1 = GC.CollectionCount(1);
            _lastGc2 = GC.CollectionCount(2);
            SampleAssetMemory(force: true);
            _latest = CaptureNow();
        }

        public void Tick(float unscaledDeltaTime)
        {
            if (_disposed)
                return;

            _accumTime += unscaledDeltaTime;
            _accumFrames++;
            _assetSampleCooldown -= unscaledDeltaTime;

            SampleFrameTimings(unscaledDeltaTime);

            if (_accumTime < _sampleInterval)
                return;

            float avgDelta = _accumTime / Mathf.Max(1, _accumFrames);
            _smoothedFps = avgDelta > 0.0001f ? 1f / avgDelta : _smoothedFps;
            _smoothedFrameMs = avgDelta * 1000f;

            RefreshGcDeltas();
            if (_assetSampleCooldown <= 0f)
                SampleAssetMemory(force: false);
            _latest = CaptureNow();

            _accumTime = 0f;
            _accumFrames = 0;
        }

        /// <summary>立即刷新纹理/网格等资产内存统计（打开详情时调用）。</summary>
        public void ForceRefreshAssetMemory()
        {
            SampleAssetMemory(force: true);
            _latest = CaptureNow();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            DisposeRecorder(ref _mainThreadRecorder);
            DisposeRecorder(ref _cpuFrameTimeRecorder);
            DisposeRecorder(ref _gpuFrameTimeRecorder);
        }

        public static string FormatBytes(long bytes)
        {
            const double kb = 1024d;
            const double mb = kb * 1024d;
            if (bytes >= mb)
                return $"{bytes / mb:F1} MB";
            if (bytes >= kb)
                return $"{bytes / kb:F1} KB";
            return $"{bytes} B";
        }

        public void AppendDebugText(StringBuilder sb)
        {
            var s = _latest;
            sb.Append("FPS ").Append(s.Fps.ToString("F1"))
              .Append("  ").Append(s.FrameMs.ToString("F2")).Append(" ms\n");
            sb.Append("CPU ").Append(s.CpuMainThreadMs.ToString("F2")).Append(" ms");
            if (s.HasGpuTiming)
                sb.Append("  GPU ").Append(s.GpuFrameMs.ToString("F2")).Append(" ms");
            sb.Append('\n');
            sb.Append("Total ").Append(FormatBytes(s.TotalAllocatedBytes))
              .Append("  Heap ").Append(FormatBytes(s.MonoUsedBytes))
              .Append('/').Append(FormatBytes(s.MonoHeapBytes)).Append('\n');
            sb.Append("Tex ").Append(FormatBytes(s.TextureBytes))
              .Append("  Mesh ").Append(FormatBytes(s.MeshBytes)).Append('\n');
            sb.Append("GC 0/1/2 ").Append(s.GcGen0).Append('/').Append(s.GcGen1).Append('/').Append(s.GcGen2);
        }

        private void BeginRecorders()
        {
            _mainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");
            _cpuFrameTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Total Frame Time");
            _gpuFrameTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time");
        }

        private void SampleFrameTimings(float unscaledDeltaTime)
        {
            float cpuMs = unscaledDeltaTime * 1000f;
            if (_mainThreadRecorder.Valid && _mainThreadRecorder.Count > 0)
                cpuMs = _mainThreadRecorder.LastValue * 1e-6f;
            else if (_cpuFrameTimeRecorder.Valid && _cpuFrameTimeRecorder.Count > 0)
                cpuMs = _cpuFrameTimeRecorder.LastValue * 1e-6f;

            _smoothedCpuMs = Mathf.Lerp(_smoothedCpuMs <= 0f ? cpuMs : _smoothedCpuMs, cpuMs, 0.25f);

            FrameTimingManager.CaptureFrameTimings();
            uint count = FrameTimingManager.GetLatestTimings(1, _frameTimings);
            if (count > 0 && _frameTimings[0].gpuFrameTime > 0d)
            {
                float gpuMs = (float)_frameTimings[0].gpuFrameTime;
                _smoothedGpuMs = Mathf.Lerp(_smoothedGpuMs <= 0f ? gpuMs : _smoothedGpuMs, gpuMs, 0.25f);
                _hasGpuTiming = true;
            }
            else if (_gpuFrameTimeRecorder.Valid && _gpuFrameTimeRecorder.Count > 0 && _gpuFrameTimeRecorder.LastValue > 0)
            {
                float gpuMs = _gpuFrameTimeRecorder.LastValue * 1e-6f;
                _smoothedGpuMs = Mathf.Lerp(_smoothedGpuMs <= 0f ? gpuMs : _smoothedGpuMs, gpuMs, 0.25f);
                _hasGpuTiming = true;
            }
        }

        private void SampleAssetMemory(bool force)
        {
            // 资产遍历较重，默认约 1s 采一次
            if (!force && _assetSampleCooldown > 0f)
                return;
            _assetSampleCooldown = 1f;

            long texBytes = 0, meshBytes = 0, matBytes = 0, audioBytes = 0;
            int texCount = 0, meshCount = 0;

            var textures = Resources.FindObjectsOfTypeAll<Texture>();
            for (int i = 0; i < textures.Length; i++)
            {
                var t = textures[i];
                if (t == null || !IsRuntimeOrSceneAsset(t)) continue;
                texBytes += Profiler.GetRuntimeMemorySizeLong(t);
                texCount++;
            }

            var meshes = Resources.FindObjectsOfTypeAll<Mesh>();
            for (int i = 0; i < meshes.Length; i++)
            {
                var m = meshes[i];
                if (m == null || !IsRuntimeOrSceneAsset(m)) continue;
                meshBytes += Profiler.GetRuntimeMemorySizeLong(m);
                meshCount++;
            }

            var materials = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < materials.Length; i++)
            {
                var mat = materials[i];
                if (mat == null || !IsRuntimeOrSceneAsset(mat)) continue;
                matBytes += Profiler.GetRuntimeMemorySizeLong(mat);
            }

            var clips = Resources.FindObjectsOfTypeAll<AudioClip>();
            for (int i = 0; i < clips.Length; i++)
            {
                var c = clips[i];
                if (c == null || !IsRuntimeOrSceneAsset(c)) continue;
                audioBytes += Profiler.GetRuntimeMemorySizeLong(c);
            }

            _textureBytes = texBytes;
            _meshBytes = meshBytes;
            _materialBytes = matBytes;
            _audioBytes = audioBytes;
            _textureCount = texCount;
            _meshCount = meshCount;
        }

        /// <summary>排除纯 Editor 资源库里未加载进玩法的资产（尽量只计运行时相关）。</summary>
        private static bool IsRuntimeOrSceneAsset(Object obj)
        {
            if (obj == null) return false;
            // HideFlags.HideAndDontSave 等内部对象仍可能占内存，保留
            if ((obj.hideFlags & HideFlags.DontSave) != 0 && obj.hideFlags != HideFlags.None)
            {
                // 运行时动态创建的 DontSave 对象需要计入
                if (Application.isPlaying)
                    return true;
            }

            return true;
        }

        private void RefreshGcDeltas()
        {
            int g0 = GC.CollectionCount(0);
            int g1 = GC.CollectionCount(1);
            int g2 = GC.CollectionCount(2);
            _gc0Delta = g0 - _lastGc0;
            _gc1Delta = g1 - _lastGc1;
            _gc2Delta = g2 - _lastGc2;
            _lastGc0 = g0;
            _lastGc1 = g1;
            _lastGc2 = g2;
        }

        private PerformanceSnapshot CaptureNow()
        {
            return new PerformanceSnapshot(
                _smoothedFps,
                _smoothedFrameMs,
                _smoothedCpuMs,
                _smoothedGpuMs,
                _hasGpuTiming,
                Profiler.GetTotalAllocatedMemoryLong(),
                Profiler.GetTotalReservedMemoryLong(),
                Profiler.GetMonoUsedSizeLong(),
                Profiler.GetMonoHeapSizeLong(),
                Profiler.GetAllocatedMemoryForGraphicsDriver(),
                _textureBytes,
                _meshBytes,
                _materialBytes,
                _audioBytes,
                _textureCount,
                _meshCount,
                _lastGc0,
                _lastGc1,
                _lastGc2,
                _gc0Delta,
                _gc1Delta,
                _gc2Delta);
        }

        private static void DisposeRecorder(ref ProfilerRecorder recorder)
        {
            if (recorder.Valid)
                recorder.Dispose();
            recorder = default;
        }
    }
}
