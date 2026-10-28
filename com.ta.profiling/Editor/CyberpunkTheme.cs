using System.IO;
using UnityEditor;
using UnityEngine;

namespace TA.Profiling.Editor
{
    /// <summary>
    /// 赛博朋克风 Editor UI：霓虹色板 + Unsplash 下载贴图。
    /// </summary>
    public static class CyberpunkTheme
    {
        public static readonly Color Bg = new Color(0.04f, 0.05f, 0.08f, 1f);
        public static readonly Color Cyan = new Color(0.15f, 0.95f, 0.95f, 1f);
        public static readonly Color Magenta = new Color(1f, 0.2f, 0.75f, 1f);
        public static readonly Color Lime = new Color(0.45f, 1f, 0.35f, 1f);
        public static readonly Color Amber = new Color(1f, 0.75f, 0.2f, 1f);
        public static readonly Color Danger = new Color(1f, 0.25f, 0.4f, 1f);
        public static readonly Color TextDim = new Color(0.65f, 0.78f, 0.9f, 0.75f);
        public static readonly Color TextBright = new Color(0.85f, 0.98f, 1f, 1f);

        private static Texture2D _headerBg;
        private static Texture2D _panelBg;
        private static Texture2D _navBg;
        private static Texture2D _neonGrid;
        private static Texture2D _scanGlow;
        private static Texture2D _fallback;
        private static Texture2D _scanline;
        private static GUIStyle _titleStyle;
        private static GUIStyle _subStyle;
        private static GUIStyle _chip;
        private static GUIStyle _cardTitle;
        private static GUIStyle _cardValue;
        private static bool _loaded;
        private static string _textureDir;

        public static Texture2D HeaderBg => _headerBg != null ? _headerBg : _fallback;
        public static Texture2D PanelBg => _panelBg != null ? _panelBg : _fallback;
        public static Texture2D NavBg => _navBg != null ? _navBg : _fallback;
        public static Texture2D NeonGrid => _neonGrid != null ? _neonGrid : _fallback;
        public static Texture2D ScanGlow => _scanGlow != null ? _scanGlow : _fallback;

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _textureDir = ResolveTextureDir();

            _headerBg = LoadTex("header_bg.jpg");
            _panelBg = LoadTex("panel_bg.jpg");
            _navBg = LoadTex("nav_bg.jpg");
            _neonGrid = LoadTex("neon_grid.jpg");
            _scanGlow = LoadTex("scan_glow.jpg");
            _fallback = LoadTex("cyber_panel_fallback.png");
            _scanline = BuildScanline(4, 64);

            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            _titleStyle.normal.textColor = Cyan;

            _subStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleLeft
            };
            _subStyle.normal.textColor = Magenta;

