using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TA.Profiling.Editor
{
    /// <summary>资源类型分类（用于汇总与过滤）。</summary>
    public enum ResourceCategory
    {
        Material,
        Texture,
        Mesh,
        Particle,
        Light,
        Script,
        Other
    }

    /// <summary>单个 GameObject 的开销估算。</summary>
    public sealed class GameObjectCostEntry
    {
        public GameObject Target;
        public string Path;
        public int ScriptCount;
        public int RendererCount;
        public int MaterialCount;
        public int TextureCount;
        public long TextureBytes;
        public int MeshCount;
        public int VertexCount;
        public int TriangleCount;
        public int EstimatedDrawCalls;
        public int ParticleSystems;
        public int ParticleCount;
        public int LightCount;
        public bool CastsShadows;
        public float CostScore;

        public string PrimaryCategoryLabel;
    }

    /// <summary>按资源类型汇总的场景开销。</summary>
    public sealed class CategoryCostSummary
    {
        public ResourceCategory Category;
        public string Label;
        public int ObjectCount;
        public int ItemCount;
        public long Bytes;
        public int ExtraInt;
        public string Detail;
        public float CostScore;
    }

    /// <summary>一次场景扫描的完整结果。</summary>
    public sealed class SceneResourceCostReport
    {
        public readonly List<GameObjectCostEntry> Objects = new List<GameObjectCostEntry>();
        public readonly List<CategoryCostSummary> Categories = new List<CategoryCostSummary>();
        public int SceneDrawCalls;
        public int SceneBatches;
        public int SceneSetPassCalls;
        public int SceneTriangles;
        public int SceneVertices;
        public bool HasUnityStats;
        public double ScannedAtEditorTime;
    }

    /// <summary>
    /// 扫描当前已加载场景中的 GameObject，按材质/贴图/网格/粒子/灯光/脚本等分类估算开销。
    /// 脚本执行时间为组件数量加权估算（非 Profiler 精确采样）；DrawCall 为 Renderer/材质/子网格估算，并读取 UnityStats 场景级数据。
    /// </summary>
    public static class SceneResourceCostAnalyzer
    {
        public static SceneResourceCostReport Scan(bool includeInactive = true)
        {
            var report = new SceneResourceCostReport
            {
                ScannedAtEditorTime = EditorApplication.timeSinceStartup
            };

            TryFillUnityStats(report);

            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            var materials = new HashSet<Material>();
            var textures = new HashSet<Texture>();
            var meshes = new HashSet<Mesh>();
            int totalParticles = 0;
            int particleSystems = 0;
            int lights = 0;
            int scripts = 0;
            int estimatedDrawCalls = 0;
            long textureBytes = 0;
            int totalVerts = 0;
            int totalTris = 0;

            foreach (var root in roots)
            {
                var transforms = root.GetComponentsInChildren<Transform>(includeInactive);
                foreach (var t in transforms)
                {
                    if (t == null) continue;
                    var go = t.gameObject;
                    var entry = AnalyzeObject(go, materials, textures, meshes);
                    if (entry == null)
                        continue;

                    report.Objects.Add(entry);
                    scripts += entry.ScriptCount;
                    particleSystems += entry.ParticleSystems;
                    totalParticles += entry.ParticleCount;
                    lights += entry.LightCount;
                    estimatedDrawCalls += entry.EstimatedDrawCalls;
                    textureBytes += entry.TextureBytes;
                    totalVerts += entry.VertexCount;
                    totalTris += entry.TriangleCount;
                }
            }

            report.Objects.Sort((a, b) => b.CostScore.CompareTo(a.CostScore));

            report.Categories.Add(MakeCategory(ResourceCategory.Material, "材质 Material",
                CountObjectsWith(report.Objects, e => e.MaterialCount > 0), materials.Count, 0,
                $"引用材质 {materials.Count}，估算 DrawCall {estimatedDrawCalls}",
                materials.Count * 10f + estimatedDrawCalls * 5f));

            report.Categories.Add(MakeCategory(ResourceCategory.Texture, "贴图 Texture",
                CountObjectsWith(report.Objects, e => e.TextureCount > 0), textures.Count, textureBytes,
                $"{textures.Count} 张，约 {FormatBytes(textureBytes)}",
                textureBytes / (512f * 1024f) + textures.Count * 2f));

            report.Categories.Add(MakeCategory(ResourceCategory.Mesh, "网格 Mesh",
                CountObjectsWith(report.Objects, e => e.MeshCount > 0), meshes.Count, 0,
                $"{meshes.Count} 网格，{totalVerts:N0} 顶点 / {totalTris:N0} 三角",
                totalTris / 5000f + meshes.Count));

            report.Categories.Add(MakeCategory(ResourceCategory.Particle, "粒子 Particle",
                CountObjectsWith(report.Objects, e => e.ParticleSystems > 0), particleSystems, 0,
                $"{particleSystems} 系统，当前粒子约 {totalParticles}",
                particleSystems * 15f + totalParticles / 50f));

            report.Categories.Add(MakeCategory(ResourceCategory.Light, "灯光 Light",
                CountObjectsWith(report.Objects, e => e.LightCount > 0), lights, 0,
                $"{lights} 盏灯",
                lights * 20f));

            report.Categories.Add(MakeCategory(ResourceCategory.Script, "脚本 Script",
                CountObjectsWith(report.Objects, e => e.ScriptCount > 0), scripts, 0,
                $"{scripts} 个 MonoBehaviour（执行时间需结合 Profiler）",
                scripts * 3f));

            report.Categories.Sort((a, b) => b.CostScore.CompareTo(a.CostScore));
            return report;
        }

        private static GameObjectCostEntry AnalyzeObject(
            GameObject go,
            HashSet<Material> allMaterials,
            HashSet<Texture> allTextures,
            HashSet<Mesh> allMeshes)
        {
            var behaviours = go.GetComponents<MonoBehaviour>();
            var renderers = go.GetComponents<Renderer>();
            var lights = go.GetComponents<Light>();
            var particles = go.GetComponents<ParticleSystem>();
            var filters = go.GetComponents<MeshFilter>();
            var skins = go.GetComponents<SkinnedMeshRenderer>();

            int scriptCount = 0;
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] != null)
                    scriptCount++;
            }

            int materialCount = 0;
            int textureCount = 0;
            long textureBytes = 0;
            int meshCount = 0;
            int verts = 0;
            int tris = 0;
            int drawCalls = 0;
            bool shadows = false;

            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (r.shadowCastingMode != ShadowCastingMode.Off)
                    shadows = true;

                var mats = r.sharedMaterials;
                int subMeshGuess = 1;
                materialCount += mats != null ? mats.Length : 0;

                if (r is MeshRenderer && r.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
                {
                    var mesh = mf.sharedMesh;
                    allMeshes.Add(mesh);
                    meshCount++;
                    verts += mesh.vertexCount;
                    tris += CountTriangles(mesh);
                    subMeshGuess = Mathf.Max(1, mesh.subMeshCount);
                }
                else if (r is SkinnedMeshRenderer sk && sk.sharedMesh != null)
                {
                    var mesh = sk.sharedMesh;
                    allMeshes.Add(mesh);
                    meshCount++;
                    verts += mesh.vertexCount;
                    tris += CountTriangles(mesh);
                    subMeshGuess = Mathf.Max(1, mesh.subMeshCount);
                }
                else if (r is ParticleSystemRenderer)
                {
                    subMeshGuess = Mathf.Max(1, mats != null ? mats.Length : 1);
                }

                if (mats != null)
                {
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var mat = mats[i];
                        if (mat == null) continue;
                        allMaterials.Add(mat);
                        CollectTextures(mat, allTextures, ref textureCount, ref textureBytes);
                    }
                }

                // 粗略：每个材质 pass ≈ 1 SetPass；按材质数与子网格估算 DrawCall
                int matSlots = mats != null ? Mathf.Max(1, mats.Length) : 1;
                drawCalls += Mathf.Max(matSlots, subMeshGuess);
            }

            // MeshFilter 无 Renderer 的情况极少，仍计入网格
            foreach (var mf in filters)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (go.GetComponent<MeshRenderer>() != null) continue;
                allMeshes.Add(mf.sharedMesh);
                meshCount++;
                verts += mf.sharedMesh.vertexCount;
                tris += CountTriangles(mf.sharedMesh);
            }

            foreach (var sk in skins)
            {
                // 已在 renderers 循环处理
                _ = sk;
            }

            int particleCount = 0;
            foreach (var ps in particles)
            {
                if (ps == null) continue;
                particleCount += ps.particleCount;
            }

            bool hasCost = scriptCount > 0 || renderers.Length > 0 || lights.Length > 0 || particles.Length > 0 || meshCount > 0;
            if (!hasCost)
                return null;

            float score =
                scriptCount * 3f +
                drawCalls * 8f +
                tris / 2000f +
                textureBytes / (1024f * 1024f) * 4f +
                particles.Length * 12f + particleCount / 40f +
                lights.Length * 18f +
                (shadows ? 10f : 0f);

            string primary =
                drawCalls > 0 || materialCount > 0 ? "渲染" :
                particles.Length > 0 ? "粒子" :
                lights.Length > 0 ? "灯光" :
                scriptCount > 0 ? "脚本" : "其他";

            return new GameObjectCostEntry
            {
                Target = go,
                Path = GetHierarchyPath(go.transform),
                ScriptCount = scriptCount,
                RendererCount = renderers.Length,
                MaterialCount = materialCount,
                TextureCount = textureCount,
                TextureBytes = textureBytes,
                MeshCount = meshCount,
                VertexCount = verts,
                TriangleCount = tris,
                EstimatedDrawCalls = drawCalls,
                ParticleSystems = particles.Length,
                ParticleCount = particleCount,
                LightCount = lights.Length,
                CastsShadows = shadows,
                CostScore = score,
                PrimaryCategoryLabel = primary
            };
        }

        private static void CollectTextures(Material mat, HashSet<Texture> all, ref int count, ref long bytes)
        {
            if (mat == null) return;
            var shader = mat.shader;
            if (shader == null) return;

            int propCount = shader.GetPropertyCount();
            for (int i = 0; i < propCount; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture)
                    continue;
                string name = shader.GetPropertyName(i);
                var tex = mat.GetTexture(name);
                if (tex == null || !all.Add(tex))
                    continue;
                count++;
                bytes += EstimateTextureBytes(tex);
            }
        }

        private static long EstimateTextureBytes(Texture tex)
        {
            if (tex == null) return 0;
            int w = tex.width;
            int h = tex.height;
            int mips = tex.mipmapCount > 0 ? tex.mipmapCount : 1;
            // 粗估：RGBA32 4 字节；压缩格式按 1 字节/像素近似
            float bpp = 2f;
            if (tex is Texture2D t2d)
            {
                var fmt = t2d.format;
                bpp = IsCompressed(fmt) ? 1f : 4f;
            }

            double total = 0;
            int cw = w, ch = h;
            for (int m = 0; m < mips; m++)
            {
                total += cw * (double)ch * bpp;
                cw = Mathf.Max(1, cw / 2);
                ch = Mathf.Max(1, ch / 2);
            }

            return (long)total;
        }

        private static bool IsCompressed(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT5:
                case TextureFormat.BC4:
                case TextureFormat.BC5:
                case TextureFormat.BC6H:
                case TextureFormat.BC7:
                case TextureFormat.ETC_RGB4:
                case TextureFormat.ETC2_RGB:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ASTC_4x4:
                case TextureFormat.ASTC_5x5:
                case TextureFormat.ASTC_6x6:
                case TextureFormat.ASTC_8x8:
                case TextureFormat.ASTC_10x10:
                case TextureFormat.ASTC_12x12:
                    return true;
                default:
                    return false;
            }
        }

        private static int CountTriangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            int sum = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                sum += (int)mesh.GetIndexCount(i) / 3;
            return sum;
        }

        private static void TryFillUnityStats(SceneResourceCostReport report)
        {
#if UNITY_EDITOR
            try
            {
                // UnityStats 仅在 Game 视图渲染后有意义
                report.SceneDrawCalls = UnityStats.drawCalls;
                report.SceneBatches = UnityStats.batches;
                report.SceneSetPassCalls = UnityStats.setPassCalls;
                report.SceneTriangles = UnityStats.triangles;
                report.SceneVertices = UnityStats.vertices;
                report.HasUnityStats = true;
            }
            catch
            {
                report.HasUnityStats = false;
            }
#endif
        }

        private static CategoryCostSummary MakeCategory(
            ResourceCategory category, string label, int objectCount, int itemCount, long bytes, string detail, float score)
        {
            return new CategoryCostSummary
            {
                Category = category,
                Label = label,
                ObjectCount = objectCount,
                ItemCount = itemCount,
                Bytes = bytes,
                Detail = detail,
                CostScore = score
            };
        }

        private static int CountObjectsWith(List<GameObjectCostEntry> list, Func<GameObjectCostEntry, bool> pred)
        {
            int n = 0;
            for (int i = 0; i < list.Count; i++)
                if (pred(list[i])) n++;
            return n;
        }

        private static string GetHierarchyPath(Transform t)
        {
            if (t == null) return string.Empty;
            var stack = new Stack<string>();
            while (t != null)
            {
                stack.Push(t.name);
                t = t.parent;
            }

            return string.Join("/", stack);
        }

        public static string FormatBytes(long bytes)
        {
            const double kb = 1024d;
            const double mb = kb * 1024d;
            if (bytes >= mb) return $"{bytes / mb:F1} MB";
            if (bytes >= kb) return $"{bytes / kb:F1} KB";
            return $"{bytes} B";
        }
    }
}
