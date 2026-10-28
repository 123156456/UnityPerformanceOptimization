using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TA.Profiling.Editor
{
    public enum SceneVizMode
    {
        Overdraw = 0,
        Lod = 1,
        TriDensity = 2,
        TexelDensity = 3,
        Mipmap = 4,
        ShadowCascade = 5,
        LightmapProbe = 6,
        SrpInstancing = 7,
        ShaderComplexity = 8,
        Layer = 9,
        ReflectionProbe = 10,
        ParticleCoverage = 11
    }

    public sealed class SceneVizItem
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public Matrix4x4 Matrix;
        public GameObject GameObject;
        public Color Color;
        public string Label;
        public float Metric;
        public Material SourceMaterial;
        public Texture SourceTexture;
    }

    /// <summary>
    /// TA 场景调试可视化：Overdraw / LOD / 面密度 / 纹素密度 / Mip /
    /// 阴影级联 / 光照贴图·探针 / SRP·实例化 / Shader 复杂度 / Layer / 反射探针 / 粒子覆盖。
    /// </summary>
    public static class SceneDebugVisualization
    {
        private static bool _enabled;
        private static bool _subscribed;
        private static SceneVizMode _mode = SceneVizMode.Overdraw;
        private static float _opacity = 0.55f;
        private static float _triDensityScale = 1f;
        private static float _texelTarget = 10f; // px / world unit 目标
        private static readonly List<SceneVizItem> Items = new List<SceneVizItem>(512);
        private static readonly List<ReflectionProbe> Probes = new List<ReflectionProbe>();
        private static readonly List<LightProbeGroup> ProbeGroups = new List<LightProbeGroup>();
        private static readonly List<ParticleSystem> Particles = new List<ParticleSystem>();
        private static Material _colorMat;
        private static Material _overdrawMat;
        private static Material _mipMat;
        private static Light _cascadeLight;
        private static string _status = string.Empty;
        private static int _selected = -1;

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (_enabled)
                {
                    // 同时只开一种 Scene 叠加，避免互相抢画导致频闪
                    BatchVisualization.Enabled = false;
                    CullingVisualization.Enabled = false;
                    Rescan();
                }

                EnsureSub();
                SceneView.RepaintAll();
            }
        }

        public static SceneVizMode Mode
        {
            get => _mode;
            set
            {
                if (_mode == value) return;
                _mode = value;
                Rescan();
            }
        }

        public static float Opacity
        {
            get => _opacity;
            set { _opacity = Mathf.Clamp01(value); SceneView.RepaintAll(); }
        }

        public static float TriDensityScale
        {
            get => _triDensityScale;
            set => _triDensityScale = Mathf.Max(0.01f, value);
        }

        public static float TexelTarget
        {
            get => _texelTarget;
            set => _texelTarget = Mathf.Max(0.1f, value);
        }

        public static IReadOnlyList<SceneVizItem> CurrentItems => Items;
        public static string Status => _status;
        public static int SelectedIndex
        {
            get => _selected;
            set { _selected = value; SceneView.RepaintAll(); }
        }

        public static string ModeLabel(SceneVizMode m)
        {
            switch (m)
            {
                case SceneVizMode.Overdraw: return "Overdraw 叠画";
                case SceneVizMode.Lod: return "LOD 等级";
                case SceneVizMode.TriDensity: return "三角面密度";
                case SceneVizMode.TexelDensity: return "纹素密度";
                case SceneVizMode.Mipmap: return "Mipmap";
                case SceneVizMode.ShadowCascade: return "阴影级联";
                case SceneVizMode.LightmapProbe: return "Lightmap / Probe";
                case SceneVizMode.SrpInstancing: return "SRP / Instancing";
                case SceneVizMode.ShaderComplexity: return "Shader 复杂度";
                case SceneVizMode.Layer: return "Layer 图层";
                case SceneVizMode.ReflectionProbe: return "反射探针范围";
                case SceneVizMode.ParticleCoverage: return "粒子覆盖";
                default: return m.ToString();
            }
        }

        public static void Shutdown()
        {
            _enabled = false;
            EnsureSub();
            DestroyMat(ref _colorMat);
            DestroyMat(ref _overdrawMat);
            DestroyMat(ref _mipMat);
            Items.Clear();
        }

        public static void Rescan()
        {
            Items.Clear();
            Probes.Clear();
            ProbeGroups.Clear();
            Particles.Clear();
            _selected = -1;
            _cascadeLight = null;

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                _status = "无有效场景";
                return;
            }

            switch (_mode)
            {
                case SceneVizMode.Overdraw: BuildOverdraw(scene); break;
                case SceneVizMode.Lod: BuildLod(scene); break;
                case SceneVizMode.TriDensity: BuildTriDensity(scene); break;
                case SceneVizMode.TexelDensity: BuildTexelDensity(scene); break;
                case SceneVizMode.Mipmap: BuildMipmap(scene); break;
                case SceneVizMode.ShadowCascade: BuildShadowCascade(scene); break;
                case SceneVizMode.LightmapProbe: BuildLightmapProbe(scene); break;
                case SceneVizMode.SrpInstancing: BuildSrpInstancing(scene); break;
                case SceneVizMode.ShaderComplexity: BuildShaderComplexity(scene); break;
                case SceneVizMode.Layer: BuildLayer(scene); break;
                case SceneVizMode.ReflectionProbe: BuildReflectionProbes(scene); break;
                case SceneVizMode.ParticleCoverage: BuildParticles(scene); break;
            }

            _status = $"{ModeLabel(_mode)} · {Items.Count} 项";
            SceneView.RepaintAll();
        }

        public static void Ping(int index)
        {
            if (index < 0 || index >= Items.Count) return;
            var go = Items[index].GameObject;
            if (go == null) return;
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            _selected = index;
        }

        // ───────── builders ─────────

        private static void BuildOverdraw(Scene scene)
        {
            foreach (var r in EnumerateRenderers(scene))
            {
                if (!TryMesh(r, out var mesh, out var mtx)) continue;
                Items.Add(new SceneVizItem
                {
                    Renderer = r, Mesh = mesh, Matrix = mtx, GameObject = r.gameObject,
                    Color = new Color(1f, 0.25f, 0.1f, 1f), Label = r.name, Metric = 1f
                });
            }
        }

        private static void BuildLod(Scene scene)
        {
            var cam = Camera.main != null ? Camera.main.transform.position
                : (SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera.transform.position : Vector3.zero);
            Color[] lodColors =
            {
                new Color(0.2f, 0.9f, 0.35f), new Color(0.5f, 0.85f, 0.2f), new Color(0.95f, 0.85f, 0.2f),
                new Color(0.95f, 0.55f, 0.15f), new Color(0.9f, 0.25f, 0.2f), new Color(0.6f, 0.2f, 0.7f)
            };

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var lod in root.GetComponentsInChildren<LODGroup>(true))
                {
                    if (lod == null) continue;
                    int level = GetCurrentLod(lod, cam);
                    var lods = lod.GetLODs();
                    if (level < 0 || level >= lods.Length)
                    {
                        // culled
                        foreach (var r in lod.GetComponentsInChildren<Renderer>())
                        {
                            if (!TryMesh(r, out var mesh, out var mtx)) continue;
                            Items.Add(Item(r, mesh, mtx, Color.magenta, "LOD Culled", level));
                        }
                        continue;
                    }

                    var col = lodColors[Mathf.Clamp(level, 0, lodColors.Length - 1)];
                    foreach (var r in lods[level].renderers)
                    {
                        if (r == null || !TryMesh(r, out var mesh, out var mtx)) continue;
                        Items.Add(Item(r, mesh, mtx, col, $"LOD{level}", level));
                    }
                }
            }
        }

        private static int GetCurrentLod(LODGroup group, Vector3 camPos)
        {
            var lods = group.GetLODs();
            if (lods == null || lods.Length == 0) return -1;

            Bounds bounds = new Bounds(group.transform.position, Vector3.one * 0.1f);
            bool init = false;
            for (int i = 0; i < lods.Length; i++)
            {
                var renderers = lods[i].renderers;
                if (renderers == null) continue;
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    if (!init) { bounds = r.bounds; init = true; }
                    else bounds.Encapsulate(r.bounds);
                }
            }

            float size = bounds.extents.magnitude;
            float dist = Vector3.Distance(camPos, bounds.center);
            float relativeHeight = size / Mathf.Max(0.01f, dist);
            for (int i = 0; i < lods.Length; i++)
            {
                if (relativeHeight >= lods[i].screenRelativeTransitionHeight)
                    return i;
            }

            return lods.Length; // culled
        }

        private static void BuildTriDensity(Scene scene)
        {
            foreach (var r in EnumerateRenderers(scene))
            {
                if (!TryMesh(r, out var mesh, out var mtx)) continue;
                int tris = CountTris(mesh);
                float area = ApproxWorldArea(r.bounds);
                float density = tris / Mathf.Max(0.01f, area) * _triDensityScale;
                // 热力：低绿高红
                float t = Mathf.Clamp01(density / 50f);
                var col = Color.Lerp(new Color(0.2f, 0.85f, 0.35f), new Color(0.95f, 0.2f, 0.15f), t);
                Items.Add(Item(r, mesh, mtx, col, $"{tris} tris · {density:F1}/m²", density));
            }
        }

        private static void BuildTexelDensity(Scene scene)
        {
            foreach (var r in EnumerateRenderers(scene))
            {
                if (!(r is MeshRenderer) || !TryMesh(r, out var mesh, out var mtx)) continue;
                var mat = r.sharedMaterial;
                Texture tex = SafeGetAlbedoTexture(mat);
                float texel = EstimateTexelDensity(mesh, r, tex);
                float ratio = texel / _texelTarget;
                // 接近目标=绿，过低蓝，过高红
                Color col;
                if (ratio < 0.5f) col = Color.Lerp(new Color(0.2f, 0.4f, 1f), Color.green, ratio * 2f);
                else if (ratio < 1.5f) col = Color.Lerp(Color.green, new Color(0.95f, 0.85f, 0.2f), (ratio - 0.5f));
                else col = Color.Lerp(new Color(0.95f, 0.85f, 0.2f), Color.red, Mathf.Clamp01((ratio - 1.5f) / 2f));

                Items.Add(new SceneVizItem
                {
                    Renderer = r, Mesh = mesh, Matrix = mtx, GameObject = r.gameObject,
                    Color = col, Label = $"{texel:F1} px/u (目标 {_texelTarget:F0})", Metric = texel,
                    SourceMaterial = mat, SourceTexture = tex
                });
            }
        }

        private static void BuildMipmap(Scene scene)
        {
            foreach (var r in EnumerateRenderers(scene))
            {
                if (!TryMesh(r, out var mesh, out var mtx)) continue;
                var mat = r.sharedMaterial;
                Texture tex = SafeGetAlbedoTexture(mat);
                float mipEst = EstimateMipLevel(r, tex);
                var col = MipHeatColor(mipEst);
                Items.Add(new SceneVizItem
                {
                    Renderer = r, Mesh = mesh, Matrix = mtx, GameObject = r.gameObject,
                    Color = col, Label = tex != null ? $"mip≈{mipEst:F1} · {tex.name}" : "无主贴图",
                    Metric = mipEst, SourceMaterial = mat, SourceTexture = tex
                });
            }
        }

        private static void BuildShadowCascade(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var light in root.GetComponentsInChildren<Light>(true))
                {
                    if (light != null && light.type == LightType.Directional && light.shadows != LightShadows.None)
                    {
                        _cascadeLight = light;
                        break;
                    }
                }
                if (_cascadeLight != null) break;
            }

            int cascades = QualitySettings.shadowCascades;
            foreach (var r in EnumerateRenderers(scene))
            {
                if (!TryMesh(r, out var mesh, out var mtx)) continue;
                int cascade = EstimateCascade(r.bounds.center, cascades);
                Color[] cols =
                {
                    new Color(0.2f, 0.85f, 0.4f), new Color(0.95f, 0.85f, 0.2f),
                    new Color(0.95f, 0.5f, 0.15f), new Color(0.85f, 0.2f, 0.25f)
                };
                var col = cols[Mathf.Clamp(cascade, 0, cols.Length - 1)];
                Items.Add(Item(r, mesh, mtx, col, $"Cascade {cascade}/{cascades}", cascade));
            }
        }

        private static void BuildLightmapProbe(Scene scene)
        {
            foreach (var r in EnumerateRenderers(scene))
            {
                if (!TryMesh(r, out var mesh, out var mtx)) continue;
                int lm = r.lightmapIndex;
                Color col;
                string label;
                if (lm >= 0)
                {
                    col = ColorFromHash(lm + 17);
                    label = $"Lightmap {lm}";
                }
                else if (r.lightProbeUsage != LightProbeUsage.Off)
                {
                    col = new Color(0.4f, 0.7f, 1f);
                    label = "Light Probes";
                }
                else
                {
                    col = new Color(0.35f, 0.35f, 0.35f);
                    label = "无 GI";
                }

                Items.Add(Item(r, mesh, mtx, col, label, lm));
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                ProbeGroups.AddRange(root.GetComponentsInChildren<LightProbeGroup>(true));
            }
        }

        private static void BuildSrpInstancing(Scene scene)
        {
            foreach (var r in EnumerateRenderers(scene))
            {
                if (!TryMesh(r, out var mesh, out var mtx)) continue;
                var mat = r.sharedMaterial;
                bool instancing = mat != null && mat.enableInstancing;
                bool srpOk = IsSrpBatcherFriendly(mat);
                // 绿=两者好，黄=仅其一，红=都差
                Color col;
                string label;
                if (srpOk && instancing) { col = new Color(0.25f, 0.85f, 0.4f); label = "SRP✓ Inst✓"; }
                else if (srpOk) { col = new Color(0.55f, 0.85f, 0.3f); label = "SRP✓ Inst✗"; }
                else if (instancing) { col = new Color(0.95f, 0.75f, 0.2f); label = "SRP✗ Inst✓"; }
                else { col = new Color(0.9f, 0.3f, 0.25f); label = "SRP✗ Inst✗"; }

                Items.Add(new SceneVizItem
                {
                    Renderer = r, Mesh = mesh, Matrix = mtx, GameObject = r.gameObject,
                    Color = col, Label = label, Metric = (srpOk ? 2 : 0) + (instancing ? 1 : 0),
                    SourceMaterial = mat
                });
            }
        }

        private static void BuildShaderComplexity(Scene scene)
        {
            foreach (var r in EnumerateRenderers(scene))
            {
                if (!TryMesh(r, out var mesh, out var mtx)) continue;
                var mat = r.sharedMaterial;
                float score = ScoreShaderComplexity(mat);
                float t = Mathf.Clamp01(score / 40f);
                var col = Color.Lerp(new Color(0.25f, 0.85f, 0.4f), new Color(0.95f, 0.2f, 0.2f), t);
                Items.Add(new SceneVizItem
                {
                    Renderer = r, Mesh = mesh, Matrix = mtx, GameObject = r.gameObject,
                    Color = col, Label = mat != null ? $"{mat.shader.name} · {score:F0}" : "无材质",
                    Metric = score, SourceMaterial = mat
                });
            }
        }

        private static void BuildLayer(Scene scene)
        {
            foreach (var r in EnumerateRenderers(scene))
            {
                if (!TryMesh(r, out var mesh, out var mtx)) continue;
                int layer = r.gameObject.layer;
                var col = ColorFromHash(layer * 37 + 3);
                string name = LayerMask.LayerToName(layer);
                if (string.IsNullOrEmpty(name)) name = layer.ToString();
                Items.Add(Item(r, mesh, mtx, col, $"Layer {name}", layer));
            }
        }

        private static void BuildReflectionProbes(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
                Probes.AddRange(root.GetComponentsInChildren<ReflectionProbe>(true));

            // 仍把受探针影响的物体浅色标出
            foreach (var r in EnumerateRenderers(scene))
            {
                if (!TryMesh(r, out var mesh, out var mtx)) continue;
                bool affected = false;
                foreach (var p in Probes)
                {
                    if (p == null || !p.enabled) continue;
                    if (p.bounds.Intersects(r.bounds)) { affected = true; break; }
                }

                var col = affected ? new Color(0.35f, 0.65f, 1f, 1f) : new Color(0.3f, 0.3f, 0.3f, 1f);
                Items.Add(Item(r, mesh, mtx, col, affected ? "探针影响内" : "外", affected ? 1 : 0));
            }
        }

        private static void BuildParticles(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
                Particles.AddRange(root.GetComponentsInChildren<ParticleSystem>(true));

            var cam = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main;
            foreach (var ps in Particles)
            {
                if (ps == null) continue;
                var r = ps.GetComponent<ParticleSystemRenderer>();
                if (r == null) continue;
                float cover = EstimateScreenCoverage(r.bounds, cam);
                float t = Mathf.Clamp01(cover * 8f);
                var col = Color.Lerp(new Color(0.25f, 0.8f, 0.4f), new Color(0.95f, 0.25f, 0.2f), t);
                Mesh mesh = null;
                Matrix4x4 mtx = r.localToWorldMatrix;
                // 粒子用包围盒即可
                Items.Add(new SceneVizItem
                {
                    Renderer = r, Mesh = mesh, Matrix = mtx, GameObject = ps.gameObject,
                    Color = col,
                    Label = $"{ps.name} · {ps.particleCount}p · 屏占比≈{cover * 100f:F1}%",
                    Metric = cover
                });
            }
        }

        // ───────── scene draw ─────────

        private static void EnsureSub()
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
            // 仅在 Repaint 绘制，避免 Layout/MouseMove 触发 DrawMesh 造成频闪与重复开销
            if (Event.current.type != EventType.Repaint && Event.current.type != EventType.Layout)
            {
                // Layout 时仍要占位图例，但跳过网格绘制
            }

            bool repaint = Event.current.type == EventType.Repaint;
            EnsureMats();

            if (repaint)
            {
                if (_mode == SceneVizMode.Overdraw)
                    DrawOverdraw(view);
                else if (_mode == SceneVizMode.Mipmap && _mipMat != null)
                    DrawMip(view);
                else
                    DrawColoredMeshes(view);

                if (_mode == SceneVizMode.ShadowCascade && _cascadeLight != null)
                    DrawCascadePlanes(view);
                if (_mode == SceneVizMode.ReflectionProbe)
                    DrawProbeVolumes();
                if (_mode == SceneVizMode.LightmapProbe)
                    DrawLightProbeSpheres();
                if (_mode == SceneVizMode.ParticleCoverage)
                    DrawParticleBounds();
            }

            Handles.BeginGUI();
            var box = new Rect(12, 12, 280, 52);
            EditorGUI.DrawRect(box, new Color(0f, 0f, 0f, 0.55f));
            GUI.Label(new Rect(20, 16, 260, 18), "场景可视 · " + ModeLabel(_mode), EditorStyles.whiteLabel);
            GUI.Label(new Rect(20, 34, 260, 18), _status, EditorStyles.miniLabel);
            Handles.EndGUI();
        }

        private static void DrawOverdraw(SceneView view)
        {
            if (_overdrawMat == null) return;
            foreach (var it in Items)
            {
                if (it.Mesh == null) continue;
                DrawMeshNow(it.Mesh, it.Matrix, _overdrawMat);
            }
        }

        private static void DrawMip(SceneView view)
        {
            Texture last = null;
            foreach (var it in Items)
            {
                if (it.Mesh == null) continue;
                if (it.SourceTexture != null && it.SourceTexture != last)
                {
                    last = it.SourceTexture;
                    if (_mipMat.HasProperty("_MainTex"))
                        _mipMat.SetTexture("_MainTex", last);
                }

                DrawMeshNow(it.Mesh, it.Matrix, _mipMat);
            }
        }

        private static void DrawColoredMeshes(SceneView view)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                var it = Items[i];
                var col = it.Color;
                col.a = _opacity;
                if (_selected >= 0 && _selected != i)
                    col.a *= 0.15f;

                if (it.Mesh != null)
                {
                    if (_colorMat.HasProperty("_Color"))
                        _colorMat.SetColor("_Color", col);
                    _colorMat.color = col;
                    DrawMeshNow(it.Mesh, it.Matrix, _colorMat);
                }
                else if (it.Renderer != null)
                {
                    Handles.color = col;
                    Handles.zTest = CompareFunction.LessEqual;
                    Handles.DrawWireCube(it.Renderer.bounds.center, it.Renderer.bounds.size);
                }
            }
        }

        private static void DrawMeshNow(Mesh mesh, Matrix4x4 matrix, Material mat)
        {
            if (mesh == null || mat == null) return;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mat.SetPass(0))
                    Graphics.DrawMeshNow(mesh, matrix, s);
            }
        }

        private static void DrawCascadePlanes(SceneView view)
        {
            if (_cascadeLight == null) return;
            var cam = Camera.main != null ? Camera.main : view.camera;
            if (cam == null) return;
            // 简化：沿视线画级联分界圆盘
            float far = QualitySettings.shadowDistance;
            float[] splits = GetCascadeSplits(QualitySettings.shadowCascades);
            Handles.color = new Color(1f, 1f, 1f, 0.35f);
            Vector3 origin = cam.transform.position;
            Vector3 fwd = cam.transform.forward;
            for (int i = 0; i < splits.Length; i++)
            {
                float d = splits[i] * far;
                Handles.DrawWireDisc(origin + fwd * d, fwd, Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * d * 1.2f);
            }
        }

        private static void DrawProbeVolumes()
        {
            foreach (var p in Probes)
            {
                if (p == null) continue;
                Handles.color = p.enabled ? new Color(0.3f, 0.65f, 1f, 0.35f) : new Color(0.4f, 0.4f, 0.4f, 0.2f);
                var b = p.bounds;
                Handles.DrawWireCube(b.center, b.size);
                Handles.Label(b.center, p.name);
            }
        }

        private static void DrawLightProbeSpheres()
        {
            Handles.color = new Color(0.5f, 0.8f, 1f, 0.5f);
            foreach (var g in ProbeGroups)
            {
                if (g == null || g.probePositions == null) continue;
                foreach (var lp in g.probePositions)
                {
                    Vector3 w = g.transform.TransformPoint(lp);
                    Handles.DrawWireDisc(w, Vector3.up, 0.15f);
                }
            }
        }

        private static void DrawParticleBounds()
        {
            foreach (var it in Items)
            {
                if (it.Renderer == null) continue;
                Handles.color = it.Color;
                Handles.zTest = CompareFunction.Always;
                Handles.DrawWireCube(it.Renderer.bounds.center, it.Renderer.bounds.size);
            }
        }

        // ───────── helpers ─────────

        private static IEnumerable<Renderer> EnumerateRenderers(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    if (r is ParticleSystemRenderer) continue;
                    yield return r;
                }
            }
        }

        private static bool TryMesh(Renderer r, out Mesh mesh, out Matrix4x4 mtx)
        {
            mesh = null;
            mtx = r.localToWorldMatrix;
            if (r is MeshRenderer && r.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
            {
                mesh = mf.sharedMesh;
                return true;
            }

            if (r is SkinnedMeshRenderer sk && sk.sharedMesh != null)
            {
                mesh = sk.sharedMesh;
                return true;
            }

            return false;
        }

        private static SceneVizItem Item(Renderer r, Mesh mesh, Matrix4x4 mtx, Color col, string label, float metric)
        {
            return new SceneVizItem
            {
                Renderer = r, Mesh = mesh, Matrix = mtx, GameObject = r.gameObject,
                Color = col, Label = label, Metric = metric
            };
        }

        private static int CountTris(Mesh mesh)
        {
            int t = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                t += (int)mesh.GetIndexCount(i) / 3;
            return t;
        }

        private static float ApproxWorldArea(Bounds b)
        {
            var s = b.size;
            return 2f * (s.x * s.y + s.y * s.z + s.z * s.x);
        }

        private static float EstimateTexelDensity(Mesh mesh, Renderer r, Texture tex)
        {
            if (tex == null || mesh == null) return 0f;
            var uvs = mesh.uv;
            var verts = mesh.vertices;
            if (uvs == null || uvs.Length < 3 || verts == null || verts.Length < 3)
                return 0f;

            // 抽样前若干三角形估算 UV 面积 / 世界面积
            var tris = mesh.triangles;
            if (tris == null || tris.Length < 3) return 0f;
            float uvArea = 0f, worldArea = 0f;
            int max = Mathf.Min(tris.Length, 300);
            var l2w = r.transform.localToWorldMatrix;
            for (int i = 0; i + 2 < max; i += 3)
            {
                Vector2 a = uvs[tris[i]], b = uvs[tris[i + 1]], c = uvs[tris[i + 2]];
                uvArea += Mathf.Abs(Cross2(b - a, c - a)) * 0.5f;
                Vector3 wa = l2w.MultiplyPoint3x4(verts[tris[i]]);
                Vector3 wb = l2w.MultiplyPoint3x4(verts[tris[i + 1]]);
                Vector3 wc = l2w.MultiplyPoint3x4(verts[tris[i + 2]]);
                worldArea += Vector3.Cross(wb - wa, wc - wa).magnitude * 0.5f;
            }

            if (worldArea < 1e-6f) return 0f;
            float texels = tex.width * Mathf.Sqrt(uvArea / worldArea);
            return texels;
        }

        public static Texture SafeGetAlbedoTexture(Material mat)
        {
            if (mat == null) return null;
            string[] names = { "_BaseMap", "_MainTex", "_BaseColorMap", "_Diffuse", "_Albedo", "_MainTexture", "_BaseColorTexture" };
            for (int i = 0; i < names.Length; i++)
            {
                if (!mat.HasProperty(names[i])) continue;
                var t = mat.GetTexture(names[i]);
                if (t != null) return t;
            }

            try
            {
                var shader = mat.shader;
                if (shader == null) return null;
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    var t = mat.GetTexture(shader.GetPropertyName(i));
                    if (t != null) return t;
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static float Cross2(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static float EstimateMipLevel(Renderer r, Texture tex)
        {
            if (tex == null) return 0f;
            var cam = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main;
            if (cam == null) return 0f;
            float dist = Vector3.Distance(cam.transform.position, r.bounds.center);
            float worldSize = r.bounds.extents.magnitude;
            float screenH = 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float px = worldSize / Mathf.Max(1e-4f, screenH) * cam.pixelHeight;
            float mip = Mathf.Log(Mathf.Max(1f, tex.width / Mathf.Max(1f, px)), 2f);
            return Mathf.Clamp(mip, 0f, 8f);
        }

        private static Color MipHeatColor(float mip)
        {
            mip = Mathf.Clamp(mip, 0f, 5f);
            if (mip < 1f) return Color.Lerp(new Color(1f, 0.15f, 0.1f), new Color(1f, 0.55f, 0.1f), mip);
            if (mip < 2f) return Color.Lerp(new Color(1f, 0.55f, 0.1f), new Color(1f, 0.9f, 0.2f), mip - 1f);
            if (mip < 3f) return Color.Lerp(new Color(1f, 0.9f, 0.2f), new Color(0.25f, 0.85f, 0.35f), mip - 2f);
            if (mip < 4f) return Color.Lerp(new Color(0.25f, 0.85f, 0.35f), new Color(0.25f, 0.55f, 1f), mip - 3f);
            return Color.Lerp(new Color(0.25f, 0.55f, 1f), new Color(0.45f, 0.25f, 0.85f), mip - 4f);
        }

        private static int EstimateCascade(Vector3 worldPos, int cascades)
        {
            var cam = Camera.main;
            if (cam == null && SceneView.lastActiveSceneView != null)
                cam = SceneView.lastActiveSceneView.camera;
            if (cam == null || cascades <= 1) return 0;
            float dist = Vector3.Dot(worldPos - cam.transform.position, cam.transform.forward);
            float far = QualitySettings.shadowDistance;
            float[] splits = GetCascadeSplits(cascades);
            for (int i = 0; i < splits.Length; i++)
            {
                if (dist < splits[i] * far) return i;
            }

            return cascades - 1;
        }

        private static float[] GetCascadeSplits(int count)
        {
            // 近似与 Unity 默认级联比例
            switch (count)
            {
                case 2: return new[] { 0.25f, 1f };
                case 4: return new[] { 0.1f, 0.25f, 0.5f, 1f };
                default: return new[] { 1f };
            }
        }

        private static bool IsSrpBatcherFriendly(Material mat)
        {
            if (mat == null || mat.shader == null) return false;

            // 部分 Unity 版本无 GetSRPBatcherCompatibilityCode，用反射探测；失败则启发式判断
            try
            {
                var method = typeof(ShaderUtil).GetMethod(
                    "GetSRPBatcherCompatibilityCode",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method != null)
                {
                    object result = method.Invoke(null, new object[] { mat.shader, 0 });
                    if (result is int code) return code == 0;
                }
            }
            catch
            {
                // fall through
            }

            string n = mat.shader.name;
            if (string.IsNullOrEmpty(n)) return false;
            if (n.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (n.IndexOf("UI/", StringComparison.Ordinal) >= 0) return false;
            if (n.IndexOf("Sprites/", StringComparison.Ordinal) >= 0) return false;
            // URP/HDRP Lit 系通常兼容；旧版 FixedFunction 不兼容
            if (n.IndexOf("Legacy Shaders", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return mat.enableInstancing || n.IndexOf("Universal Render Pipeline", StringComparison.Ordinal) >= 0
                   || n.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0
                   || n.IndexOf("/Lit", StringComparison.Ordinal) >= 0
                   || n.IndexOf("Unlit", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static float ScoreShaderComplexity(Material mat)
        {
            if (mat == null || mat.shader == null) return 0f;
            float score = mat.passCount * 8f;
            var shader = mat.shader;
            score += shader.maximumLOD > 0 ? shader.maximumLOD / 100f : 5f;
            try
            {
                int props = shader.GetPropertyCount();
                for (int i = 0; i < props; i++)
                {
                    if (shader.GetPropertyType(i) == ShaderPropertyType.Texture) score += 1.5f;
                    else score += 0.35f;
                }
            }
            catch { score += 5f; }

            string n = shader.name.ToLowerInvariant();
            if (n.Contains("lit") || n.Contains("standard")) score += 8f;
            if (n.Contains("complex") || n.Contains("skin") || n.Contains("hair")) score += 12f;
            if (n.Contains("unlit") || n.Contains("simple")) score -= 4f;
            return Mathf.Max(0f, score);
        }

        private static float EstimateScreenCoverage(Bounds bounds, Camera cam)
        {
            if (cam == null) return 0f;
            Vector3 c = bounds.center;
            float dist = Vector3.Distance(cam.transform.position, c);
            float rad = bounds.extents.magnitude;
            float screenH = 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            return Mathf.Clamp01((rad * 2f) / Mathf.Max(0.01f, screenH));
        }

        private static Color ColorFromHash(int h)
        {
            unchecked
            {
                float hue = (h & 0xFFFF) / 65535f;
                return Color.HSVToRGB(hue, 0.65f, 0.9f);
            }
        }

        private static void EnsureMats()
        {
            if (_colorMat == null)
            {
                var sh = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Unlit/Color");
                _colorMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                _colorMat.SetInt("_ZWrite", 0);
                _colorMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                _colorMat.SetInt("_Cull", (int)CullMode.Off);
                _colorMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                _colorMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            }

            if (_overdrawMat == null)
            {
                var sh = Shader.Find("Hidden/TA/Overdraw");
                if (sh != null)
                    _overdrawMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }

            if (_mipMat == null)
            {
                var sh = Shader.Find("Hidden/TA/MipColor");
                if (sh != null)
                    _mipMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
        }

        private static void DestroyMat(ref Material m)
        {
            if (m == null) return;
            UnityEngine.Object.DestroyImmediate(m);
            m = null;
        }
    }
}
