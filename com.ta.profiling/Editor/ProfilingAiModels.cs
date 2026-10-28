using System;
using System.Collections.Generic;

namespace TA.Profiling.Editor
{
    /// <summary>
    /// AI 可读的性能分析报告（JSON 序列化友好）。
    /// schema 1.1 起支持分块：includedSections + 可选 frameWindow。
    /// </summary>
    [Serializable]
    public sealed class ProfilingAiReport
    {
        public string schemaVersion = "1.1";
        public string generatedAt;
        public string unityVersion;
        public string sceneName;
        public bool isPlaying;
        /// <summary>full | chunk</summary>
        public string exportMode = "full";
        /// <summary>本次实际写入的块名，如 realtime / frameWindow。</summary>
        public List<string> includedSections = new List<string>();
        public ProfilingAiRealtime realtime = new ProfilingAiRealtime();
        public ProfilingAiRenderStats render = new ProfilingAiRenderStats();
        public ProfilingAiGcInfo gc = new ProfilingAiGcInfo();
        public ProfilingAiCullingInfo culling = new ProfilingAiCullingInfo();
        public ProfilingAiBatchInfo batch = new ProfilingAiBatchInfo();
        /// <summary>监控缓冲中的时段曲线（帧率波动等）；未导出时为 null 语义用空对象 + sampleCount=0。</summary>
        public ProfilingAiFrameWindow frameWindow = new ProfilingAiFrameWindow();
        public List<ProfilingAiIssue> issues = new List<ProfilingAiIssue>();
        public List<ProfilingAiActionDef> availableActions = new List<ProfilingAiActionDef>();
        public string guidance;
    }

    /// <summary>一段时间内的采样汇总 + 可选降采样点，供 AI 分析帧率波动等。</summary>
    [Serializable]
    public sealed class ProfilingAiFrameWindow
    {
        public string focus = "fps_fluctuation";
        public float durationSec;
        public int sampleCount;
        public int startIndex;
        public int endIndex;
        public float targetFps = 60f;
        public float frameBudgetMs = 16.67f;

        public float fpsAvg;
        public float fpsMin;
        public float fpsMax;
        public float fpsP1Low;
        public float fpsP5Low;

        public float frameMsAvg;
        public float frameMsMax;
        public float cpuMsAvg;
        public float cpuMsMax;

        public int stutterCount;
        public float stutterRatePct;
        public int hitchCount;

        public float drawCallAvg;
        public float drawCallMax;
        public float gcAllocAvg;
        public float gcAllocMax;

        public string summary;
        /// <summary>降采样后的时序点（相对窗口起点秒）。</summary>
        public List<ProfilingAiFramePoint> samples = new List<ProfilingAiFramePoint>();
    }

    [Serializable]
    public sealed class ProfilingAiFramePoint
    {
        public float t;
        public float fps;
        public float frameMs;
        public float cpuMs;
        public int drawCalls;
        public int setPass;
        public float gcAlloc;
    }

    [Serializable]
    public sealed class ProfilingAiRealtime
    {
        public float fps;
        public float frameMs;
        public float cpuMainThreadMs;
        public float gpuFrameMs;
        public bool hasGpuTiming;
        public long totalAllocBytes;
        public long monoUsedBytes;
        public long monoHeapBytes;
        public long textureBytes;
        public long meshBytes;
        public long graphicsDriverBytes;
    }

    [Serializable]
    public sealed class ProfilingAiRenderStats
    {
        public int drawCalls;
        public int setPassCalls;
        public int batches;
        public int triangles;
        public int vertices;
        public int drawCallsPeak;
        public int setPassPeak;
        public int realtimeLights;
        public int shadowLights;
        public int activePostVolumes;
        public List<string> postEffects = new List<string>();
        public List<ProfilingAiNamedScore> topLights = new List<ProfilingAiNamedScore>();
    }

    [Serializable]
    public sealed class ProfilingAiGcInfo
    {
        public float collectsPerSecond;
        public float allocBytesPerFrame;
        public int gen0, gen1, gen2;
        public List<ProfilingAiScriptSample> topScriptsByTime = new List<ProfilingAiScriptSample>();
        public List<ProfilingAiScriptSample> topScriptsByGc = new List<ProfilingAiScriptSample>();
    }