            _chip = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = 10,
                padding = new RectOffset(6, 6, 2, 2)
            };
            _chip.normal.textColor = TextBright;

            _cardTitle = new GUIStyle(EditorStyles.miniLabel);
            _cardTitle.normal.textColor = TextDim;

            _cardValue = new GUIStyle(EditorStyles.largeLabel)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 18
            };
        }

        public static Rect DrawHudBanner(string title, string subtitle)
        {
            EnsureLoaded();
            var rect = GUILayoutUtility.GetRect(8f, 56f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                var tex = ScanGlow != null ? ScanGlow : HeaderBg;
                if (tex != null)
                    GUI.DrawTexture(rect, tex, ScaleMode.ScaleAndCrop);
                EditorGUI.DrawRect(rect, new Color(0.02f, 0.03f, 0.08f, 0.62f));
                DrawNeonFrame(rect, Cyan, Magenta, 1.5f);
                if (_scanline != null)
                    GUI.DrawTextureWithTexCoords(rect, _scanline, new Rect(0f, 0f, 1f, rect.height / 6f));
            }

            GUI.Label(new Rect(rect.x + 14f, rect.y + 10f, rect.width - 28f, 22f), "◈ " + title, _titleStyle);
            GUI.Label(new Rect(rect.x + 14f, rect.y + 32f, rect.width - 28f, 16f), subtitle, _subStyle);
            return rect;
        }

        public static void DrawNeonFrame(Rect rect, Color a, Color b, float thickness = 1f)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), a);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), a);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), b);
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), b);
            const float c = 8f;
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, c, thickness * 2f), a);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness * 2f, c), a);
            EditorGUI.DrawRect(new Rect(rect.xMax - c, rect.yMax - thickness * 2f, c, thickness * 2f), b);
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness * 2f, rect.yMax - c, thickness * 2f, c), b);
        }

        public static void BeginNavRail(float width)
        {
            EnsureLoaded();
            var rect = EditorGUILayout.BeginVertical(GUILayout.Width(width), GUILayout.ExpandHeight(true));
            if (Event.current.type == EventType.Repaint)
            {
                if (NavBg != null)
                {
                    GUI.DrawTexture(rect, NavBg, ScaleMode.ScaleAndCrop);
                    EditorGUI.DrawRect(rect, new Color(0.02f, 0.04f, 0.09f, 0.82f));
                }
                else
                {
                    EditorGUI.DrawRect(rect, new Color(0.06f, 0.07f, 0.12f, 0.95f));
                }

                DrawNeonFrame(rect, Magenta * 0.7f, Cyan * 0.7f, 1f);
            }
        }

        public static void EndNavRail() => EditorGUILayout.EndVertical();

        public static bool NavButton(bool on, string label, float height = 30f)
        {
            EnsureLoaded();
            var rect = GUILayoutUtility.GetRect(8f, height, GUILayout.ExpandWidth(true));
            rect.x += 6f;
            rect.width -= 12f;

            var e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                if (on)
                {
                    EditorGUI.DrawRect(rect, new Color(Cyan.r, Cyan.g, Cyan.b, 0.18f));
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y, 3f, rect.height), Cyan);
                    EditorGUI.DrawRect(new Rect(rect.xMax - 3f, rect.y, 3f, rect.height), Magenta);
                }
                else if (hover)
                {
                    EditorGUI.DrawRect(rect, new Color(Magenta.r, Magenta.g, Magenta.b, 0.12f));
                }

                var style = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 11
                };
                style.normal.textColor = on ? Cyan : (hover ? TextBright : TextDim);
                GUI.Label(rect, label, style);
            }

            if (e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                e.Use();
                return true;
            }

            return false;
        }

        public static void DrawChip(Rect r, string text, Color tint)
        {
            EnsureLoaded();
            EditorGUI.DrawRect(r, new Color(tint.r, tint.g, tint.b, 0.22f));
            DrawNeonFrame(r, tint, tint, 1f);
            GUI.Label(r, text, _chip);
        }

        public const float MetricCardWidth = 148f;
        public const float MetricCardMinHeight = 100f;
        public const float StatusChipWidth = 76f;
        public const float StatusChipHeight = 20f;

        public static void BeginMetricCard()
        {
            EnsureLoaded();
            // 固定尺寸：小框不随窗口拉伸；页面其它区域仍自适应
            var rect = EditorGUILayout.BeginVertical(
                GUILayout.Width(MetricCardWidth),
                GUILayout.MinHeight(MetricCardMinHeight),
                GUILayout.MaxWidth(MetricCardWidth),
                GUILayout.ExpandWidth(false));
            if (Event.current.type == EventType.Repaint)
            {
                if (PanelBg != null)
                {
                    GUI.DrawTexture(rect, PanelBg, ScaleMode.ScaleAndCrop);
                    EditorGUI.DrawRect(rect, new Color(0.03f, 0.05f, 0.1f, 0.78f));
                }
                else
                {
                    EditorGUI.DrawRect(rect, new Color(0.04f, 0.06f, 0.12f, 0.92f));
                }

                DrawNeonFrame(rect, Cyan * 0.65f, Magenta * 0.55f, 1f);
            }

            GUILayout.Space(6f);
        }

        public static void EndMetricCard()
        {
            GUILayout.Space(6f);
            EditorGUILayout.EndVertical();
        }

        /// <summary>内容面板（帧详情、脚本列表等）——贴图底 + 霓虹框。</summary>
        public static void BeginContentPanel(bool useGrid = false, params GUILayoutOption[] options)
        {
            EnsureLoaded();
            var rect = EditorGUILayout.BeginVertical(options);
            PaintPanelBackground(rect, useGrid, 0.72f);
            GUILayout.Space(4f);
        }

        public static void EndContentPanel()
        {
            GUILayout.Space(4f);
            EditorGUILayout.EndVertical();
        }

        public static void BeginRow(params GUILayoutOption[] options)
        {
            EnsureLoaded();
            var rect = EditorGUILayout.BeginHorizontal(options);
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, new Color(0.05f, 0.08f, 0.14f, 0.72f));
                EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f),
                    new Color(Cyan.r, Cyan.g, Cyan.b, 0.25f));
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, 2f, rect.height),
                    new Color(Magenta.r, Magenta.g, Magenta.b, 0.35f));
            }
        }

        public static void EndRow() => EditorGUILayout.EndHorizontal();

        public static void DrawInfoBanner(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            EnsureLoaded();
            var rect = GUILayoutUtility.GetRect(8f, 36f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                if (ScanGlow != null)
                {
                    GUI.DrawTexture(rect, ScanGlow, ScaleMode.ScaleAndCrop);
                    EditorGUI.DrawRect(rect, new Color(0.02f, 0.05f, 0.1f, 0.78f));
                }
                else
                {
                    EditorGUI.DrawRect(rect, new Color(0.06f, 0.1f, 0.16f, 0.92f));
                }

                DrawNeonFrame(rect, Cyan * 0.7f, Amber * 0.6f, 1f);
            }

            var style = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 8, 4, 4)
            };
            style.normal.textColor = TextBright;
            GUI.Label(rect, "◈  " + message, style);
        }

        public static void BeginToolbarStrip(params GUILayoutOption[] options)
        {
            EnsureLoaded();
            var rect = EditorGUILayout.BeginHorizontal(options);
            if (Event.current.type == EventType.Repaint)
            {
                if (PanelBg != null)
                {
                    GUI.DrawTexture(rect, PanelBg, ScaleMode.ScaleAndCrop);
                    EditorGUI.DrawRect(rect, new Color(0.02f, 0.04f, 0.1f, 0.8f));
                }
                else
                {
                    EditorGUI.DrawRect(rect, new Color(0.05f, 0.07f, 0.12f, 0.95f));
                }

                DrawNeonFrame(rect, Magenta * 0.55f, Cyan * 0.55f, 1f);
            }

            GUILayout.Space(4f);
        }

        public static void EndToolbarStrip()
        {
            GUILayout.Space(4f);
            EditorGUILayout.EndHorizontal();
        }

        public static bool ModeToggle(bool on, string label, params GUILayoutOption[] options)
        {
            EnsureLoaded();
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = on
                ? new Color(Cyan.r, Cyan.g, Cyan.b, 0.85f)
                : new Color(0.25f, 0.28f, 0.38f, 0.9f);
            bool next = GUILayout.Toggle(on, label, "Button", options);
            GUI.backgroundColor = prev;
            return next;
        }

        public static void PaintPanelBackground(Rect rect, bool useGrid, float dimAlpha)
        {
            if (Event.current.type != EventType.Repaint) return;

            var tex = useGrid ? NeonGrid : (PanelBg != null ? PanelBg : ScanGlow);
            if (tex != null)
            {
                GUI.DrawTexture(rect, tex, ScaleMode.ScaleAndCrop);
                EditorGUI.DrawRect(rect, new Color(0.02f, 0.04f, 0.09f, dimAlpha));
            }
            else
            {
                EditorGUI.DrawRect(rect, new Color(0.05f, 0.07f, 0.12f, 0.95f));
            }

            if (_scanline != null)
                GUI.DrawTextureWithTexCoords(rect, _scanline, new Rect(0f, 0f, 1f, Mathf.Max(1f, rect.height / 8f)));

            DrawNeonFrame(rect, Cyan * 0.75f, Magenta * 0.65f, 1.2f);
        }

        public static GUIStyle SectionTitleStyle
        {
            get
            {
                EnsureLoaded();
                var s = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
                s.normal.textColor = Cyan;
                return s;
            }
        }

        public static GUIStyle MiniLabelStyle
        {
            get
            {
                EnsureLoaded();
                var s = new GUIStyle(EditorStyles.miniLabel);
                s.normal.textColor = TextDim;
                return s;
            }
        }

        public static GUIStyle CardTitle
        {
            get
            {
                EnsureLoaded();
                return _cardTitle;
            }
        }

        public static GUIStyle CardValueStyle
        {
            get
            {
                EnsureLoaded();
                return _cardValue;
            }
        }

        private static string ResolveTextureDir()
        {
            string[] guids = AssetDatabase.FindAssets("CyberpunkTheme t:MonoScript");
            if (guids != null && guids.Length > 0)
            {
                string scriptPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                string editorDir = Path.GetDirectoryName(scriptPath);
                if (!string.IsNullOrEmpty(editorDir))
                    return Path.Combine(editorDir, "UI", "Textures").Replace('\\', '/');
            }

            return "Packages/com.ta.profiling/Editor/UI/Textures";
        }

        private static Texture2D LoadTex(string fileName)
        {
            string packagePath = (_textureDir ?? "Packages/com.ta.profiling/Editor/UI/Textures") + "/" + fileName;
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(packagePath);
            if (tex != null) return tex;

            string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? "";
            string abs = Path.Combine(projectRoot, packagePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs))
                abs = Path.Combine(@"d:\UGit\UnityToolsLib\packages\com.ta.profiling\Editor\UI\Textures", fileName);

            if (!File.Exists(abs)) return null;

            var bytes = File.ReadAllBytes(abs);
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!t.LoadImage(bytes)) return null;
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.hideFlags = HideFlags.DontSave;
            return t;
        }

        private static Texture2D BuildScanline(int lineH, int height)
        {
            var t = new Texture2D(1, height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.DontSave
            };
            for (int y = 0; y < height; y++)
            {
                float a = (y % lineH == 0) ? 0.18f : 0.02f;
                t.SetPixel(0, y, new Color(0.2f, 0.9f, 1f, a));
            }

            t.Apply();
            return t;
        }
    }
}
