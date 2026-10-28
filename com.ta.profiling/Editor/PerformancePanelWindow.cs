using System;
using System.Collections.Generic;
using System.Linq;
using TA.EditorCore;
using UnityEditor;
using UnityEngine;

namespace TA.Profiling.Editor
{
    /// <summary>
    /// TA 性能与渲染调试工具。左侧按「监控 / 诊断 / 优化」组织，顶部常驻关键指标。
    /// </summary>
    public sealed class PerformancePanelWindow : TAEditorWindow
    {
        private const string PrefsPrefix = "TA.Profiling.EditorPanel.";
        private const float NavWidth = 96f;

        private enum NavId
        {
            Monitor = 0,
            Diagnose = 1,
            Visual = 2,
            Optimize = 3,
            Ai = 4
        }

        private PerformanceMetrics _metrics;
        private GcScriptProfiler _gcProfiler;
        private readonly RenderStatsTracker _renderStats = new RenderStatsTracker();
        private float _sampleInterval = 0.25f;
        private double _lastEditorTime;
        private readonly float[] _fpsHistory = new float[60];
        private int _fpsHistoryIndex;
        private readonly MetricFrameHistory _frameHistory = new MetricFrameHistory();
        private bool _historyPaused;
        private int _selectedVisualIndex = -1;
        private MetricSeries _selectedSeries = MetricSeries.None;
        private Vector2 _frameDetailScroll;
        private bool _showSettings;

        private NavId _nav;
        private int _diagSub; // 0 GC, 1 渲染, 2 资源, 3 Shader
        private int _vizSub;  // 0 场景, 1 批次, 2 剔除
        private Vector2 _aiIssueScroll;
        private string _aiStatus = string.Empty;
        private ProfilingAiReport _aiReport;
        private bool _aiSecRealtime = true;
        private bool _aiSecRender = true;
        private bool _aiSecGc = true;
        private bool _aiSecCulling = true;
        private bool _aiSecBatch = true;
        private bool _aiSecIssues = true;
        private bool _aiSecActions = true;
        private bool _aiSecFrameWindow;
        private float _aiWindowSeconds = 30f;
        private bool _aiUseIndexRange;
        private int _aiWinStart;
        private int _aiWinEnd = MetricFrameHistory.Length - 1;
        private bool _aiIncludeSamples = true;
        private int _aiMaxSamples = 120;
        private ShaderPlatformInstrReport _shaderInstrReport;
        private Vector2 _shaderInstrScroll;
        private bool _shaderPlatD3D = true;
        private bool _shaderPlatVulkan = true;
        private bool _shaderPlatGles = false;
        private bool _shaderPlatMetal = true;
        private bool _shaderPreferSelection = true;
        private bool _shaderOnePerShader = true;
        private string _shaderInstrStatus = string.Empty;
        private string _shaderFilter = string.Empty;
        private readonly Dictionary<string, bool> _shaderFoldouts = new Dictionary<string, bool>();
        private ProfilingSessionReport _sessionReport;
        private float _reportTargetFps = 60f;
        private bool _showReportPanel;
        private Vector2 _reportScroll;
        private string _reportExportPath = string.Empty;
        private bool _showMemoryDetail;
        private MemoryDetailReport _memoryDetail;
        private Vector2 _contentScroll;
        private Vector2 _largeObjScroll;
        private Vector2 _leakScroll;
        private Vector2 _gcTimeScroll;
        private Vector2 _gcAllocScroll;
        private string _locateStatus = string.Empty;
        private GcScriptSnapshot _pinnedGcSnap;
        private bool _gcOnlyAssetsScripts = true;
        private Vector2 _lightScroll;
        private Vector2 _objectScroll;
        private Vector2 _batchListScroll;
        private Vector2 _batchVizScroll;
        private Vector2 _cullScroll;
        private int _cullListFilter; // 0 all, 1 out frustum, 2 occluded, 3 missing occludee
        private Vector2 _sceneVizScroll;

        private SceneResourceCostReport _costReport;
        private RenderImpactReport _impactReport;
        private string _objectFilter = string.Empty;
        private ResourceCategory? _categoryFilter;
        private int _objectSort;
        private bool _includeInactive = true;

        private string _batchNamePattern = "P_";
        private NameMatchMode _batchMatchMode = NameMatchMode.Prefix;
        private bool _batchIncludeInactive = true;
        private bool _batchOnlyRenderer = true;
        private bool _batchMatchRootsOnly;
        private List<BatchMatchEntry> _batchMatches;
        private string _batchStatus = string.Empty;

        protected override string HeaderTitle => "TA PROFILING";
        protected override string HeaderSubtitle => "NEON · MONITOR · DIAGNOSE · VISUAL · BATCH · AI";
        protected override bool UseScrollView => false;
        protected override RectOffset ContentPadding => new RectOffset(6, 6, 2, 6);

        [MenuItem("Tools/TA/Profiling/Performance Panel")]
        public static void Open()
        {
            var win = GetWindow<PerformancePanelWindow>();
            win.titleContent = new GUIContent("TA Profiling");
            win.minSize = new Vector2(640f, 480f);
            win.Show();
        }

        private void OnEnable()
        {
            _sampleInterval = EditorPrefs.GetFloat(PrefsPrefix + "interval", 0.25f);
            _nav = (NavId)Mathf.Clamp(EditorPrefs.GetInt(PrefsPrefix + "nav", 0), 0, 4);
            _diagSub = Mathf.Clamp(EditorPrefs.GetInt(PrefsPrefix + "diagSub", 0), 0, 3);
            _vizSub = Mathf.Clamp(EditorPrefs.GetInt(PrefsPrefix + "vizSub", 0), 0, 2);
            _includeInactive = EditorPrefs.GetBool(PrefsPrefix + "inactive", true);
            _batchNamePattern = EditorPrefs.GetString(PrefsPrefix + "batchPattern", "P_");
            _batchMatchMode = (NameMatchMode)EditorPrefs.GetInt(PrefsPrefix + "batchMode", 0);
            _gcOnlyAssetsScripts = EditorPrefs.GetBool(PrefsPrefix + "gcOnlyAssets", true);
            _reportTargetFps = EditorPrefs.GetFloat(PrefsPrefix + "reportTargetFps", 60f);

            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            _lastEditorTime = EditorApplication.timeSinceStartup;
            EnsureSamplers();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            BatchVisualization.Shutdown();
            CullingVisualization.Shutdown();
            SceneDebugVisualization.Shutdown();
            DisposeSamplers();
            EditorPrefs.SetFloat(PrefsPrefix + "interval", _sampleInterval);
            EditorPrefs.SetInt(PrefsPrefix + "nav", (int)_nav);
            EditorPrefs.SetInt(PrefsPrefix + "diagSub", _diagSub);
            EditorPrefs.SetInt(PrefsPrefix + "vizSub", _vizSub);
            EditorPrefs.SetBool(PrefsPrefix + "inactive", _includeInactive);
            EditorPrefs.SetString(PrefsPrefix + "batchPattern", _batchNamePattern ?? "P_");
            EditorPrefs.SetInt(PrefsPrefix + "batchMode", (int)_batchMatchMode);
            EditorPrefs.SetBool(PrefsPrefix + "gcOnlyAssets", _gcOnlyAssetsScripts);
            EditorPrefs.SetFloat(PrefsPrefix + "reportTargetFps", _reportTargetFps);
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                DisposeSamplers();
                EnsureSamplers();
                _frameHistory.Clear();
                _historyPaused = false;
                _selectedVisualIndex = -1;
                _selectedSeries = MetricSeries.None;
                _lastEditorTime = EditorApplication.timeSinceStartup;
                EditorApplication.delayCall += () =>
                {
                    if (EditorApplication.isPlaying)
                        MemoryDetailAnalyzer.CaptureBaseline();
                };
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                DisposeSamplers();
                MemoryDetailAnalyzer.ClearBaseline();
                _memoryDetail = null;
                _showMemoryDetail = false;
            }

            Repaint();
        }

        private void EnsureSamplers()
        {
            if (_metrics == null) _metrics = new PerformanceMetrics(_sampleInterval);
            if (_gcProfiler == null) _gcProfiler = new GcScriptProfiler();
        }

        private void DisposeSamplers()
        {
            _metrics?.Dispose();
            _metrics = null;
            _gcProfiler?.Dispose();
            _gcProfiler = null;
        }

        private void OnEditorUpdate()
        {
            if (!EditorApplication.isPlaying) return;
            // 工具内暂停：冻结曲线采样，便于回看；游戏 Pause 时也停录
            if (_historyPaused || EditorApplication.isPaused) return;

            EnsureSamplers();
            double now = EditorApplication.timeSinceStartup;
            float dt = (float)(now - _lastEditorTime);
            _lastEditorTime = now;
            if (dt <= 0f || dt > 1f)
                dt = Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime : 0.016f;

            _metrics.Tick(dt);
            _gcProfiler.Tick(dt);
            _renderStats.Tick();
            _fpsHistory[_fpsHistoryIndex % _fpsHistory.Length] = _metrics.Latest.Fps;
            _fpsHistoryIndex++;
            PushFrameSample();
            Repaint();
        }

        private void PushFrameSample()
        {
            if (_metrics == null || _gcProfiler == null) return;
            var s = _metrics.Latest;
            var sample = new MetricFrameSample
            {
                TimeSinceStartup = EditorApplication.timeSinceStartup,
                Fps = s.Fps,
                FrameMs = s.FrameMs,
                CpuMainThreadMs = s.CpuMainThreadMs,
                GpuFrameMs = s.GpuFrameMs,
                HasGpuTiming = s.HasGpuTiming,
                DrawCalls = _renderStats.DrawCalls,
                SetPassCalls = _renderStats.SetPassCalls,
                Batches = _renderStats.Batches,
                Triangles = _renderStats.Triangles,
                Vertices = _renderStats.Vertices,
                TotalAllocatedBytes = s.TotalAllocatedBytes,
                MonoUsedBytes = s.MonoUsedBytes,
                TextureBytes = s.TextureBytes,
                MeshBytes = s.MeshBytes,
                TextureCount = s.TextureCount,
                GcCollectDelta = _gcProfiler.CollectHistory.Length > 0
                    ? _gcProfiler.CollectHistory[(_gcProfiler.HistoryIndex - 1 + GcScriptProfiler.HistoryLength) %
                                                 GcScriptProfiler.HistoryLength]
                    : 0f,
                GcAllocBytes = _gcProfiler.AllocBytesPerFrame,
                GcGen0 = s.GcGen0,
                GcGen1 = s.GcGen1,
                GcGen2 = s.GcGen2,
                TopScriptsByTime = MetricFrameHistory.FormatTopScripts(_gcProfiler.Latest, false),
                TopScriptsByGc = MetricFrameHistory.FormatTopScripts(_gcProfiler.Latest, true)
            };
            _frameHistory.Push(sample);
        }

        private void SetHistoryPaused(bool paused)
        {
            if (_historyPaused == paused) return;
            _historyPaused = paused;
            if (!paused)
                _lastEditorTime = EditorApplication.timeSinceStartup;
        }

        protected override void DrawContent()
        {
            CyberpunkTheme.EnsureLoaded();
            CyberpunkTheme.DrawHudBanner("TA PROFILING", "cyber ops // 监控 · 诊断 · 可视 · 合批 · AI");
            GUILayout.Space(4f);
            DrawStatusBar();
            GUILayout.Space(4f);

            EditorGUILayout.BeginHorizontal();
            DrawNav();
            GUILayout.Space(6f);
            EditorGUILayout.BeginVertical();
            _contentScroll = EditorGUILayout.BeginScrollView(_contentScroll);
            DrawPage();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawStatusBar()
        {
            var rect = EditorGUILayout.GetControlRect(false, 28f);
            if (Event.current.type == EventType.Repaint)
            {
                if (CyberpunkTheme.PanelBg != null)
                {
                    GUI.DrawTexture(rect, CyberpunkTheme.PanelBg, ScaleMode.ScaleAndCrop);
                    EditorGUI.DrawRect(rect, new Color(0.02f, 0.04f, 0.1f, 0.72f));
                }
                else
                {
                    EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.35f));
                }

                CyberpunkTheme.DrawNeonFrame(rect, CyberpunkTheme.Cyan * 0.8f, CyberpunkTheme.Magenta * 0.7f, 1f);
            }

            bool play = EditorApplication.isPlaying;
            float fps = play && _metrics != null ? _metrics.Latest.Fps : 0f;
            float cps = _gcProfiler != null ? _gcProfiler.CollectsPerSecond : 0f;