    [Serializable]
    public sealed class ProfilingAiScriptSample
    {
        public string name;
        public float selfMs;
        public long gcBytes;
        public int calls;
    }

    [Serializable]
    public sealed class ProfilingAiCullingInfo
    {
        public string cameraName;
        public bool cameraOcclusionEnabled;
        public bool hasUmbraData;
        public int inFrustum;
        public int outFrustum;
        public int visible;
        public int likelyOccluded;
        public int occluderStatic;
        public int occludeeStatic;
        public List<string> topOutFrustum = new List<string>();
        public List<string> topLikelyOccluded = new List<string>();
        public List<string> missingOccludee = new List<string>();
    }

    [Serializable]
    public sealed class ProfilingAiBatchInfo
    {
        public int groupCount;
        public int multiBatchGroups;
        public int singleGroups;
        public int rendererCount;
        public List<ProfilingAiBatchGroup> topGroups = new List<ProfilingAiBatchGroup>();
    }

    [Serializable]
    public sealed class ProfilingAiBatchGroup
    {
        public string label;
        public int count;
        public bool canBatch;
    }

    [Serializable]
    public sealed class ProfilingAiNamedScore
    {
        public string name;
        public string detail;
        public float score;
    }

    [Serializable]
    public sealed class ProfilingAiIssue
    {
        public string id;
        public string severity; // info | warning | error
        public string category;
        public string title;
        public string detail;
        public string suggestedActionType;
        public string suggestedPayload;
    }

    /// <summary>描述 AI 可调用的动作（写入报告供模型阅读）。</summary>
    [Serializable]
    public sealed class ProfilingAiActionDef
    {
        public string type;
        public string description;
        public string payloadExample;
    }

    /// <summary>AI 回写的动作请求。</summary>
    [Serializable]
    public sealed class ProfilingAiActionRequest
    {
        public string type;
        /// <summary>命名前缀，如 P_</summary>
        public string namePrefix;
        public bool enable = true;
        public bool occluder;
        public bool occludee;
        public string cameraName;
        public List<string> objectPaths = new List<string>();
        public string note;
    }

    [Serializable]
    public sealed class ProfilingAiActionBatch
    {
        public string schemaVersion = "1.0";
        public List<ProfilingAiActionRequest> actions = new List<ProfilingAiActionRequest>();
    }

    [Serializable]
    public sealed class ProfilingAiActionResult
    {
        public bool ok;
        public string message;
        public int affected;
    }

    /// <summary>AI 导出可选块。</summary>
    [Flags]
    public enum AiExportSection
    {
        None = 0,
        Realtime = 1 << 0,
        Render = 1 << 1,
        Gc = 1 << 2,
        Culling = 1 << 3,
        Batch = 1 << 4,
        Issues = 1 << 5,
        AvailableActions = 1 << 6,
        FrameWindow = 1 << 7,
        Snapshot = Realtime | Render | Gc | Culling | Batch | Issues | AvailableActions,
        All = Snapshot | FrameWindow
    }

    /// <summary>分块 / 时段导出选项。</summary>
    public sealed class AiExportOptions
    {
        public AiExportSection Sections = AiExportSection.All;
        /// <summary>取缓冲末尾最近 N 秒；&lt;=0 时用索引范围。</summary>
        public float WindowSeconds = 30f;
        /// <summary>显式起止视觉索引（含）；均 &gt;=0 时优先于 WindowSeconds。</summary>
        public int StartIndex = -1;
        public int EndIndex = -1;
        public float TargetFps = 60f;
        public bool IncludeFrameSamples = true;
        public int MaxFrameSamples = 120;
        public string Focus = "fps_fluctuation";

        public static AiExportOptions Full() => new AiExportOptions { Sections = AiExportSection.Snapshot };

        public static AiExportOptions FpsFluctuation(float windowSeconds = 30f) => new AiExportOptions
        {
            Sections = AiExportSection.FrameWindow | AiExportSection.Realtime | AiExportSection.Issues,
            WindowSeconds = windowSeconds,
            Focus = "fps_fluctuation",
            IncludeFrameSamples = true
        };

        public bool Has(AiExportSection s) => (Sections & s) != 0;

        public bool IsFullSnapshot =>
            Sections == AiExportSection.Snapshot || Sections == AiExportSection.All;
    }
}
