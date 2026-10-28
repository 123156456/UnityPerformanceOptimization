using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TA.Profiling.Editor
{
    public enum CullingVizMode
    {
        /// <summary>视锥体内外着色。</summary>
        Frustum = 0,
        /// <summary>锥内可见 vs 锥内不可见（遮挡嫌疑）。</summary>
        Occlusion = 1,
        /// <summary>同时显示视锥线框 + 遮挡嫌疑高亮。</summary>
        Combined = 2
    }

    public sealed class CullingEntry
    {
        public Renderer Renderer;
        public GameObject GameObject;
        public string Path;
        public bool InFrustum;
        public bool IsVisible;
        public bool LikelyOccluded;
        public bool IsOccluderStatic;
        public bool IsOccludeeStatic;
        public int TriangleEstimate;
    }

    public sealed class CullingReport
    {
        public readonly List<CullingEntry> Entries = new List<CullingEntry>();
        public int TotalRenderers;
        public int InFrustum;
        public int OutFrustum;
        public int Visible;
        public int LikelyOccluded;
        public int OccluderStaticCount;
        public int OccludeeStaticCount;
        public bool CameraOcclusionEnabled;
        public string CameraName;
        public bool HasOcclusionData;
    }

    /// <summary>
    /// 视锥体剔除与遮挡剔除（Occlusion）分析 / Scene 可视化。
    /// </summary>
    public static class CullingVisualization
    {
        private static bool _enabled;
        private static bool _subscribed;
        private static CullingVizMode _mode = CullingVizMode.Combined;
        private static Camera _targetCamera;
        private static bool _drawFrustum = true;
        private static bool _drawOutside = true;
        private static bool _drawOccluded = true;
        private static float _opacity = 0.5f;
        private static CullingReport _report = new CullingReport();
        private static Material _drawMat;
        private static readonly Plane[] FrustumPlanes = new Plane[6];
        private static double _lastScan;

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
                    BatchVisualization.Enabled = false;
                    Rescan();
                }

                EnsureSub();
                SceneView.RepaintAll();
            }
        }

        public static CullingVizMode Mode
        {
            get => _mode;
            set { _mode = value; SceneView.RepaintAll(); }
        }

        public static Camera TargetCamera
        {
            get => _targetCamera;
            set
            {
                _targetCamera = value;
                if (_enabled) Rescan();
            }
        }

        public static bool DrawFrustum
        {
            get => _drawFrustum;
            set { _drawFrustum = value; SceneView.RepaintAll(); }
        }

        public static bool DrawOutside
        {
            get => _drawOutside;
            set { _drawOutside = value; SceneView.RepaintAll(); }
        }

        public static bool DrawOccluded
        {
            get => _drawOccluded;
            set { _drawOccluded = value; SceneView.RepaintAll(); }
        }

        public static float Opacity
        {
            get => _opacity;
            set { _opacity = Mathf.Clamp01(value); SceneView.RepaintAll(); }
        }

        public static CullingReport Report => _report;

        public static void Shutdown()
        {
            _enabled = false;
            EnsureSub();
            if (_drawMat != null)
            {
                UnityEngine.Object.DestroyImmediate(_drawMat);
                _drawMat = null;
            }
        }

        public static Camera ResolveCamera()
        {
            if (_targetCamera != null) return _targetCamera;
            if (Camera.main != null) return Camera.main;
            var cams = UnityEngine.Object.FindObjectsOfType<Camera>();
            for (int i = 0; i < cams.Length; i++)
            {
                if (cams[i] != null && cams[i].enabled)
                    return cams[i];
            }

            return SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
        }

        public static CullingReport Rescan()
        {
            var report = new CullingReport();
            var cam = ResolveCamera();
            if (cam == null)
            {
                _report = report;
                return report;
            }

            report.CameraName = cam.name;
            report.CameraOcclusionEnabled = cam.useOcclusionCulling;
            try
            {
                report.HasOcclusionData = StaticOcclusionCulling.umbraDataSize > 0;
            }
            catch
            {
                report.HasOcclusionData = false;
            }

            GeometryUtility.CalculateFrustumPlanes(cam, FrustumPlanes);

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                _report = report;
                return report;
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                foreach (var r in renderers)
                {
                    if (r == null || !r.enabled) continue;
                    // 跳过相机自身等
                    if (r.GetComponent<Camera>() != null) continue;

                    bool inFrustum = GeometryUtility.TestPlanesAABB(FrustumPlanes, r.bounds);
                    bool visible = r.isVisible;
                    // Play / Scene：锥内但 isVisible=false → 多为遮挡或层剔除
                    bool likelyOcc = inFrustum && !visible && Application.isPlaying;

                    bool occluder = false, occludee = false;
                    try
                    {
                        var flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                        occluder = (flags & StaticEditorFlags.OccluderStatic) != 0;
                        occludee = (flags & StaticEditorFlags.OccludeeStatic) != 0;
                    }
                    catch { /* ignore */ }

                    int tris = 0;
                    if (r is MeshRenderer && r.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
                    {
                        var mesh = mf.sharedMesh;
                        for (int i = 0; i < mesh.subMeshCount; i++)
                            tris += (int)mesh.GetIndexCount(i) / 3;
                    }
                    else if (r is SkinnedMeshRenderer sk && sk.sharedMesh != null)
                    {
                        var mesh = sk.sharedMesh;
                        for (int i = 0; i < mesh.subMeshCount; i++)
                            tris += (int)mesh.GetIndexCount(i) / 3;
                    }

                    report.Entries.Add(new CullingEntry
                    {
                        Renderer = r,
                        GameObject = r.gameObject,
                        Path = GetPath(r.transform),
                        InFrustum = inFrustum,
                        IsVisible = visible,
                        LikelyOccluded = likelyOcc,
                        IsOccluderStatic = occluder,
                        IsOccludeeStatic = occludee,
                        TriangleEstimate = tris
                    });

                    report.TotalRenderers++;
                    if (inFrustum) report.InFrustum++;
                    else report.OutFrustum++;
                    if (visible) report.Visible++;
                    if (likelyOcc) report.LikelyOccluded++;
                    if (occluder) report.OccluderStaticCount++;
                    if (occludee) report.OccludeeStaticCount++;
                }
            }

            report.Entries.Sort((a, b) => b.TriangleEstimate.CompareTo(a.TriangleEstimate));
            _report = report;
            _lastScan = EditorApplication.timeSinceStartup;
            SceneView.RepaintAll();
            return report;
        }

        /// <summary>批量为匹配物体设置 Occluder / Occludee Static。</summary>
        public static int SetOcclusionStatic(IList<CullingEntry> entries, bool occluder, bool occludee, bool enable)
        {
            if (entries == null) return 0;
            int n = 0;
            Undo.IncrementCurrentGroup();
            int g = Undo.GetCurrentGroup();
            foreach (var e in entries)
            {
                if (e?.GameObject == null) continue;
                Undo.RecordObject(e.GameObject, "Set Occlusion Static");
                var flags = GameObjectUtility.GetStaticEditorFlags(e.GameObject);
                if (occluder)
                {
                    if (enable) flags |= StaticEditorFlags.OccluderStatic;
                    else flags &= ~StaticEditorFlags.OccluderStatic;
                }

                if (occludee)
                {
                    if (enable) flags |= StaticEditorFlags.OccludeeStatic;
                    else flags &= ~StaticEditorFlags.OccludeeStatic;
                }

                GameObjectUtility.SetStaticEditorFlags(e.GameObject, flags);
                n++;
            }

            Undo.CollapseUndoOperations(g);
            return n;
        }

        public static void SetCameraOcclusion(Camera cam, bool enabled)
        {
            if (cam == null) return;
            Undo.RecordObject(cam, "Toggle Occlusion Culling");
            cam.useOcclusionCulling = enabled;
            EditorUtility.SetDirty(cam);
        }

        public static void OpenOcclusionWindow()
        {
            EditorApplication.ExecuteMenuItem("Window/Rendering/Occlusion Culling");
        }

        private static void EnsureSub()
        {
            if (_enabled && !_subscribed)
            {
                SceneView.duringSceneGui += OnSceneGUI;
                EditorApplication.update += OnUpdate;
                _subscribed = true;
            }
            else if (!_enabled && _subscribed)
            {
                SceneView.duringSceneGui -= OnSceneGUI;
                EditorApplication.update -= OnUpdate;
                _subscribed = false;
            }
        }

        private static void OnUpdate()
        {
            if (!_enabled) return;
            // Play 时降低刷新频率，避免每帧 Rescan 造成抖动
            if (Application.isPlaying && EditorApplication.timeSinceStartup - _lastScan > 0.75)
                Rescan();
        }

        private static void OnSceneGUI(SceneView view)
        {
            if (!_enabled || view == null) return;
            var cam = ResolveCamera();
            if (cam == null) return;

            bool repaint = Event.current.type == EventType.Repaint;

            if (repaint && _drawFrustum && (_mode == CullingVizMode.Frustum || _mode == CullingVizMode.Combined))
                DrawFrustumGizmo(cam);

            if (repaint)
            {
                EnsureMat();
                var report = _report;
                if (report != null)
                {
                    foreach (var e in report.Entries)
                    {
                        if (e.Renderer == null) continue;
                        Color? col = null;

                        if (_mode == CullingVizMode.Frustum || _mode == CullingVizMode.Combined)
                        {
                            if (e.InFrustum)
                                col = new Color(0.25f, 0.85f, 0.4f, _opacity);
                            else if (_drawOutside)
                                col = new Color(0.9f, 0.25f, 0.2f, _opacity * 0.65f);
                        }

                        if ((_mode == CullingVizMode.Occlusion || _mode == CullingVizMode.Combined) &&
                            _drawOccluded && e.LikelyOccluded)
                        {
                            col = new Color(1f, 0.55f, 0.1f, Mathf.Clamp01(_opacity + 0.2f));
                        }

                        if (!col.HasValue) continue;

                        Handles.color = col.Value;
                        Handles.zTest = CompareFunction.LessEqual;
                        Bounds b = e.Renderer.bounds;
                        Handles.DrawWireCube(b.center, b.size);

                        if (e.Renderer is MeshRenderer && e.Renderer.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
                        {
                            if (_drawMat.HasProperty("_Color"))
                                _drawMat.SetColor("_Color", col.Value);
                            _drawMat.color = col.Value;
                            for (int s = 0; s < mf.sharedMesh.subMeshCount; s++)
                            {
                                if (_drawMat.SetPass(0))
                                    Graphics.DrawMeshNow(mf.sharedMesh, e.Renderer.localToWorldMatrix, s);
                            }
                        }
                    }
                }
            }

            Handles.BeginGUI();
            var box = new Rect(12f, 12f, 260f, 70f);
            EditorGUI.DrawRect(box, new Color(0f, 0f, 0f, 0.55f));
            GUI.Label(new Rect(20, 16, 240, 18),
                $"剔除可视 · {cam.name}", EditorStyles.whiteLabel);
            GUI.Label(new Rect(20, 34, 240, 18),
                $"锥内 {_report.InFrustum} / 锥外 {_report.OutFrustum} · 可见 {_report.Visible}",
                EditorStyles.miniLabel);
            GUI.Label(new Rect(20, 50, 240, 18),
                $"遮挡嫌疑 {_report.LikelyOccluded} · Umbra {(_report.HasOcclusionData ? "有数据" : "无")}",
                EditorStyles.miniLabel);
            Handles.EndGUI();
        }

        private static void DrawFrustumGizmo(Camera cam)
        {
            Matrix4x4 old = Handles.matrix;
            // near/far 四边形
            float n = cam.nearClipPlane;
            float f = Mathf.Min(cam.farClipPlane, n + Mathf.Max(20f, cam.farClipPlane * 0.35f));
            // 限制远裁可视化长度，避免撑满场景
            f = Mathf.Min(cam.farClipPlane, cam.transform.position.magnitude + 80f);
            if (f <= n) f = n + 10f;

            Vector3[] near = GetFrustumCorners(cam, n);
            Vector3[] far = GetFrustumCorners(cam, f);

            Handles.color = new Color(0.3f, 0.75f, 1f, 0.9f);
            DrawQuad(near);
            DrawQuad(far);
            for (int i = 0; i < 4; i++)
                Handles.DrawLine(near[i], far[i]);

            Handles.color = new Color(0.3f, 0.75f, 1f, 0.15f);
            Handles.DrawSolidRectangleWithOutline(
                new[] { near[0], near[1], near[2], near[3] },
                new Color(0.2f, 0.6f, 1f, 0.08f),
                new Color(0.3f, 0.75f, 1f, 0.6f));

            Handles.matrix = old;
        }

        private static Vector3[] GetFrustumCorners(Camera cam, float distance)
        {
            var corners = new Vector3[4];
            cam.CalculateFrustumCorners(new Rect(0, 0, 1, 1), distance, Camera.MonoOrStereoscopicEye.Mono, corners);
            for (int i = 0; i < 4; i++)
                corners[i] = cam.transform.TransformPoint(corners[i]);
            // CalculateFrustumCorners 顺序: bottom-left, top-left, top-right, bottom-right
            return corners;
        }

        private static void DrawQuad(Vector3[] c)
        {
            Handles.DrawLine(c[0], c[1]);
            Handles.DrawLine(c[1], c[2]);
            Handles.DrawLine(c[2], c[3]);
            Handles.DrawLine(c[3], c[0]);
        }

        private static void EnsureMat()
        {
            if (_drawMat != null) return;
            var shader = Shader.Find("Hidden/Internal-Colored")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Sprites/Default");
            _drawMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _drawMat.SetInt("_ZWrite", 0);
            _drawMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            _drawMat.SetInt("_Cull", (int)CullMode.Off);
            _drawMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _drawMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        }

        private static string GetPath(Transform t)
        {
            var stack = new Stack<string>();
            while (t != null)
            {
                stack.Push(t.name);
                t = t.parent;
            }

            return string.Join("/", stack);
        }
    }
}
