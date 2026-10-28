using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TA.Profiling.Editor
{
    public enum BatchVizGroupMode
    {
        /// <summary>按共享材质分组（合批潜力）。</summary>
        SharedMaterial = 0,
        /// <summary>材质 + BatchingStatic + Lightmap（更接近真实合批打断条件）。</summary>
        BatchKey = 1,
        /// <summary>仅已标记 Batching Static 的物体，按材质着色。</summary>
        StaticOnly = 2
    }

    public sealed class BatchVizMember
    {
        public MeshRenderer Renderer;
        public Mesh Mesh;
        public Matrix4x4 LocalToWorld;
        public GameObject GameObject;
    }

    public sealed class BatchVizGroup
    {
        public string Key;
        public string Label;
        public Color Color;
        public Material SharedMaterial;
        public bool IsBatchingStatic;
        public int LightmapIndex;
        public readonly List<BatchVizMember> Members = new List<BatchVizMember>();

        public int Count => Members.Count;
        public bool CanBatch => Members.Count >= 2;
    }

    /// <summary>
    /// 场景批次可视化：按合批键分组并用纯色在 Scene 视图叠加绘制。
    /// </summary>
    public static class BatchVisualization
    {
        private static bool _enabled;
        private static bool _subscribed;
        private static bool _drawBounds = true;
        private static bool _drawMeshes = true;
        private static bool _hideSingles;
        private static float _opacity = 0.55f;
        private static BatchVizGroupMode _mode = BatchVizGroupMode.BatchKey;
        private static string _nameFilter = string.Empty;

        private static readonly List<BatchVizGroup> Groups = new List<BatchVizGroup>();
        private static readonly Dictionary<string, BatchVizGroup> GroupMap = new Dictionary<string, BatchVizGroup>();
        private static Material _drawMat;
        private static int _selectedGroup = -1;
        private static double _lastScanTime;

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (_enabled)
                {
                    SceneDebugVisualization.Enabled = false;
                    CullingVisualization.Enabled = false;
                    Rescan();
                }

                EnsureSubscription();
                SceneView.RepaintAll();
            }
        }

        public static bool DrawMeshes
        {
            get => _drawMeshes;
            set { _drawMeshes = value; SceneView.RepaintAll(); }
        }

        public static bool DrawBounds
        {
            get => _drawBounds;
            set { _drawBounds = value; SceneView.RepaintAll(); }
        }

        public static bool HideSingles
        {
            get => _hideSingles;
            set { _hideSingles = value; SceneView.RepaintAll(); }
        }

        public static float Opacity
        {
            get => _opacity;
            set { _opacity = Mathf.Clamp01(value); SceneView.RepaintAll(); }
        }

        public static BatchVizGroupMode Mode
        {
            get => _mode;
            set
            {
                if (_mode == value) return;
                _mode = value;
                Rescan();
            }
        }

        public static string NameFilter
        {
            get => _nameFilter;
            set => _nameFilter = value ?? string.Empty;
        }

        public static int SelectedGroup
        {
            get => _selectedGroup;
            set
            {
                _selectedGroup = value;
                SceneView.RepaintAll();
            }
        }

        public static IReadOnlyList<BatchVizGroup> CurrentGroups => Groups;
        public static int RendererCount { get; private set; }
        public static int MultiBatchCount { get; private set; }
        public static int SingleCount { get; private set; }

        public static void Rescan()
        {
            Groups.Clear();
            GroupMap.Clear();
            RendererCount = 0;
            MultiBatchCount = 0;
            SingleCount = 0;
            _selectedGroup = -1;
            _lastScanTime = EditorApplication.timeSinceStartup;

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return;

            string filter = _nameFilter.Trim();
            bool hasFilter = filter.Length > 0;

            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
                foreach (var r in renderers)
                {
                    if (r == null || !r.enabled) continue;
                    if (hasFilter && r.gameObject.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;

                    bool batchingStatic = false;
                    try
                    {
                        batchingStatic = (GameObjectUtility.GetStaticEditorFlags(r.gameObject)
                                          & StaticEditorFlags.BatchingStatic) != 0;
                    }
                    catch { /* ignore */ }

                    if (_mode == BatchVizGroupMode.StaticOnly && !batchingStatic)
                        continue;

                    var mat = r.sharedMaterial;
                    string key = BuildKey(r, mat, batchingStatic);
                    if (!GroupMap.TryGetValue(key, out var group))
                    {
                        group = new BatchVizGroup
                        {
                            Key = key,
                            Label = BuildLabel(mat, batchingStatic, r.lightmapIndex),
                            Color = ColorFromKey(key),
                            SharedMaterial = mat,
                            IsBatchingStatic = batchingStatic,
                            LightmapIndex = r.lightmapIndex
                        };
                        GroupMap[key] = group;
                        Groups.Add(group);
                    }

                    group.Members.Add(new BatchVizMember
                    {
                        Renderer = r,
                        Mesh = mf.sharedMesh,
                        LocalToWorld = r.localToWorldMatrix,
                        GameObject = r.gameObject
                    });
                    RendererCount++;
                }
            }

            Groups.Sort((a, b) => b.Count.CompareTo(a.Count));
            foreach (var g in Groups)
            {
                if (g.CanBatch) MultiBatchCount++;
                else SingleCount++;
            }

            SceneView.RepaintAll();
        }

        public static void Shutdown()
        {
            _enabled = false;
            EnsureSubscription();
            DestroyDrawMat();
            Groups.Clear();
            GroupMap.Clear();
        }

        public static void SelectGroupObjects(int index)
        {
            if (index < 0 || index >= Groups.Count) return;
            var list = new List<UnityEngine.Object>();
            foreach (var m in Groups[index].Members)
            {
                if (m.GameObject != null)
                    list.Add(m.GameObject);
            }

            Selection.objects = list.ToArray();
            if (list.Count > 0)
                EditorGUIUtility.PingObject(list[0]);
            SelectedGroup = index;
        }

        private static string BuildKey(MeshRenderer r, Material mat, bool batchingStatic)
        {
            int matId = mat != null ? mat.GetInstanceID() : 0;
            switch (_mode)
            {
                case BatchVizGroupMode.SharedMaterial:
                    return "mat:" + matId;
                case BatchVizGroupMode.StaticOnly:
                    return "static|mat:" + matId;
                default:
                    // 材质 + static 标记 + lightmap + 阴影模式（常见打断合批因素）
                    return $"mat:{matId}|st:{(batchingStatic ? 1 : 0)}|lm:{r.lightmapIndex}|sh:{(int)r.shadowCastingMode}|rs:{(r.receiveShadows ? 1 : 0)}";
            }
        }

        private static string BuildLabel(Material mat, bool batchingStatic, int lightmapIndex)
        {
            string matName = mat != null ? mat.name : "(无材质)";
            if (_mode == BatchVizGroupMode.SharedMaterial)
                return matName;
            return $"{matName}  ·  {(batchingStatic ? "Static" : "Dynamic")}  ·  LM{lightmapIndex}";
        }

        private static Color ColorFromKey(string key)
        {
            unchecked
            {
                int h = key.GetHashCode();
                float hue = ((h & 0xFFFF) / 65535f);
                float sat = 0.65f + ((h >> 16) & 0xFF) / 255f * 0.25f;
                float val = 0.85f;
                var c = Color.HSVToRGB(hue, sat, val);
                c.a = 1f;
                return c;
            }
        }

        private static void EnsureSubscription()
        {
            if (_enabled && !_subscribed)
            {
                SceneView.duringSceneGui += OnSceneGUI;
                _subscribed = true;
            }
            else if (!_enabled && _subscribed)
            {
                SceneView.duringSceneGui -= OnSceneGUI;
                _subscribed = false;
            }
        }

        private static void OnSceneGUI(SceneView view)
        {
            if (!_enabled || view == null) return;
            if (Event.current.type != EventType.Repaint)
            {
                Handles.BeginGUI();
                var rectEarly = new Rect(12f, 12f, 220f, 54f);
                EditorGUI.DrawRect(rectEarly, new Color(0f, 0f, 0f, 0.55f));
                GUI.Label(new Rect(20f, 16f, 200f, 18f), $"批次可视 · {Groups.Count} 组", EditorStyles.whiteLabel);
                Handles.EndGUI();
                return;
            }

            EnsureDrawMat();

            for (int gi = 0; gi < Groups.Count; gi++)
            {
                var g = Groups[gi];
                if (_hideSingles && !g.CanBatch) continue;
                if (_selectedGroup >= 0 && _selectedGroup != gi) continue;

                var col = g.Color;
                col.a = _opacity;
                if (_drawMat.HasProperty("_Color"))
                    _drawMat.SetColor("_Color", col);
                _drawMat.color = col;

                foreach (var m in g.Members)
                {
                    if (m.Renderer == null || m.Mesh == null) continue;

                    if (_drawMeshes)
                    {
                        for (int s = 0; s < m.Mesh.subMeshCount; s++)
                        {
                            if (_drawMat.SetPass(0))
                                Graphics.DrawMeshNow(m.Mesh, m.LocalToWorld, s);
                        }
                    }

                    if (_drawBounds)
                    {
                        Handles.color = new Color(col.r, col.g, col.b, Mathf.Clamp01(_opacity + 0.25f));
                        Handles.zTest = CompareFunction.LessEqual;
                        Bounds b = m.Renderer.bounds;
                        Handles.DrawWireCube(b.center, b.size);
                    }
                }
            }

            Handles.BeginGUI();
            var rect = new Rect(12f, 12f, 220f, 54f);
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.55f));
            GUI.Label(new Rect(20f, 16f, 200f, 18f), $"批次可视 · {Groups.Count} 组 / {RendererCount} MR", EditorStyles.whiteLabel);
            GUI.Label(new Rect(20f, 34f, 200f, 18f), $"可合批组 {MultiBatchCount} · 单件 {SingleCount}", EditorStyles.miniLabel);
            Handles.EndGUI();
        }

        private static void EnsureDrawMat()
        {
            if (_drawMat != null) return;
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            _drawMat = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _drawMat.SetInt("_ZWrite", 0);
            _drawMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            _drawMat.SetInt("_Cull", (int)CullMode.Off);
            _drawMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _drawMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            if (_drawMat.HasProperty("_Color"))
                _drawMat.SetColor("_Color", Color.white);
        }

        private static void DestroyDrawMat()
        {
            if (_drawMat == null) return;
            UnityEngine.Object.DestroyImmediate(_drawMat);
            _drawMat = null;
        }
    }
}