            float x = rect.x + 8f;
            void Chip(string text, Color tint)
            {
                // 固定尺寸，不随文案长短拉伸；顶栏整体仍随窗口宽度自适应
                var r = new Rect(x, rect.y + 4f, CyberpunkTheme.StatusChipWidth, CyberpunkTheme.StatusChipHeight);
                CyberpunkTheme.DrawChip(r, text, tint);
                x = r.xMax + 6f;
            }

            Chip(play ? "PLAY" : "EDIT", play ? CyberpunkTheme.Lime : CyberpunkTheme.TextDim);
            if (play)
            {
                Chip($"FPS {fps:F0}", fps < 30f ? CyberpunkTheme.Danger : CyberpunkTheme.Cyan);
                Chip($"DC {_renderStats.DrawCalls}", CyberpunkTheme.Magenta);
                Chip($"SP {_renderStats.SetPassCalls}", CyberpunkTheme.Cyan);
                Chip($"GC {cps:F1}/s", cps > 2f ? CyberpunkTheme.Amber : CyberpunkTheme.Lime);
                if (_historyPaused)
                    Chip("PAUSED", CyberpunkTheme.Amber);
            }

            var gear = new Rect(rect.xMax - 28f, rect.y + 3f, 22f, 20f);
            if (GUI.Button(gear, "⚙", EditorStyles.miniButton))
                _showSettings = !_showSettings;

            if (_showSettings)
            {
                EditorGUI.BeginChangeCheck();
                _sampleInterval = EditorGUILayout.Slider("采样间隔 (s)", _sampleInterval, 0.05f, 1f);
                if (EditorGUI.EndChangeCheck())
                {
                    _metrics?.Dispose();
                    _metrics = new PerformanceMetrics(_sampleInterval);
                }
            }
        }

        private void DrawNav()
        {
            CyberpunkTheme.BeginNavRail(NavWidth);
            GUILayout.Space(6f);
            if (CyberpunkTheme.NavButton(_nav == NavId.Monitor, "监控") && _nav != NavId.Monitor)
                SwitchNav(NavId.Monitor);
            if (CyberpunkTheme.NavButton(_nav == NavId.Diagnose, "诊断") && _nav != NavId.Diagnose)
                SwitchNav(NavId.Diagnose);
            if (CyberpunkTheme.NavButton(_nav == NavId.Visual, "可视") && _nav != NavId.Visual)
                SwitchNav(NavId.Visual);
            if (CyberpunkTheme.NavButton(_nav == NavId.Optimize, "合批") && _nav != NavId.Optimize)
                SwitchNav(NavId.Optimize);
            if (CyberpunkTheme.NavButton(_nav == NavId.Ai, "AI") && _nav != NavId.Ai)
                SwitchNav(NavId.Ai);
            GUILayout.FlexibleSpace();
            var hint = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, alignment = TextAnchor.MiddleCenter };
            hint.normal.textColor = CyberpunkTheme.Magenta;
            GUILayout.Label("NEON\nCORE", hint);
            GUILayout.Space(8f);
            CyberpunkTheme.EndNavRail();
        }

        private void NavItem(NavId id, string label)
        {
            // 保留兼容；导航已改用 CyberpunkTheme.NavButton
            if (CyberpunkTheme.NavButton(_nav == id, label) && _nav != id)
                SwitchNav(id);
        }

        private void OpenDiagnose(int sub)
        {
            _diagSub = sub;
            SwitchNav(NavId.Diagnose);
        }

        private void OpenVisual(int sub)
        {
            _vizSub = sub;
            SwitchNav(NavId.Visual);
        }

        private void SwitchNav(NavId id)
        {
            _nav = id;
            EditorApplication.delayCall += () =>
            {
                OnEnterPage();
                Repaint();
            };
        }

        private void OnEnterPage()
        {
            if (_nav == NavId.Diagnose)
            {
                if (_diagSub == 0) _gcProfiler?.ForceSampleHierarchy();
                if (_diagSub == 1 && _impactReport == null)
                    _impactReport = RenderImpactAnalyzer.Scan();
                if (_diagSub == 2 && _costReport == null)
                    _costReport = SceneResourceCostAnalyzer.Scan(_includeInactive);
                // Shader 页需手动点扫描（编译较慢）
            }
            else if (_nav == NavId.Visual)
            {
                if (_vizSub == 0) SceneDebugVisualization.Rescan();
                if (_vizSub == 1) BatchVisualization.Rescan();
                if (_vizSub == 2) CullingVisualization.Rescan();
            }
            else if (_nav == NavId.Ai && _aiReport == null)
            {
                _aiReport = ProfilingAiBridge.CollectReport(BuildAiExportOptions(), _frameHistory);
            }
        }

        private void DrawPage()
        {
            switch (_nav)
            {
                case NavId.Monitor:
                    DrawMonitor();
                    break;
                case NavId.Diagnose:
                    DrawSubTabs(ref _diagSub, new[] { "GC / 脚本", "渲染影响", "资源开销", "Shader" });
                    GUILayout.Space(4f);
                    if (_diagSub == 0) DrawGcScript();
                    else if (_diagSub == 1) DrawRenderImpact();
                    else if (_diagSub == 2) DrawResourceCost();
                    else DrawShaderPlatformInstr();
                    break;
                case NavId.Visual:
                    DrawSubTabs(ref _vizSub, new[] { "场景着色", "批次", "视锥/遮挡" });
                    GUILayout.Space(4f);
                    if (_vizSub == 0) DrawSceneViz();
                    else if (_vizSub == 1) DrawBatchViz();
                    else DrawCulling();
                    break;
                case NavId.Optimize:
                    DrawNameBatch();
                    break;
                case NavId.Ai:
                    DrawAiAssist();
                    break;
            }
        }

        private void DrawSubTabs(ref int index, string[] labels)
        {
            CyberpunkTheme.BeginToolbarStrip();
            int next = GUILayout.Toolbar(index, labels, GUILayout.Height(22f));
            CyberpunkTheme.EndToolbarStrip();
            if (next == index) return;
            index = next;
            EditorApplication.delayCall += () =>
            {
                OnEnterPage();
                Repaint();
            };
        }

        private static void NavSection(string title)
        {
            // 保留空实现避免旧调用；现导航已扁平化
        }

        // ─── 监控总览 ───

        private void DrawMonitor()
        {
            if (!EditorApplication.isPlaying)
            {
                CyberpunkTheme.DrawInfoBanner("进入 Play 后刷新实时指标。可停靠到 Game 视图旁。");
            }

            var snap = EditorApplication.isPlaying && _metrics != null ? _metrics.Latest : (PerformanceSnapshot?)null;
            bool has = snap.HasValue;
            var s = has ? snap.Value : default;

            // 第一行：帧 & 算力
            EditorGUILayout.BeginHorizontal();
            MetricCard("FPS", has ? $"{s.Fps:F1}" : "—", has ? $"{s.FrameMs:F1} ms" : "",
                has ? FpsColor(s.Fps) : Color.gray, () =>
                {
                    DrawSeriesSparkline(MetricSeries.Fps, 0f, 120f, has ? FpsColor(s.Fps) : Color.gray, 26f);
                });
            MetricCard("CPU", has ? $"{s.CpuMainThreadMs:F1}" : "—", "ms 主线程",
                has && s.CpuMainThreadMs > 16f ? Warn : Ok, () =>
                {
                    if (!has) return;
                    GUILayout.Label(s.HasGpuTiming ? $"GPU {s.GpuFrameMs:F1} ms" : "GPU —", CyberpunkTheme.MiniLabelStyle);
                    DrawBudgetBar(GUILayoutUtility.GetRect(
                            CyberpunkTheme.MetricCardWidth - 16f, 6f,
                            GUILayout.Width(CyberpunkTheme.MetricCardWidth - 16f),
                            GUILayout.Height(6f),
                            GUILayout.ExpandWidth(false)),
                        s.CpuMainThreadMs, s.HasGpuTiming ? s.GpuFrameMs : 0f);
                });
            MetricCard("DrawCall", $"{_renderStats.DrawCalls}", $"峰值 {_renderStats.DrawCallsPeak}",
                Accent, () =>
                {
                    DrawSeriesSparkline(MetricSeries.DrawCall, 0f,
                        Mathf.Max(10f, _frameHistory.GetSeriesMax(MetricSeries.DrawCall, _renderStats.DrawCallsPeak)),
                        Accent, 26f);
                });
            MetricCard("SetPass", $"{_renderStats.SetPassCalls}", $"峰值 {_renderStats.SetPassPeak}",
                Blue, () =>
                {
                    DrawSeriesSparkline(MetricSeries.SetPass, 0f,
                        Mathf.Max(5f, _frameHistory.GetSeriesMax(MetricSeries.SetPass, _renderStats.SetPassPeak)),
                        Blue, 26f);
                });
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(4f);
            EditorGUILayout.BeginHorizontal();
            MetricCard("内存", has ? PerformanceMetrics.FormatBytes(s.TotalAllocatedBytes) : "—",
                has ? $"堆 {PerformanceMetrics.FormatBytes(s.MonoUsedBytes)}" : "点击展开",
                Color.white, null, ToggleMemoryDetail);
            MetricCard("纹理", has ? PerformanceMetrics.FormatBytes(s.TextureBytes) : "—",
                has ? $"{s.TextureCount} 张 · 网格 {PerformanceMetrics.FormatBytes(s.MeshBytes)}" : "", Color.white);
            float cps = _gcProfiler != null ? _gcProfiler.CollectsPerSecond : 0f;
            MetricCard("GC", has ? $"{cps:F1}/s" : "—",
                has ? $"Alloc {(_gcProfiler?.AllocBytesPerFrame ?? 0f):F0} B" : "点此详查",
                cps > 2f ? Warn : Color.white, () =>
                {
                    DrawSeriesSparkline(MetricSeries.GcCollect, 0f, 3f, cps > 2f ? Warn : Blue, 22f);
                }, () => OpenDiagnose(0));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(4f);
            CyberpunkTheme.BeginToolbarStrip();
            GUILayout.Label($"Batches {_renderStats.Batches}   Tris {_renderStats.Triangles:N0}   Verts {_renderStats.Vertices:N0}",
                CyberpunkTheme.MiniLabelStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_historyPaused ? "继续录制" : "暂停查看", EditorStyles.toolbarButton, GUILayout.Width(64f)))
                SetHistoryPaused(!_historyPaused);
            if (GUILayout.Button("清选中", EditorStyles.toolbarButton, GUILayout.Width(48f)))
            {
                _selectedVisualIndex = -1;
                _selectedSeries = MetricSeries.None;
            }

            if (GUILayout.Button("清峰值", EditorStyles.toolbarButton, GUILayout.Width(52f)))
                _renderStats.ResetPeaks();
            if (GUILayout.Button("生成报告", EditorStyles.toolbarButton, GUILayout.Width(64f)))
            {
                _sessionReport = ProfilingReportExporter.Build(_frameHistory, _reportTargetFps, _sampleInterval);
                _showReportPanel = true;
            }

            if (GUILayout.Button("导出报告", EditorStyles.toolbarButton, GUILayout.Width(64f)))
                ExportSessionReport(true);
            if (GUILayout.Button("GC", EditorStyles.toolbarButton, GUILayout.Width(36f)))
                OpenDiagnose(0);
            if (GUILayout.Button("批次", EditorStyles.toolbarButton, GUILayout.Width(40f)))
                OpenVisual(1);
            if (GUILayout.Button("剔除", EditorStyles.toolbarButton, GUILayout.Width(40f)))
                OpenVisual(2);
            if (GUILayout.Button("可视", EditorStyles.toolbarButton, GUILayout.Width(40f)))
                OpenVisual(0);
            CyberpunkTheme.EndToolbarStrip();

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _reportTargetFps = EditorGUILayout.Slider("报告目标 FPS", _reportTargetFps, 30f, 120f);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetFloat(PrefsPrefix + "reportTargetFps", _reportTargetFps);
            GUILayout.Label($"缓冲 {_frameHistory.Filled}/{MetricFrameHistory.Length}", CyberpunkTheme.MiniLabelStyle, GUILayout.Width(90f));
            EditorGUILayout.EndHorizontal();

            if (_historyPaused || _selectedVisualIndex >= 0)
            {
                CyberpunkTheme.DrawInfoBanner(
                    _historyPaused
                        ? "已暂停录制。单击任意曲线采样点，查看该帧指标与可能来源。"
                        : "已选中采样点。可点「暂停查看」冻结曲线。");
            }

            if (_showReportPanel)
                DrawSessionReportPanel();

            DrawSelectedFrameDetail();

            if (_showMemoryDetail)
                DrawMemoryDetailPanel();
        }

        private void ExportSessionReport(bool reveal)
        {
            _sessionReport = ProfilingReportExporter.Build(_frameHistory, _reportTargetFps, _sampleInterval);
            _showReportPanel = true;
            if (_sessionReport.sampleCount <= 0)
            {
                _reportExportPath = string.Empty;
                Debug.LogWarning("[TA.Profiling] 无采样数据，无法导出报告。");
                return;
            }

            string path = ProfilingReportExporter.Export(_sessionReport, true);
            _reportExportPath = path;
            Debug.Log($"[TA.Profiling] 已导出性能报告：{path}");
            if (reveal)
                EditorUtility.RevealInFinder(path);
        }

        private void DrawSessionReportPanel()
        {
            if (_sessionReport == null)
                _sessionReport = ProfilingReportExporter.Build(_frameHistory, _reportTargetFps, _sampleInterval);

            var r = _sessionReport;
            CyberpunkTheme.BeginContentPanel(false, GUILayout.ExpandWidth(true));
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("时段报告（均值 / 跳帧）", CyberpunkTheme.SectionTitleStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("刷新统计", EditorStyles.miniButton, GUILayout.Width(64f)))
                _sessionReport = ProfilingReportExporter.Build(_frameHistory, _reportTargetFps, _sampleInterval);
            if (GUILayout.Button("导出", EditorStyles.miniButton, GUILayout.Width(40f)))
                ExportSessionReport(true);
            if (GUILayout.Button("收起", EditorStyles.miniButton, GUILayout.Width(40f)))
                _showReportPanel = false;
            EditorGUILayout.EndHorizontal();

            if (r.sampleCount <= 0)
            {
                CyberpunkTheme.DrawInfoBanner("暂无数据。Play 后录制一段时间再生成报告。");
                CyberpunkTheme.EndContentPanel();
                return;
            }

            CyberpunkTheme.DrawInfoBanner(r.summary);

            EditorGUILayout.BeginHorizontal();
            MetricCard("FPS 均值", $"{r.fpsAvg:F1}", $"1% {r.fpsP1Low:F1} · 5% {r.fpsP5Low:F1}", FpsColor(r.fpsAvg));
            MetricCard("帧耗时", $"{r.frameMsAvg:F2}", $"max {r.frameMsMax:F2} ms", r.frameMsAvg > r.frameBudgetMs ? Warn : Ok);
            MetricCard("跳帧", $"{r.stutterCount}", $"{r.stutterRatePct:F1}% >{r.frameBudgetMs:F1}ms",
                r.stutterRatePct > 15f ? Danger : r.stutterRatePct > 5f ? Warn : Ok);
            MetricCard("Hitch", $"{r.hitchCount}", $"严重 {r.severeHitchCount}",
                r.hitchCount > 0 ? Warn : Ok);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(4f);
            EditorGUILayout.BeginHorizontal();
            MetricCard("DC 均值", $"{r.drawCallAvg:F0}", $"峰值 {r.drawCallMax:F0}", Accent);
            MetricCard("SetPass", $"{r.setPassAvg:F0}", $"峰值 {r.setPassMax:F0}", Blue);
            MetricCard("内存", $"{r.memAvgMb:F1} MB", $"峰值 {r.memMaxMb:F1} MB", Color.white);
            MetricCard("GC.Alloc", $"{r.gcAllocAvg:F0} B", $"峰值 {r.gcAllocMax:F0} B", Orange);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(4f);
            GUILayout.Label(
                $"时长 {r.durationSec:F1}s · 采样 {r.sampleCount} · 目标 {r.targetFps:F0} FPS · " +
                $"CPU {r.cpuMsAvg:F2}/{r.cpuMsMax:F2} ms" +
                (r.hasGpuTiming ? $" · GPU {r.gpuMsAvg:F2}/{r.gpuMsMax:F2} ms" : ""),
                CyberpunkTheme.MiniLabelStyle);

            if (!string.IsNullOrEmpty(_reportExportPath))
                GUILayout.Label("已导出：" + _reportExportPath, CyberpunkTheme.MiniLabelStyle);

            _reportScroll = EditorGUILayout.BeginScrollView(_reportScroll, GUILayout.MaxHeight(160f));
            EditorGUILayout.TextArea(r.markdownPreview ?? "", GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            CyberpunkTheme.EndContentPanel();
        }

        // ─── GC / 脚本 ───

        private void DrawGcScript()
        {
            EnsureSamplers();
            var snap = _pinnedGcSnap ?? _gcProfiler?.Latest;

            EditorGUILayout.BeginHorizontal();
            if (TAEditorGUI.PrimaryButton("刷新采样", 24f))
            {
                _pinnedGcSnap = null;
                ProfilingLocator.ClearAssetsScriptCache();
                _gcProfiler?.ForceSampleHierarchy();
            }

            if (GUILayout.Button(_pinnedGcSnap != null ? "解除冻结" : "冻结列表", GUILayout.Width(72f), GUILayout.Height(24f)))
            {
                if (_pinnedGcSnap != null)
                    _pinnedGcSnap = null;
                else if (_gcProfiler?.Latest != null)
                    _pinnedGcSnap = CloneGcSnap(_gcProfiler.Latest);
            }

            EditorGUI.BeginChangeCheck();
            _gcOnlyAssetsScripts = GUILayout.Toggle(
                _gcOnlyAssetsScripts, "仅 Assets 脚本", GUILayout.Width(108f), GUILayout.Height(24f));
            if (EditorGUI.EndChangeCheck())
                ProfilingLocator.ClearAssetsScriptCache();

            GUILayout.Label(snap?.Status ?? "", CyberpunkTheme.MiniLabelStyle);
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_locateStatus))
                CyberpunkTheme.DrawInfoBanner(_locateStatus);

            EditorGUILayout.BeginHorizontal();
            MetricCard("触发频率", $"{_gcProfiler?.CollectsPerSecond ?? 0f:F2}", "次/秒", Warn, () =>
            {
                DrawSeriesSparkline(MetricSeries.GcCollect, 0f, 4f, Warn, 32f);
            });
            MetricCard("GC.Alloc", $"{_gcProfiler?.AllocBytesPerFrame ?? 0f:F0}", "B / 帧", Orange, () =>
            {
                float a = _gcProfiler?.AllocBytesPerFrame ?? 0f;
                DrawSeriesSparkline(MetricSeries.GcAlloc, 0f, Mathf.Max(1024f, a * 2f), Orange, 32f);
            });
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (_selectedVisualIndex >= 0)
                DrawSelectedFrameDetail();

            GUILayout.Space(4f);
            CyberpunkTheme.DrawInfoBanner(
                _gcOnlyAssetsScripts
                    ? "已隐藏引擎/Package 内部项，仅显示 Assets 下脚本。单击可定位。"
                    : "显示全部 Profiler 样本。勾选「仅 Assets 脚本」可过滤引擎内部项。");
            EditorGUILayout.BeginHorizontal();
            DrawListPanel("耗时脚本 (Self ms)", ref _gcTimeScroll, () => DrawScriptRows(snap?.TopByTime, false));
            DrawListPanel("高频 GC 源头", ref _gcAllocScroll, () => DrawScriptRows(snap?.TopByGc, true));
            EditorGUILayout.EndHorizontal();
        }

        private static GcScriptSnapshot CloneGcSnap(GcScriptSnapshot src)
        {
            var dst = new GcScriptSnapshot
            {
                GcAllocBytesPerFrame = src.GcAllocBytesPerFrame,
                GcCollectsPerSecond = src.GcCollectsPerSecond,
                Gen0 = src.Gen0,
                Gen1 = src.Gen1,
                Gen2 = src.Gen2,
                ProfilerEnabled = src.ProfilerEnabled,
                Status = src.Status + " · 已冻结"
            };
            dst.TopByTime.AddRange(src.TopByTime);
            dst.TopByGc.AddRange(src.TopByGc);
            return dst;
        }

        private static void DrawListPanel(string title, ref Vector2 scroll, Action body)
        {
            CyberpunkTheme.BeginContentPanel(true, GUILayout.ExpandWidth(true));
            GUILayout.Label(title, CyberpunkTheme.SectionTitleStyle);
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(240f));
            body?.Invoke();
            EditorGUILayout.EndScrollView();
            CyberpunkTheme.EndContentPanel();
        }

        private void DrawScriptRows(List<ScriptCostEntry> list, bool showGc)
        {
            if (list == null || list.Count == 0)
            {
                GUILayout.Label(EditorApplication.isPlaying ? "等待 Profiler 数据" : "需 Play", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            int shown = 0;
            foreach (var e in list)
            {
                if (_gcOnlyAssetsScripts && !ProfilingLocator.IsAssetsScriptSample(e.Name))
                    continue;

                shown++;
                EditorGUILayout.BeginVertical();
                EditorGUILayout.BeginHorizontal();
                string label = Truncate(e.Name, 36);
                if (GUILayout.Button(label, EditorStyles.linkLabel, GUILayout.MinWidth(80f)))
                {
                    if (_pinnedGcSnap == null && _gcProfiler?.Latest != null)
                        _pinnedGcSnap = CloneGcSnap(_gcProfiler.Latest);

                    bool ok = ProfilingLocator.TryLocateProfilerSample(e.Name, out string msg);
                    _locateStatus = (ok ? "✓ " : "✗ ") + msg;
                    Debug.Log(ok
                        ? $"[TA.Profiling] {msg}"
                        : $"[TA.Profiling] 定位失败 · {msg}");
                }

                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
                GUILayout.Label(showGc ? PerformanceMetrics.FormatBytes(e.GcBytes) : $"{e.SelfMs:F2} ms", GUILayout.Width(60f));
                EditorGUILayout.EndHorizontal();
                float w = showGc ? Mathf.Clamp01(e.GcBytes / (256f * 1024f)) : Mathf.Clamp01(e.SelfMs / 5f);
                var bar = GUILayoutUtility.GetRect(8f, 3f, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(bar, new Color(0f, 0f, 0f, 0.08f));
                EditorGUI.DrawRect(new Rect(bar.x, bar.y, bar.width * w, bar.height), showGc ? Orange : Blue);
                EditorGUILayout.EndVertical();
            }

            if (shown == 0)
            {
                GUILayout.Label(
                    _gcOnlyAssetsScripts
                        ? "当前没有命中 Assets 脚本（取消勾选可看全部）"
                        : "无数据",
                    EditorStyles.centeredGreyMiniLabel);
            }
        }

        // ─── 渲染影响 ───

        private void DrawRenderImpact()
        {
            EditorGUILayout.BeginHorizontal();
            if (TAEditorGUI.PrimaryButton("扫描场景", 24f))
                _impactReport = RenderImpactAnalyzer.Scan();
            GUILayout.FlexibleSpace();
            GUILayout.Label($"DC {_renderStats.DrawCalls}  SP {_renderStats.SetPassCalls}", CyberpunkTheme.MiniLabelStyle);
            EditorGUILayout.EndHorizontal();

            DrawSeriesSparkline(MetricSeries.DrawCall, 0f,
                Mathf.Max(10f, _frameHistory.GetSeriesMax(MetricSeries.DrawCall, _renderStats.DrawCallsPeak)),
                Accent, 28f);

            if (_impactReport == null)
            {
                GUILayout.Label("扫描光源 / 阴影 / 后处理开销", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var r = _impactReport;
            EditorGUILayout.BeginHorizontal();
            ScoreChip("光照", r.LightingScore, $"{r.RealtimeLights} 实时");
            ScoreChip("阴影", r.ShadowScore, $"{r.ShadowCastingLights} 投影");
            ScoreChip("后处理", r.PostScore, $"{r.ActivePostVolumes} Vol");
            ScoreChip("探针", r.ActiveReflectionProbes * 8f, $"{r.ActiveReflectionProbes}/{r.ReflectionProbes}");
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (r.PostEffects.Count > 0)
                GUILayout.Label("效果: " + string.Join(" · ", r.PostEffects), CyberpunkTheme.MiniLabelStyle);

            if (!string.IsNullOrEmpty(_locateStatus))
                CyberpunkTheme.DrawInfoBanner(_locateStatus);

            GUILayout.Label("光源（单击定位）", CyberpunkTheme.SectionTitleStyle);
            CyberpunkTheme.BeginContentPanel(true);
            _lightScroll = EditorGUILayout.BeginScrollView(_lightScroll, GUILayout.MinHeight(160f));
            foreach (var L in r.Lights)
            {
                CyberpunkTheme.BeginRow();
                if (GUILayout.Button(Truncate(L.Name, 20), EditorStyles.linkLabel, GUILayout.Width(120f)))
                {
                    bool ok = ProfilingLocator.TryLocate(L.Light != null ? L.Light.gameObject : null, out string msg);
                    _locateStatus = (ok ? "✓ " : "✗ ") + msg;
                }

                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
                GUILayout.Label(L.Impact, GUILayout.MinWidth(100f));
                GUILayout.Label($"{L.Score:F0}", GUILayout.Width(36f));
                var bar = GUILayoutUtility.GetRect(50f, 8f, GUILayout.Width(70f));
                EditorGUI.DrawRect(bar, new Color(0f, 0f, 0f, 0.1f));
                EditorGUI.DrawRect(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(L.Score / 100f), bar.height),
                    L.Shadows != LightShadows.None ? Warn : Ok);
                CyberpunkTheme.EndRow();
            }

            EditorGUILayout.EndScrollView();
            CyberpunkTheme.EndContentPanel();

            if (r.Volumes.Count > 0)
            {
                GUILayout.Label("后处理 Volume（单击定位）", CyberpunkTheme.SectionTitleStyle);
                CyberpunkTheme.BeginContentPanel(false);
                foreach (var v in r.Volumes)
                {
                    CyberpunkTheme.BeginRow();
                    if (GUILayout.Button(Truncate(v.Name, 20), EditorStyles.linkLabel, GUILayout.Width(120f)))
                    {
                        bool ok = ProfilingLocator.TryLocate(v.Volume, out string msg);
                        _locateStatus = (ok ? "✓ " : "✗ ") + msg;
                    }

                    EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
                    GUILayout.Label(v.Active ? "ON" : "off", GUILayout.Width(28f));
                    GUILayout.Label(Truncate(v.Effects ?? "", 40), CyberpunkTheme.MiniLabelStyle);
                    CyberpunkTheme.EndRow();
                }

                CyberpunkTheme.EndContentPanel();
            }

            if (r.Probes.Count > 0)
            {
                GUILayout.Label("反射探针（单击定位）", CyberpunkTheme.SectionTitleStyle);
                CyberpunkTheme.BeginContentPanel(false);
                foreach (var p in r.Probes)
                {
                    CyberpunkTheme.BeginRow();
                    if (GUILayout.Button(Truncate(p.Name, 20), EditorStyles.linkLabel, GUILayout.Width(120f)))
                    {
                        bool ok = ProfilingLocator.TryLocate(p.Probe, out string msg);
                        _locateStatus = (ok ? "✓ " : "✗ ") + msg;
                    }

                    EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
                    GUILayout.Label(p.Active ? "Active" : "Inactive", CyberpunkTheme.MiniLabelStyle);
                    CyberpunkTheme.EndRow();
                }

                CyberpunkTheme.EndContentPanel();
            }
        }

        private static void ScoreChip(string title, float score, string detail)
        {
            CyberpunkTheme.BeginMetricCard();
            GUILayout.Label(title, CyberpunkTheme.CardTitle);
            var c = score > 60f ? Danger : score > 30f ? Warn : Ok;
            var valueStyle = new GUIStyle(CyberpunkTheme.CardValueStyle);
            valueStyle.normal.textColor = c;
            GUILayout.Label($"{score:F0}", valueStyle);
            GUILayout.Label(detail, CyberpunkTheme.MiniLabelStyle);
            CyberpunkTheme.EndMetricCard();
        }

        // ─── Shader 多平台编译 / 指令数（TA）───

        private void DrawShaderPlatformInstr()
        {
            CyberpunkTheme.DrawInfoBanner(
                "同一 Shader 的不同 Pass/Keywords 不是重复。默认「每Shader一份」；需要对比材质变体时再关掉。");

            EditorGUILayout.BeginHorizontal();
            _shaderPlatD3D = GUILayout.Toggle(_shaderPlatD3D, "Win D3D", GUILayout.Width(72f));
            _shaderPlatVulkan = GUILayout.Toggle(_shaderPlatVulkan, "Vulkan", GUILayout.Width(64f));
            _shaderPlatGles = GUILayout.Toggle(_shaderPlatGles, "GLES3", GUILayout.Width(56f));
            _shaderPlatMetal = GUILayout.Toggle(_shaderPlatMetal, "Metal", GUILayout.Width(56f));
            _shaderPreferSelection = GUILayout.Toggle(_shaderPreferSelection, "含选中", GUILayout.Width(60f));
            _includeInactive = GUILayout.Toggle(_includeInactive, "未激活", GUILayout.Width(60f));
            _shaderOnePerShader = GUILayout.Toggle(_shaderOnePerShader, "每Shader一份", GUILayout.Width(88f));
            EditorGUILayout.EndHorizontal();

            if (!_shaderOnePerShader)
            {
                EditorGUILayout.HelpBox(
                    "关闭「每Shader一份」后，会按材质 Keywords 展开多份变体（同一 URP/Lit 会重复出现，Pass/Keywords 不同）。",
                    MessageType.Info);
            }

            EditorGUILayout.BeginHorizontal();
            if (TAEditorGUI.PrimaryButton("扫描编译", 24f))
                RunShaderPlatformScan();
            if (GUILayout.Button("复制 Markdown", GUILayout.Height(24f), GUILayout.Width(100f)))
            {
                if (_shaderInstrReport == null)
                    _shaderInstrStatus = "请先扫描";
                else
                {
                    EditorGUIUtility.systemCopyBuffer = ShaderPlatformInstrAnalyzer.ToMarkdown(_shaderInstrReport);
                    _shaderInstrStatus = "已复制 Markdown 报告";
                }
            }

            if (GUILayout.Button("打开 Compiled", GUILayout.Height(24f), GUILayout.Width(96f)))
            {
                var sh = Selection.activeObject as Shader;
                if (sh == null && Selection.activeObject is Material mat)
                    sh = mat.shader;
                if (sh != null)
                {
                    if (ShaderPlatformInstrAnalyzer.TryOpenCompiledShader(sh, out string err))
                        _shaderInstrStatus = "已打开编译代码：" + sh.name;
                    else
                        _shaderInstrStatus = err;
                }
                else
                    _shaderInstrStatus = "请在 Project 选中 Shader 或 Material";
            }

            _shaderFilter = EditorGUILayout.TextField(_shaderFilter);
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_shaderInstrStatus))
                EditorGUILayout.LabelField(_shaderInstrStatus, CyberpunkTheme.MiniLabelStyle);

            if (_shaderInstrReport == null)
            {
                GUILayout.Label("选择平台后点「扫描编译」。可先在 Hierarchy/Project 选中物体缩小范围。",
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }

            EditorGUILayout.LabelField(_shaderInstrReport.summary, CyberpunkTheme.MiniLabelStyle);

            // 汇总卡片：各平台最大指令
            EditorGUILayout.BeginHorizontal();
            MetricCard("着色器", $"{_shaderInstrReport.shaderCount}", $"Pass {_shaderInstrReport.rowCount}", CyberpunkTheme.Cyan);
            int maxInstr = 0;
            int fail = 0;
            foreach (var row in _shaderInstrReport.rows)
            {
                if (row.MaxInstructions > maxInstr) maxInstr = row.MaxInstructions;
                foreach (var st in row.stages)
                    if (!st.success) fail++;
            }

            MetricCard("峰值指令", maxInstr > 0 ? maxInstr.ToString() : "—", "跨平台 max",
                maxInstr > 200 ? Warn : Ok);
            MetricCard("编译失败", $"{fail}", "stage 数", fail > 0 ? Warn : Ok);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            _shaderInstrScroll = EditorGUILayout.BeginScrollView(_shaderInstrScroll, GUILayout.MinHeight(260f));
            string filter = (_shaderFilter ?? string.Empty).Trim();

            // 按 Shader 名合并，避免「看起来一样」的重复大标题
            var groups = new Dictionary<string, List<ShaderPassCompileRow>>();
            foreach (var row in _shaderInstrReport.rows)
            {
                if (!string.IsNullOrEmpty(filter)
                    && row.shaderName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && (row.passName == null
                        || row.passName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    && (row.keywordsJoined == null
                        || row.keywordsJoined.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                if (!groups.TryGetValue(row.shaderName, out var list))
                {
                    list = new List<ShaderPassCompileRow>();
                    groups[row.shaderName] = list;
                }

                list.Add(row);
            }

            foreach (var kv in groups.OrderByDescending(g => g.Value.Max(r => r.MaxBytecode)))
            {
                string shaderName = kv.Key;
                var rows = kv.Value;
                if (!_shaderFoldouts.TryGetValue(shaderName, out bool open))
                    open = true;

                CyberpunkTheme.BeginContentPanel(false);
                EditorGUILayout.BeginHorizontal();
                open = EditorGUILayout.Foldout(open, shaderName, true);
                _shaderFoldouts[shaderName] = open;
                GUILayout.FlexibleSpace();
                GUILayout.Label(
                    $"{rows.Count} Pass/变体 · {(rows[0].srpBatcherOk ? "SRP✓" : "SRP?")}",
                    CyberpunkTheme.MiniLabelStyle);
                if (GUILayout.Button("选中", GUILayout.Width(44f), GUILayout.Height(18f)))
                {
                    var found = Shader.Find(shaderName);
                    if (found != null)
                    {
                        Selection.activeObject = found;
                        EditorGUIUtility.PingObject(found);
                    }
                }

                EditorGUILayout.EndHorizontal();

                if (open)
                {
                    foreach (var row in rows)
                    {
                        GUILayout.Space(2f);
                        GUILayout.Label(
                            $"▸ {row.passName}  ·  kw {row.keywordCount}"
                            + (string.IsNullOrEmpty(row.keywordsJoined) ? " (默认)" : $" [{row.keywordsJoined}]"),
                            CyberpunkTheme.MiniLabelStyle);

                        foreach (var st in row.stages)
                        {
                            string instr = st.instructionCount >= 0 ? st.instructionCount.ToString() : "—";
                            var style = new GUIStyle(CyberpunkTheme.MiniLabelStyle);
                            if (!st.success) style.normal.textColor = CyberpunkTheme.Danger;
                            else if (st.instructionCount >= 200) style.normal.textColor = CyberpunkTheme.Amber;
                            else if (st.instructionCount >= 0) style.normal.textColor = CyberpunkTheme.Lime;

                            GUILayout.Label(
                                $"    {st.platformLabel} {st.stageLabel}  Instr {instr}  " +
                                $"{st.bytecodeBytes} B  Tex {st.textureBindings}  CB {st.constantBuffers}  Attr {st.attributes}"
                                + (string.IsNullOrEmpty(st.message) ? "" : $"  · {st.message}"),
                                style);
                        }
                    }
                }

                CyberpunkTheme.EndContentPanel();
            }

            EditorGUILayout.EndScrollView();
        }

        private void RunShaderPlatformScan()
        {
            var presets = new List<ShaderCompileTargetPreset>();
            if (_shaderPlatD3D) presets.Add(ShaderCompileTargetPreset.WindowsD3D);
            if (_shaderPlatVulkan) presets.Add(ShaderCompileTargetPreset.AndroidVulkan);
            if (_shaderPlatGles) presets.Add(ShaderCompileTargetPreset.AndroidGLES3);
            if (_shaderPlatMetal) presets.Add(ShaderCompileTargetPreset.IosMetal);
            if (presets.Count == 0)
            {
                _shaderInstrStatus = "请至少勾选一个平台";
                return;
            }

            _shaderInstrReport = ShaderPlatformInstrAnalyzer.ScanScene(
                presets, _includeInactive, _shaderPreferSelection, _shaderOnePerShader);
            _shaderInstrStatus = _shaderInstrReport.summary;
        }

        // ─── AI（面板左侧导航页，非独立菜单）───

        private void DrawAiAssist()
        {
            CyberpunkTheme.DrawInfoBanner(
                "可全量或分块导出给 AI。勾选「时段曲线」可只分析一段时间内的帧率波动。");

            EditorGUILayout.BeginHorizontal();
            if (TAEditorGUI.PrimaryButton("采集 / 刷新", 24f))
            {
                _aiReport = ProfilingAiBridge.CollectReport(BuildAiExportOptions(), _frameHistory);
                _aiStatus = $"已采集 · mode={_aiReport.exportMode} · issues={_aiReport.issues.Count}";
            }

            if (GUILayout.Button("导出全量", GUILayout.Height(24f), GUILayout.Width(72f)))
            {
                string path = ProfilingAiBridge.ExportContext();
                _aiStatus = "已导出全量：" + path;
                _aiReport = ProfilingAiBridge.CollectReport();
            }

            if (GUILayout.Button("导出分块", GUILayout.Height(24f), GUILayout.Width(72f)))
            {
                var opt = BuildAiExportOptions();
                string path = ProfilingAiBridge.ExportContext(opt, _frameHistory);
                _aiStatus = $"已导出分块（{opt.Sections}）：{path}";
                _aiReport = ProfilingAiBridge.CollectReport(opt, _frameHistory);
            }

            if (GUILayout.Button("复制分块", GUILayout.Height(24f), GUILayout.Width(72f)))
            {
                var opt = BuildAiExportOptions();
                ProfilingAiBridge.CopyContextToClipboard(opt, _frameHistory);
                _aiStatus = "分块 JSON 已复制";
            }

            if (GUILayout.Button("执行动作", GUILayout.Height(24f), GUILayout.Width(72f)))
            {
                var results = ProfilingAiBridge.ApplyActionsFromFile();
                int ok = 0, fail = 0;
                foreach (var r in results)
                {
                    if (r.ok) ok++;
                    else fail++;
                    Debug.Log(r.ok
                        ? $"[TA.Profiling] {r.message} (affected={r.affected})"
                        : $"[TA.Profiling] FAIL · {r.message}");
                }

                _aiStatus = $"执行完成 · ok={ok} fail={fail}";
                _aiReport = ProfilingAiBridge.CollectReport(BuildAiExportOptions(), _frameHistory);
            }

            EditorGUILayout.EndHorizontal();

            DrawAiChunkOptions();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("打开上下文文件", GUILayout.Height(22f)))
            {
                string abs = ProfilingAiBridge.GetAbsolutePath(
                    _aiSecFrameWindow || !IsAiFullSnapshotSelection()
                        ? ProfilingAiBridge.ChunkRelativePath
                        : ProfilingAiBridge.DefaultRelativePath);
                if (System.IO.File.Exists(abs))
                    EditorUtility.RevealInFinder(abs);
                else
                {
                    string full = ProfilingAiBridge.GetAbsolutePath(ProfilingAiBridge.DefaultRelativePath);
                    if (System.IO.File.Exists(full))
                        EditorUtility.RevealInFinder(full);
                    else
                        _aiStatus = "尚未导出，请先点「导出全量」或「导出分块」";
                }
            }

            if (GUILayout.Button("打开 / 创建动作模板", GUILayout.Height(22f)))
            {
                string path = ProfilingAiBridge.EnsureActionsTemplate();
                _aiStatus = "动作文件：" + path;
            }

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_aiStatus))
                EditorGUILayout.LabelField(_aiStatus, CyberpunkTheme.MiniLabelStyle);

            if (_aiReport == null)
            {
                GUILayout.Label("点「采集 / 刷新」生成问题列表", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            EditorGUILayout.LabelField(
                $"场景 {_aiReport.sceneName} · {_aiReport.generatedAt} · {_aiReport.exportMode} · issues {_aiReport.issues.Count}",
                CyberpunkTheme.MiniLabelStyle);

            if (_aiReport.includedSections != null && _aiReport.includedSections.Count > 0)
                EditorGUILayout.LabelField(
                    "块: " + string.Join(", ", _aiReport.includedSections),
                    CyberpunkTheme.MiniLabelStyle);

            if (_aiReport.frameWindow != null && _aiReport.frameWindow.sampleCount > 0)
                EditorGUILayout.LabelField(_aiReport.frameWindow.summary, CyberpunkTheme.MiniLabelStyle);

            _aiIssueScroll = EditorGUILayout.BeginScrollView(_aiIssueScroll, GUILayout.MinHeight(180f));
            foreach (var issue in _aiReport.issues)
            {
                CyberpunkTheme.BeginContentPanel(false);
                var sev = issue.severity ?? "info";
                var color = sev == "error" ? CyberpunkTheme.Danger
                    : sev == "warning" ? CyberpunkTheme.Amber
                    : CyberpunkTheme.Cyan;
                var titleStyle = new GUIStyle(CyberpunkTheme.SectionTitleStyle);
                titleStyle.normal.textColor = color;
                GUILayout.Label($"[{sev}] {issue.title}", titleStyle);
                if (!string.IsNullOrEmpty(issue.detail))
                    GUILayout.Label(issue.detail, CyberpunkTheme.MiniLabelStyle);
                if (!string.IsNullOrEmpty(issue.suggestedActionType))
                    GUILayout.Label($"建议动作: {issue.suggestedActionType}", CyberpunkTheme.MiniLabelStyle);
                CyberpunkTheme.EndContentPanel();
            }

            if (_aiReport.issues.Count == 0)
                GUILayout.Label("当前无自动检出问题", EditorStyles.centeredGreyMiniLabel);

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4f);
            GUILayout.Label("可执行动作", CyberpunkTheme.SectionTitleStyle);
            CyberpunkTheme.BeginContentPanel(false);
            foreach (var a in _aiReport.availableActions)
                GUILayout.Label($"· {a.type} — {a.description}", CyberpunkTheme.MiniLabelStyle);
            CyberpunkTheme.EndContentPanel();
        }

        private void DrawAiChunkOptions()
        {
            CyberpunkTheme.BeginContentPanel(false);
            GUILayout.Label("分块选择", CyberpunkTheme.SectionTitleStyle);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("全选快照", GUILayout.Height(20f), GUILayout.Width(72f)))
                SetAiSnapshotSections(true);
            if (GUILayout.Button("仅帧率波动", GUILayout.Height(20f), GUILayout.Width(84f)))
            {
                SetAiSnapshotSections(false);
                _aiSecRealtime = true;
                _aiSecIssues = true;
                _aiSecFrameWindow = true;
                _aiWindowSeconds = Mathf.Max(5f, _aiWindowSeconds);
            }

            if (GUILayout.Button("仅 GC", GUILayout.Height(20f), GUILayout.Width(56f)))
            {
                SetAiSnapshotSections(false);
                _aiSecGc = true;
                _aiSecIssues = true;
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _aiSecRealtime = GUILayout.Toggle(_aiSecRealtime, "实时", GUILayout.Width(48f));
            _aiSecRender = GUILayout.Toggle(_aiSecRender, "渲染", GUILayout.Width(48f));
            _aiSecGc = GUILayout.Toggle(_aiSecGc, "GC", GUILayout.Width(40f));
            _aiSecCulling = GUILayout.Toggle(_aiSecCulling, "剔除", GUILayout.Width(48f));
            _aiSecBatch = GUILayout.Toggle(_aiSecBatch, "合批", GUILayout.Width(48f));
            _aiSecIssues = GUILayout.Toggle(_aiSecIssues, "问题", GUILayout.Width(48f));
            _aiSecActions = GUILayout.Toggle(_aiSecActions, "动作", GUILayout.Width(48f));
            EditorGUILayout.EndHorizontal();

            _aiSecFrameWindow = GUILayout.Toggle(_aiSecFrameWindow,
                $"时段曲线（帧率波动等）· 缓冲 {_frameHistory.Filled}/{MetricFrameHistory.Length}");

            if (_aiSecFrameWindow)
            {
                EditorGUI.indentLevel++;
                _aiUseIndexRange = EditorGUILayout.ToggleLeft("按采样索引（否则按最近秒数）", _aiUseIndexRange);
                if (_aiUseIndexRange)
                {
                    int maxIdx = Mathf.Max(0, _frameHistory.Filled - 1);
                    EditorGUILayout.BeginHorizontal();
                    _aiWinStart = EditorGUILayout.IntField("起", Mathf.Clamp(_aiWinStart, 0, maxIdx), GUILayout.Width(120f));
                    _aiWinEnd = EditorGUILayout.IntField("止", Mathf.Clamp(_aiWinEnd, 0, maxIdx), GUILayout.Width(120f));
                    GUILayout.Label($"0~{maxIdx}", CyberpunkTheme.MiniLabelStyle);
                    EditorGUILayout.EndHorizontal();
                }
                else
                {
                    _aiWindowSeconds = EditorGUILayout.Slider("最近秒数", _aiWindowSeconds, 5f, 120f);
                }

                EditorGUILayout.BeginHorizontal();
                _aiIncludeSamples = GUILayout.Toggle(_aiIncludeSamples, "附带采样点", GUILayout.Width(90f));
                if (_aiIncludeSamples)
                    _aiMaxSamples = EditorGUILayout.IntSlider("最大点数", _aiMaxSamples, 16, 240);
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel--;
            }

            CyberpunkTheme.EndContentPanel();
        }

        private void SetAiSnapshotSections(bool on)
        {
            _aiSecRealtime = on;
            _aiSecRender = on;
            _aiSecGc = on;
            _aiSecCulling = on;
            _aiSecBatch = on;
            _aiSecIssues = on;
            _aiSecActions = on;
            if (!on) _aiSecFrameWindow = false;
        }

        private bool IsAiFullSnapshotSelection()
        {
            return _aiSecRealtime && _aiSecRender && _aiSecGc && _aiSecCulling && _aiSecBatch
                   && _aiSecIssues && _aiSecActions && !_aiSecFrameWindow;
        }

        private AiExportOptions BuildAiExportOptions()
        {
            var sections = AiExportSection.None;
            if (_aiSecRealtime) sections |= AiExportSection.Realtime;
            if (_aiSecRender) sections |= AiExportSection.Render;
            if (_aiSecGc) sections |= AiExportSection.Gc;
            if (_aiSecCulling) sections |= AiExportSection.Culling;
            if (_aiSecBatch) sections |= AiExportSection.Batch;
            if (_aiSecIssues) sections |= AiExportSection.Issues;
            if (_aiSecActions) sections |= AiExportSection.AvailableActions;
            if (_aiSecFrameWindow) sections |= AiExportSection.FrameWindow;

            if (sections == AiExportSection.None)
                sections = AiExportSection.Snapshot;

            var opt = new AiExportOptions
            {
                Sections = sections,
                WindowSeconds = _aiWindowSeconds,
                TargetFps = _reportTargetFps,
                IncludeFrameSamples = _aiIncludeSamples,
                MaxFrameSamples = _aiMaxSamples,
                Focus = _aiSecFrameWindow && !_aiSecCulling && !_aiSecBatch
                    ? "fps_fluctuation"
                    : "general"
            };

            if (_aiSecFrameWindow && _aiUseIndexRange)
            {
                opt.StartIndex = _aiWinStart;
                opt.EndIndex = _aiWinEnd;
                opt.WindowSeconds = 0f;
            }

            return opt;
        }

        // ─── 资源开销 ───

        private void DrawResourceCost()
        {
            EditorGUILayout.BeginHorizontal();
            if (TAEditorGUI.PrimaryButton("扫描", 24f))
                _costReport = SceneResourceCostAnalyzer.Scan(_includeInactive);
            _includeInactive = GUILayout.Toggle(_includeInactive, "未激活", GUILayout.Width(60f));
            _objectFilter = EditorGUILayout.TextField(_objectFilter);
            _objectSort = EditorGUILayout.Popup(_objectSort, new[] { "评分", "DC", "Tris", "脚本" }, GUILayout.Width(70f));
            EditorGUILayout.EndHorizontal();

            if (_costReport == null)
            {
                GUILayout.Label("扫描后按类型过滤 GameObject 开销", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            foreach (var cat in _costReport.Categories)
            {
                bool on = _categoryFilter == cat.Category;
                bool next = GUILayout.Toggle(on, Truncate(cat.Label, 6), "Button");
                if (next != on)
                    _categoryFilter = next ? cat.Category : (ResourceCategory?)null;
            }

            EditorGUILayout.EndHorizontal();

            var list = FilterObjects(_costReport.Objects);
            if (!string.IsNullOrEmpty(_locateStatus))
                CyberpunkTheme.DrawInfoBanner(_locateStatus);

            CyberpunkTheme.DrawInfoBanner("单击名称定位到 Hierarchy，并在 Scene 视图框选。");
            CyberpunkTheme.BeginContentPanel(true);
            _objectScroll = EditorGUILayout.BeginScrollView(_objectScroll, GUILayout.MinHeight(260f));
            foreach (var e in list)
            {
                CyberpunkTheme.BeginRow();
                string name = e.Target != null ? e.Target.name : "?";
                if (GUILayout.Button(Truncate(name, 16), EditorStyles.linkLabel, GUILayout.Width(110f)))
                {
                    bool ok = ProfilingLocator.TryLocate(e.Target, out string msg);
                    _locateStatus = (ok ? "✓ " : "✗ ") + msg;
                    if (!ok && !string.IsNullOrEmpty(e.Path))
                        _locateStatus += " · " + e.Path;
                }

                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
                GUILayout.Label(e.PrimaryCategoryLabel, GUILayout.Width(32f));
                GUILayout.Label($"S{e.ScriptCount}", GUILayout.Width(28f));
                GUILayout.Label($"DC{e.EstimatedDrawCalls}", GUILayout.Width(40f));
                GUILayout.Label($"T{e.TriangleCount:N0}", GUILayout.Width(56f));
                GUILayout.Label($"{e.CostScore:F0}", GUILayout.Width(32f));
                CyberpunkTheme.EndRow();
            }

            EditorGUILayout.EndScrollView();
            CyberpunkTheme.EndContentPanel();
        }

        private List<GameObjectCostEntry> FilterObjects(List<GameObjectCostEntry> source)
        {
            var result = new List<GameObjectCostEntry>();
            string f = _objectFilter?.Trim() ?? "";
            foreach (var e in source)
            {
                if (_categoryFilter.HasValue && !MatchCat(e, _categoryFilter.Value)) continue;
                if (f.Length > 0)
                {
                    string n = e.Target != null ? e.Target.name : "";
                    if (n.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (e.Path == null || e.Path.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
                }

                result.Add(e);
            }

            switch (_objectSort)
            {
                case 1: result.Sort((a, b) => b.EstimatedDrawCalls.CompareTo(a.EstimatedDrawCalls)); break;
                case 2: result.Sort((a, b) => b.TriangleCount.CompareTo(a.TriangleCount)); break;
                case 3: result.Sort((a, b) => b.ScriptCount.CompareTo(a.ScriptCount)); break;
                default: result.Sort((a, b) => b.CostScore.CompareTo(a.CostScore)); break;
            }

            if (result.Count > 200) result.RemoveRange(200, result.Count - 200);
            return result;
        }

        private static bool MatchCat(GameObjectCostEntry e, ResourceCategory c)
        {
            switch (c)
            {
                case ResourceCategory.Material: return e.MaterialCount > 0;
                case ResourceCategory.Texture: return e.TextureCount > 0;
                case ResourceCategory.Mesh: return e.MeshCount > 0;
                case ResourceCategory.Particle: return e.ParticleSystems > 0;
                case ResourceCategory.Light: return e.LightCount > 0;
                case ResourceCategory.Script: return e.ScriptCount > 0;
                default: return true;
            }
        }

        // ─── 场景可视（多模式） ───

        private void DrawSceneViz()
        {
            EditorGUILayout.BeginHorizontal();
            bool on = GUILayout.Toggle(SceneDebugVisualization.Enabled,
                SceneDebugVisualization.Enabled ? "着色 ON" : "着色 OFF",
                "Button", GUILayout.Height(24f), GUILayout.Width(72f));
            if (on != SceneDebugVisualization.Enabled)
                SceneDebugVisualization.Enabled = on;
            if (GUILayout.Button("扫描", GUILayout.Height(24f), GUILayout.Width(48f)))
                SceneDebugVisualization.Rescan();
            GUILayout.FlexibleSpace();
            GUILayout.Label(SceneDebugVisualization.Status, CyberpunkTheme.MiniLabelStyle);
            EditorGUILayout.EndHorizontal();

            // 模式网格按钮
            var modes = (SceneVizMode[])Enum.GetValues(typeof(SceneVizMode));
            int cols = 3;
            for (int i = 0; i < modes.Length; i += cols)
            {
                EditorGUILayout.BeginHorizontal();
                for (int c = 0; c < cols; c++)
                {
                    int idx = i + c;
                    if (idx >= modes.Length)
                    {
                        GUILayout.FlexibleSpace();
                        continue;
                    }

                    var m = modes[idx];
                    bool sel = SceneDebugVisualization.Mode == m;
                    if (CyberpunkTheme.ModeToggle(sel, SceneDebugVisualization.ModeLabel(m), GUILayout.Height(22f)) && !sel)
                        SceneDebugVisualization.Mode = m;
                }

                EditorGUILayout.EndHorizontal();
            }

            SceneDebugVisualization.Opacity = EditorGUILayout.Slider("透明度", SceneDebugVisualization.Opacity, 0.1f, 0.95f);

            var mode = SceneDebugVisualization.Mode;
            EditorGUI.BeginChangeCheck();
            if (mode == SceneVizMode.TriDensity)
                SceneDebugVisualization.TriDensityScale = EditorGUILayout.Slider("密度倍率", SceneDebugVisualization.TriDensityScale, 0.1f, 5f);
            if (mode == SceneVizMode.TexelDensity)
                SceneDebugVisualization.TexelTarget = EditorGUILayout.Slider("目标纹素 px/u", SceneDebugVisualization.TexelTarget, 1f, 40f);
            if (EditorGUI.EndChangeCheck())
                SceneDebugVisualization.Rescan();

            DrawSceneVizLegend(mode);

            CyberpunkTheme.BeginContentPanel(true);
            _sceneVizScroll = EditorGUILayout.BeginScrollView(_sceneVizScroll, GUILayout.MinHeight(240f));
            var items = SceneDebugVisualization.CurrentItems;
            int show = Mathf.Min(items.Count, 200);
            for (int i = 0; i < show; i++)
            {
                var it = items[i];
                bool sel = SceneDebugVisualization.SelectedIndex == i;
                CyberpunkTheme.BeginRow();
                EditorGUI.DrawRect(GUILayoutUtility.GetRect(12f, 12f, GUILayout.Width(12f)), it.Color);
                if (GUILayout.Button(Truncate(it.GameObject != null ? it.GameObject.name : "?", 16),
                        EditorStyles.linkLabel, GUILayout.Width(110f)))
                    SceneDebugVisualization.Ping(i);
                GUILayout.Label(Truncate(it.Label, 28), CyberpunkTheme.MiniLabelStyle, GUILayout.MinWidth(80f));
                var prevBg = GUI.backgroundColor;
                if (sel) GUI.backgroundColor = CyberpunkTheme.Cyan;
                if (GUILayout.Button(sel ? "×" : "◎", GUILayout.Width(28f)))
                    SceneDebugVisualization.SelectedIndex = sel ? -1 : i;
                GUI.backgroundColor = prevBg;
                CyberpunkTheme.EndRow();
            }

            if (items.Count == 0)
                GUILayout.Label("启用着色后看 Scene 视图", EditorStyles.centeredGreyMiniLabel);
            else if (items.Count > show)
                GUILayout.Label($"… 共 {items.Count}，仅列前 {show}", CyberpunkTheme.MiniLabelStyle);
            EditorGUILayout.EndScrollView();
            CyberpunkTheme.EndContentPanel();
        }

        private static void DrawSceneVizLegend(SceneVizMode mode)
        {
            string tip;
            switch (mode)
            {
                case SceneVizMode.Overdraw: tip = "叠画越亮/越红 = 填充越重（半透明、多层）"; break;
                case SceneVizMode.Lod: tip = "绿→红 = LOD0→更高；品红 = Culled"; break;
                case SceneVizMode.TriDensity: tip = "绿低密度 / 红高密度（三角 / 表面积）"; break;
                case SceneVizMode.TexelDensity: tip = "蓝偏低 · 绿接近目标 · 红过高"; break;
                case SceneVizMode.Mipmap: tip = "红 mip0（过大）→ 蓝/紫高 mip（偏糊）"; break;
                case SceneVizMode.ShadowCascade: tip = "颜色区分 Cascade 0~N；圆盘为分界示意"; break;
                case SceneVizMode.LightmapProbe: tip = "按 Lightmap 索引着色；蓝=探针；灰=无 GI"; break;
                case SceneVizMode.SrpInstancing: tip = "绿双开 / 黄仅 Inst / 红皆不兼容"; break;
                case SceneVizMode.ShaderComplexity: tip = "绿简单 → 红复杂（Pass/属性近似）"; break;
                case SceneVizMode.Layer: tip = "按 GameObject Layer 着色"; break;
                case SceneVizMode.ReflectionProbe: tip = "线框=探针范围；蓝物体在影响内"; break;
                case SceneVizMode.ParticleCoverage: tip = "绿低覆盖 → 红高屏占比；看粒子/特效"; break;
                default: tip = ""; break;
            }

            if (!string.IsNullOrEmpty(tip))
                CyberpunkTheme.DrawInfoBanner(tip);
        }

        // ─── 视锥 / 遮挡剔除 ───

        private void DrawCulling()
        {
            var cam = CullingVisualization.ResolveCamera();
            EditorGUILayout.BeginHorizontal();
            bool on = GUILayout.Toggle(CullingVisualization.Enabled,
                CullingVisualization.Enabled ? "可视 ON" : "可视 OFF",
                "Button", GUILayout.Height(24f), GUILayout.Width(72f));
            if (on != CullingVisualization.Enabled)
                CullingVisualization.Enabled = on;
            if (GUILayout.Button("扫描", GUILayout.Height(24f), GUILayout.Width(48f)))
                CullingVisualization.Rescan();
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            var nextCam = (Camera)EditorGUILayout.ObjectField("相机", cam, typeof(Camera), true);
            if (EditorGUI.EndChangeCheck())
                CullingVisualization.TargetCamera = nextCam;

            CullingVisualization.Mode = (CullingVizMode)EditorGUILayout.EnumPopup("模式", CullingVisualization.Mode);

            EditorGUILayout.BeginHorizontal();
            CullingVisualization.DrawFrustum = GUILayout.Toggle(CullingVisualization.DrawFrustum, "视锥线框", GUILayout.Width(72f));
            CullingVisualization.DrawOutside = GUILayout.Toggle(CullingVisualization.DrawOutside, "锥外红", GUILayout.Width(56f));
            CullingVisualization.DrawOccluded = GUILayout.Toggle(CullingVisualization.DrawOccluded, "遮挡橙", GUILayout.Width(56f));
            CullingVisualization.Opacity = GUILayout.HorizontalSlider(CullingVisualization.Opacity, 0.15f, 0.85f);
            EditorGUILayout.EndHorizontal();

            var report = CullingVisualization.Report;
            EditorGUILayout.BeginHorizontal();
            MetricCard("锥内", $"{report.InFrustum}", $"锥外 {report.OutFrustum}", Ok);
            MetricCard("可见", $"{report.Visible}", $"Renderer {report.TotalRenderers}", Accent);
            MetricCard("遮挡嫌疑", $"{report.LikelyOccluded}", Application.isPlaying ? "锥内不可见" : "需 Play",
                report.LikelyOccluded > 0 ? Warn : Color.white);
            MetricCard("Occlusion", report.CameraOcclusionEnabled ? "ON" : "OFF",
                report.HasOcclusionData ? "Umbra 有数据" : "无 Bake 数据",
                report.CameraOcclusionEnabled ? Ok : Warn);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (cam != null)
            {
                bool occ = cam.useOcclusionCulling;
                if (GUILayout.Toggle(occ, "Camera Occlusion", "Button", GUILayout.Height(22f)) != occ)
                    CullingVisualization.SetCameraOcclusion(cam, !occ);
            }

            if (GUILayout.Button("打开 Occlusion 窗口", GUILayout.Height(22f)))
                CullingVisualization.OpenOcclusionWindow();
            GUILayout.FlexibleSpace();
            GUILayout.Label($"Occluder {report.OccluderStaticCount} · Occludee {report.OccludeeStaticCount}",
                CyberpunkTheme.MiniLabelStyle);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _cullListFilter = GUILayout.Toolbar(_cullListFilter,
                new[] { "全部", "锥外", "遮挡嫌疑", "缺 Occludee" });
            EditorGUILayout.EndHorizontal();

            // 快捷：给列表中物体打 Occludee/Occluder
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(report.Entries.Count == 0);
            if (GUILayout.Button("列表→Occludee Static", GUILayout.Height(22f)))
            {
                var list = FilterCullList(report);
                int n = CullingVisualization.SetOcclusionStatic(list, false, true, true);
                _batchStatus = $"已标记 Occludee Static ×{n}";
                CullingVisualization.Rescan();
            }

            if (GUILayout.Button("列表→Occluder Static", GUILayout.Height(22f)))
            {
                var list = FilterCullList(report);
                int n = CullingVisualization.SetOcclusionStatic(list, true, false, true);
                _batchStatus = $"已标记 Occluder Static ×{n}";
                CullingVisualization.Rescan();
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_batchStatus) && _nav == NavId.Visual && _vizSub == 2)
                CyberpunkTheme.DrawInfoBanner(_batchStatus);

            CyberpunkTheme.BeginContentPanel(true);
            _cullScroll = EditorGUILayout.BeginScrollView(_cullScroll, GUILayout.MinHeight(240f));
            var filtered = FilterCullList(report);
            int shown = 0;
            foreach (var e in filtered)
            {
                if (shown++ > 250) break;
                CyberpunkTheme.BeginRow();
                if (GUILayout.Button(Truncate(e.GameObject != null ? e.GameObject.name : "?", 16),
                        EditorStyles.linkLabel, GUILayout.Width(110f)))
                {
                    Selection.activeGameObject = e.GameObject;
                    EditorGUIUtility.PingObject(e.GameObject);
                }

                GUILayout.Label(e.InFrustum ? "锥内" : "锥外", GUILayout.Width(32f));
                GUILayout.Label(e.IsVisible ? "可见" : "隐", GUILayout.Width(28f));
                GUILayout.Label(e.LikelyOccluded ? "遮挡?" : "—", GUILayout.Width(36f));
                GUILayout.Label(e.IsOccluderStatic ? "OR" : "—", GUILayout.Width(24f));
                GUILayout.Label(e.IsOccludeeStatic ? "OE" : "—", GUILayout.Width(24f));
                GUILayout.Label($"T{e.TriangleEstimate:N0}", GUILayout.Width(56f));
                CyberpunkTheme.EndRow();
            }

            if (filtered.Count == 0)
                GUILayout.Label("无条目 — 选择相机并扫描", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndScrollView();
            CyberpunkTheme.EndContentPanel();
        }

        private List<CullingEntry> FilterCullList(CullingReport report)
        {
            var result = new List<CullingEntry>();
            if (report?.Entries == null) return result;
            foreach (var e in report.Entries)
            {
                switch (_cullListFilter)
                {
                    case 1:
                        if (e.InFrustum) continue;
                        break;
                    case 2:
                        if (!e.LikelyOccluded) continue;
                        break;
                    case 3:
                        if (e.IsOccludeeStatic || !e.InFrustum) continue;
                        break;
                }

                result.Add(e);
            }

            return result;
        }

        // ─── 命名合批 ───

        private void DrawNameBatch()
        {
            EditorGUILayout.BeginHorizontal();
            _batchNamePattern = EditorGUILayout.TextField("命名", _batchNamePattern);
            _batchMatchMode = (NameMatchMode)EditorGUILayout.EnumPopup(_batchMatchMode, GUILayout.Width(80f));
            if (GUILayout.Button("查找", GUILayout.Width(48f)))
                RefreshBatchMatches();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _batchIncludeInactive = GUILayout.Toggle(_batchIncludeInactive, "未激活", GUILayout.Width(56f));
            _batchOnlyRenderer = GUILayout.Toggle(_batchOnlyRenderer, "仅渲染", GUILayout.Width(56f));
            _batchMatchRootsOnly = GUILayout.Toggle(_batchMatchRootsOnly, "仅顶层", GUILayout.Width(56f));
            GUILayout.FlexibleSpace();
            GUILayout.Label($"匹配 {_batchMatches?.Count ?? 0}", CyberpunkTheme.MiniLabelStyle);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(_batchMatches == null || _batchMatches.Count == 0);
            if (TAEditorGUI.PrimaryButton("Batching Static", 24f))
            {
                _batchStatus = NamePrefixBatchProcessor.SetBatchingStatic(_batchMatches, true).Message;
                RefreshBatchMatches();
            }

            if (GUILayout.Button("清除", GUILayout.Height(24f), GUILayout.Width(48f)))
            {
                _batchStatus = NamePrefixBatchProcessor.SetBatchingStatic(_batchMatches, false).Message;
                RefreshBatchMatches();
            }

            if (GUILayout.Button("Combine", GUILayout.Height(24f), GUILayout.Width(72f)))
                _batchStatus = NamePrefixBatchProcessor.CombineStaticBatches(_batchMatches).Message;
            if (GUILayout.Button("全选", GUILayout.Height(24f), GUILayout.Width(48f)))
                NamePrefixBatchProcessor.SelectMatches(_batchMatches);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_batchStatus))
                CyberpunkTheme.DrawInfoBanner(_batchStatus);

            CyberpunkTheme.BeginContentPanel(false);
            _batchListScroll = EditorGUILayout.BeginScrollView(_batchListScroll, GUILayout.MinHeight(260f));
            if (_batchMatches == null || _batchMatches.Count == 0)
            {
                GUILayout.Label("例：前缀 P_ → 查找 → Static / Combine", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                foreach (var e in _batchMatches)
                {
                    CyberpunkTheme.BeginRow();
                    if (GUILayout.Button(Truncate(e.Target != null ? e.Target.name : "?", 18),
                            EditorStyles.linkLabel, GUILayout.Width(120f)))
                    {
                        Selection.activeGameObject = e.Target;
                        EditorGUIUtility.PingObject(e.Target);
                    }

                    GUILayout.Label(e.IsBatchingStatic ? "ST" : "—", GUILayout.Width(28f));
                    GUILayout.Label($"R{e.RendererCount}", GUILayout.Width(32f));
                    GUILayout.Label(Truncate(e.SharedMaterialName, 14), GUILayout.Width(90f));
                    GUILayout.Label(Truncate(e.Path, 36), CyberpunkTheme.MiniLabelStyle);
                    CyberpunkTheme.EndRow();
                }
            }

            EditorGUILayout.EndScrollView();
            CyberpunkTheme.EndContentPanel();
        }

        private void RefreshBatchMatches()
        {
            _batchMatches = NamePrefixBatchProcessor.FindMatches(
                _batchNamePattern, _batchMatchMode, _batchIncludeInactive, _batchOnlyRenderer, _batchMatchRootsOnly);
            Repaint();
        }

        // ─── 批次可视 ───

        private void DrawBatchViz()
        {
            EditorGUILayout.BeginHorizontal();
            bool on = GUILayout.Toggle(BatchVisualization.Enabled, BatchVisualization.Enabled ? "着色 ON" : "着色 OFF",
                "Button", GUILayout.Height(24f), GUILayout.Width(72f));
            if (on != BatchVisualization.Enabled)
                BatchVisualization.Enabled = on;
            if (GUILayout.Button("扫描", GUILayout.Height(24f), GUILayout.Width(48f)))
                BatchVisualization.Rescan();
            GUILayout.FlexibleSpace();
            GUILayout.Label($"可合批 {BatchVisualization.MultiBatchCount} / 单件 {BatchVisualization.SingleCount}",
                CyberpunkTheme.MiniLabelStyle);
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            BatchVisualization.Mode = (BatchVizGroupMode)EditorGUILayout.EnumPopup("分组", BatchVisualization.Mode);
            BatchVisualization.NameFilter = EditorGUILayout.TextField("过滤", BatchVisualization.NameFilter);
            if (EditorGUI.EndChangeCheck() && BatchVisualization.Enabled)
                BatchVisualization.Rescan();

            EditorGUILayout.BeginHorizontal();
            BatchVisualization.DrawMeshes = GUILayout.Toggle(BatchVisualization.DrawMeshes, "网格", GUILayout.Width(48f));
            BatchVisualization.DrawBounds = GUILayout.Toggle(BatchVisualization.DrawBounds, "包围盒", GUILayout.Width(56f));
            BatchVisualization.HideSingles = GUILayout.Toggle(BatchVisualization.HideSingles, "藏单件", GUILayout.Width(56f));
            BatchVisualization.Opacity = GUILayout.HorizontalSlider(BatchVisualization.Opacity, 0.15f, 0.9f);
            EditorGUILayout.EndHorizontal();

            CyberpunkTheme.BeginContentPanel(true);
            _batchVizScroll = EditorGUILayout.BeginScrollView(_batchVizScroll, GUILayout.MinHeight(280f));
            var groups = BatchVisualization.CurrentGroups;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (BatchVisualization.HideSingles && !g.CanBatch) continue;
                bool sel = BatchVisualization.SelectedGroup == i;
                CyberpunkTheme.BeginRow();
                EditorGUI.DrawRect(GUILayoutUtility.GetRect(12f, 12f, GUILayout.Width(12f)), g.Color);
                if (GUILayout.Button(Truncate(g.Label, 32), EditorStyles.linkLabel, GUILayout.MinWidth(140f)))
                    BatchVisualization.SelectGroupObjects(i);
                GUILayout.Label($"×{g.Count}", GUILayout.Width(32f));
                GUILayout.Label(g.CanBatch ? "合批" : "单", GUILayout.Width(28f));
                var prevBg = GUI.backgroundColor;
                if (sel) GUI.backgroundColor = CyberpunkTheme.Cyan;
                if (GUILayout.Button(sel ? "×" : "◎", GUILayout.Width(28f)))
                    BatchVisualization.SelectedGroup = sel ? -1 : i;
                GUI.backgroundColor = prevBg;
                CyberpunkTheme.EndRow();
            }

            if (groups.Count == 0)
                GUILayout.Label("启用着色后在 Scene 查看同色合批组", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndScrollView();
            CyberpunkTheme.EndContentPanel();
        }

        // ─── 内存详情 ───

        private void ToggleMemoryDetail()
        {
            _showMemoryDetail = !_showMemoryDetail;
            if (_showMemoryDetail)
                EditorApplication.delayCall += RefreshMemoryDetail;
        }

        private void RefreshMemoryDetail()
        {
            if (!this) return;
            _metrics?.ForceRefreshAssetMemory();
            _memoryDetail = MemoryDetailAnalyzer.Scan();
            Repaint();
        }

        private void DrawMemoryDetailPanel()
        {
            GUILayout.Space(4f);
            CyberpunkTheme.BeginToolbarStrip();
            GUILayout.Label("大对象 / 泄露点", CyberpunkTheme.SectionTitleStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("刷新", EditorStyles.toolbarButton))
                EditorApplication.delayCall += RefreshMemoryDetail;
            if (GUILayout.Button(MemoryDetailAnalyzer.HasBaseline ? "重记基线" : "记基线", EditorStyles.toolbarButton))
            {
                EditorApplication.delayCall += () =>
                {
                    MemoryDetailAnalyzer.CaptureBaseline();
                    RefreshMemoryDetail();
                };
            }

            if (GUILayout.Button("收起", EditorStyles.toolbarButton))
                _showMemoryDetail = false;
            CyberpunkTheme.EndToolbarStrip();
            if (_memoryDetail == null) return;

            EditorGUILayout.BeginHorizontal();
            CyberpunkTheme.BeginContentPanel(true, GUILayout.Width(position.width * 0.38f));
            _leakScroll = EditorGUILayout.BeginScrollView(_leakScroll, GUILayout.Height(150f));
            foreach (var s in _memoryDetail.LeakSuspects)
            {
                CyberpunkTheme.BeginContentPanel(false);
                GUILayout.Label(s.Title, CyberpunkTheme.SectionTitleStyle);
                GUILayout.Label(s.Detail, CyberpunkTheme.MiniLabelStyle);
                if (s.PingTarget != null && GUILayout.Button("定位", GUILayout.Width(40f)))
                {
                    Selection.activeObject = s.PingTarget;
                    EditorGUIUtility.PingObject(s.PingTarget);
                }

                CyberpunkTheme.EndContentPanel();
            }

            EditorGUILayout.EndScrollView();
            CyberpunkTheme.EndContentPanel();

            CyberpunkTheme.BeginContentPanel(false);
            _largeObjScroll = EditorGUILayout.BeginScrollView(_largeObjScroll, GUILayout.Height(150f));
            foreach (var e in _memoryDetail.LargeObjects)
            {
                CyberpunkTheme.BeginRow();
                if (GUILayout.Button(Truncate(e.Name, 16), EditorStyles.linkLabel, GUILayout.Width(110f)))
                {
                    Selection.activeObject = e.Target;
                    EditorGUIUtility.PingObject(e.Target);
                }

                GUILayout.Label(e.TypeName, GUILayout.Width(64f));
                GUILayout.Label(PerformanceMetrics.FormatBytes(e.Bytes), GUILayout.Width(56f));
                CyberpunkTheme.EndRow();
            }

            EditorGUILayout.EndScrollView();
            CyberpunkTheme.EndContentPanel();
            EditorGUILayout.EndHorizontal();
        }

        // ─── UI 组件 ───

        private static readonly Color Ok = CyberpunkTheme.Lime;
        private static readonly Color Warn = CyberpunkTheme.Amber;
        private static readonly Color Danger = CyberpunkTheme.Danger;
        private static readonly Color Accent = CyberpunkTheme.Cyan;
        private static readonly Color Blue = CyberpunkTheme.Cyan;
        private static readonly Color Orange = CyberpunkTheme.Magenta;

        private static Color FpsColor(float fps) =>
            fps >= 55f ? Ok : fps >= 30f ? Warn : Danger;

        private static void MetricCard(string title, string value, string sub, Color valueColor,
            Action extra = null, Action onClick = null)
        {
            CyberpunkTheme.BeginMetricCard();
            if (onClick != null)
            {
                var link = new GUIStyle(CyberpunkTheme.CardTitle) { fontStyle = FontStyle.Bold };
                link.normal.textColor = CyberpunkTheme.Cyan;
                if (GUILayout.Button(title, link))
                    onClick();
                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
            }
            else
            {
                GUILayout.Label(title, CyberpunkTheme.CardTitle);
            }

            var valueStyle = new GUIStyle(CyberpunkTheme.CardValueStyle);
            valueStyle.normal.textColor = valueColor;
            GUILayout.Label(value, valueStyle);
            if (!string.IsNullOrEmpty(sub))
            {
                var subStyle = new GUIStyle(EditorStyles.miniLabel);
                subStyle.normal.textColor = CyberpunkTheme.TextDim;
                GUILayout.Label(sub, subStyle);
            }

            extra?.Invoke();
            CyberpunkTheme.EndMetricCard();
        }

        private static void DrawBudgetBar(Rect rect, float cpuMs, float gpuMs)
        {
            const float budget = 16.67f;
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.12f));
            float cpu = Mathf.Clamp01(cpuMs / budget);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width * cpu, rect.height * 0.5f),
                cpu > 0.9f ? Danger : Ok);
            if (gpuMs > 0f)
            {
                float gpu = Mathf.Clamp01(gpuMs / budget);
                EditorGUI.DrawRect(new Rect(rect.x, rect.y + rect.height * 0.5f, rect.width * gpu, rect.height * 0.5f),
                    gpu > 0.9f ? Warn : Blue);
            }
        }

        private void DrawSeriesSparkline(MetricSeries series, float min, float max, Color color, float height)
        {
            // 曲线跟卡片同宽，不单独拉伸
            var rect = GUILayoutUtility.GetRect(
                CyberpunkTheme.MetricCardWidth - 16f, height,
                GUILayout.Width(CyberpunkTheme.MetricCardWidth - 16f),
                GUILayout.Height(height),
                GUILayout.ExpandWidth(false));
            DrawInteractiveSparkline(rect, series, min, max, color);
        }

        private void DrawInteractiveSparkline(Rect rect, MetricSeries series, float min, float max, Color color)
        {
            int filled = _frameHistory.Filled;
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.06f));

            var e = Event.current;
            if (filled > 0 && rect.Contains(e.mousePosition) &&
                (e.type == EventType.MouseDown && e.button == 0))
            {
                float t = Mathf.Clamp01((e.mousePosition.x - rect.x) / Mathf.Max(1f, rect.width));
                int idx = Mathf.Clamp(Mathf.RoundToInt(t * (filled - 1)), 0, filled - 1);
                _selectedVisualIndex = idx;
                _selectedSeries = series;
                if (!_historyPaused)
                    SetHistoryPaused(true);
                e.Use();
                Repaint();
            }

            if (e.type != EventType.Repaint || filled < 2) return;

            Handles.BeginGUI();
            Handles.color = color;
            Vector3 prev = default;
            bool hasPrev = false;
            int count = filled;
            for (int i = 0; i < count; i++)
            {
                float v = _frameHistory.GetSeriesValue(i, series);
                float tx = count <= 1 ? 0f : i / (float)(count - 1);
                float n = Mathf.InverseLerp(min, max, Mathf.Clamp(v, min, max));
                var p = new Vector3(rect.x + tx * rect.width, rect.yMax - n * rect.height, 0f);
                if (hasPrev) Handles.DrawLine(prev, p);
                prev = p;
                hasPrev = true;
            }

            if (_selectedVisualIndex >= 0 && _selectedVisualIndex < filled)
            {
                float sx = filled <= 1
                    ? rect.x
                    : rect.x + (_selectedVisualIndex / (float)(filled - 1)) * rect.width;
                Handles.color = new Color(1f, 1f, 1f, 0.55f);
                Handles.DrawLine(new Vector3(sx, rect.y, 0f), new Vector3(sx, rect.yMax, 0f));
                float sv = _frameHistory.GetSeriesValue(_selectedVisualIndex, series);
                float sn = Mathf.InverseLerp(min, max, Mathf.Clamp(sv, min, max));
                var marker = new Vector3(sx, rect.yMax - sn * rect.height, 0f);
                Handles.color = Color.white;
                Handles.DrawSolidDisc(marker, Vector3.forward, 3f);
                Handles.color = color;
                Handles.DrawWireDisc(marker, Vector3.forward, 3.5f);
            }

            Handles.EndGUI();
        }

        private void DrawSelectedFrameDetail()
        {
            if (_selectedVisualIndex < 0 ||
                !_frameHistory.TryGetChronological(_selectedVisualIndex, out var cur))
                return;

            MetricFrameSample? prev = null;
            if (_selectedVisualIndex > 0 &&
                _frameHistory.TryGetChronological(_selectedVisualIndex - 1, out var p))
                prev = p;

            CyberpunkTheme.BeginContentPanel(false, GUILayout.ExpandWidth(true));
            EditorGUILayout.BeginHorizontal();
            var head = CyberpunkTheme.SectionTitleStyle;
            GUILayout.Label(
                $"采样 #{_selectedVisualIndex + 1}/{_frameHistory.Filled} · {_selectedSeries}",
                head);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("←", EditorStyles.miniButton, GUILayout.Width(24f)) &&
                _selectedVisualIndex > 0)
                _selectedVisualIndex--;
            if (GUILayout.Button("→", EditorStyles.miniButton, GUILayout.Width(24f)) &&
                _selectedVisualIndex < _frameHistory.Filled - 1)
                _selectedVisualIndex++;
            EditorGUILayout.EndHorizontal();

            _frameDetailScroll = EditorGUILayout.BeginScrollView(_frameDetailScroll, GUILayout.MaxHeight(220f));

            GUILayout.Label("帧指标", CyberpunkTheme.SectionTitleStyle);
            DrawDeltaRow("FPS", cur.Fps, prev?.Fps, "F1", true);
            DrawDeltaRow("帧耗时 ms", cur.FrameMs, prev?.FrameMs, "F2", false);
            DrawDeltaRow("CPU ms", cur.CpuMainThreadMs, prev?.CpuMainThreadMs, "F2", false);
            if (cur.HasGpuTiming)
                DrawDeltaRow("GPU ms", cur.GpuFrameMs, prev?.GpuFrameMs, "F2", false);
            DrawDeltaRow("DrawCall", cur.DrawCalls, prev?.DrawCalls, "F0", false);
            DrawDeltaRow("SetPass", cur.SetPassCalls, prev?.SetPassCalls, "F0", false);
            DrawDeltaRow("Batches", cur.Batches, prev?.Batches, "F0", false);
            GUILayout.Label(
                $"Tris {cur.Triangles:N0} · Verts {cur.Vertices:N0}",
                CyberpunkTheme.MiniLabelStyle);
            GUILayout.Label(
                $"内存 {PerformanceMetrics.FormatBytes(cur.TotalAllocatedBytes)} · 堆 {PerformanceMetrics.FormatBytes(cur.MonoUsedBytes)}",
                CyberpunkTheme.MiniLabelStyle);
            GUILayout.Label(
                $"纹理 {PerformanceMetrics.FormatBytes(cur.TextureBytes)} ({cur.TextureCount}) · 网格 {PerformanceMetrics.FormatBytes(cur.MeshBytes)}",
                CyberpunkTheme.MiniLabelStyle);
            DrawDeltaRow("GC 触发", cur.GcCollectDelta, prev?.GcCollectDelta, "F0", false);
            DrawDeltaRow("GC.Alloc B", cur.GcAllocBytes, prev?.GcAllocBytes, "F0", false);
            GUILayout.Label($"GC Gen {cur.GcGen0}/{cur.GcGen1}/{cur.GcGen2}", CyberpunkTheme.MiniLabelStyle);

            GUILayout.Space(4f);
            GUILayout.Label("可能来源（采样时 Profiler 摘要）", CyberpunkTheme.SectionTitleStyle);
            if (!string.IsNullOrEmpty(cur.TopScriptsByTime))
            {
                GUILayout.Label("耗时脚本（单击定位）", CyberpunkTheme.MiniLabelStyle);
                foreach (var line in cur.TopScriptsByTime.Split('\n'))
                    DrawLocatableSampleLine(line);
            }
            else
            {
                GUILayout.Label("暂无耗时脚本摘要（需 Play + Profiler）", EditorStyles.centeredGreyMiniLabel);
            }

            if (!string.IsNullOrEmpty(cur.TopScriptsByGc))
            {
                GUILayout.Space(2f);
                GUILayout.Label("GC 分配源头（单击定位）", CyberpunkTheme.MiniLabelStyle);
                foreach (var line in cur.TopScriptsByGc.Split('\n'))
                    DrawLocatableSampleLine(line);
            }

            if (prev.HasValue)
            {
                GUILayout.Space(4f);
                GUILayout.Label("相对上一采样的变化提示", CyberpunkTheme.SectionTitleStyle);
                float dcDelta = cur.DrawCalls - prev.Value.DrawCalls;
                float fpsDelta = cur.Fps - prev.Value.Fps;
                float allocDelta = cur.GcAllocBytes - prev.Value.GcAllocBytes;
                if (Mathf.Abs(dcDelta) >= 5f)
                    GUILayout.Label($"· DrawCall {(dcDelta >= 0 ? "+" : "")}{dcDelta:F0} — 检查新增 Renderer / 材质切换 / 粒子",
                        CyberpunkTheme.MiniLabelStyle);
                if (fpsDelta <= -5f)
                    GUILayout.Label($"· FPS {fpsDelta:F1} — 对照 CPU/GPU 与脚本耗时",
                        CyberpunkTheme.MiniLabelStyle);
                if (allocDelta >= 1024f)
                    GUILayout.Label($"· GC.Alloc +{allocDelta:F0} B — 对照上方 GC 源头",
                        CyberpunkTheme.MiniLabelStyle);
                if (cur.GcCollectDelta > 0f)
                    GUILayout.Label("· 本采样发生了 GC Collect — 可能造成卡顿尖峰",
                        CyberpunkTheme.MiniLabelStyle);
            }

            EditorGUILayout.EndScrollView();
            CyberpunkTheme.EndContentPanel();
        }

        private void DrawLocatableSampleLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            string sample = line;
            int sep = line.IndexOf(" · ", StringComparison.Ordinal);
            if (sep > 0) sample = line.Substring(0, sep);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("·", GUILayout.Width(10f));
            if (GUILayout.Button(Truncate(line, 64), EditorStyles.linkLabel))
            {
                bool ok = ProfilingLocator.TryLocateProfilerSample(sample, out string msg);
                _locateStatus = (ok ? "✓ " : "✗ ") + msg;
            }

            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawDeltaRow(string label, float cur, float? prev, string format, bool higherBetter)
        {
            string text = $"{label}  {cur.ToString(format)}";
            if (prev.HasValue)
            {
                float d = cur - prev.Value;
                string sign = d > 0f ? "+" : "";
                text += $"  ({sign}{d.ToString(format)})";
                var style = new GUIStyle(CyberpunkTheme.MiniLabelStyle);
                bool bad = higherBetter ? d < 0f : d > 0f;
                if (Mathf.Abs(d) > 0.01f)
                    style.normal.textColor = bad ? CyberpunkTheme.Danger : CyberpunkTheme.Lime;
                GUILayout.Label(text, style);
            }
            else
            {
                GUILayout.Label(text, CyberpunkTheme.MiniLabelStyle);
            }
        }

        private static void DrawSparkline(Rect rect, float[] data, int writeIndex, float min, float max, Color color)
        {
            if (Event.current.type != EventType.Repaint || data == null || data.Length < 2) return;
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.06f));
            Handles.BeginGUI();
            Handles.color = color;
            int len = data.Length;
            Vector3 prev = default;
            bool hasPrev = false;
            for (int i = 0; i < len; i++)
            {
                float v = data[(writeIndex + i) % len];
                float t = i / (float)(len - 1);
                float n = Mathf.InverseLerp(min, max, Mathf.Clamp(v, min, max));
                var p = new Vector3(rect.x + t * rect.width, rect.yMax - n * rect.height, 0f);
                if (hasPrev) Handles.DrawLine(prev, p);
                prev = p;
                hasPrev = true;
            }

            Handles.EndGUI();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s.Substring(0, max - 1) + "…";
        }
    }
}
