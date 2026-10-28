using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace TA.Profiling.Editor
{
    public sealed class LightImpactEntry
    {
        public Light Light;
        public string Name;
        public LightType Type;
        public LightShadows Shadows;
        public float Intensity;
        public bool Realtime;
        public float Score;
        public string Impact;
    }

    public sealed class RenderImpactReport
    {
        public readonly List<LightImpactEntry> Lights = new List<LightImpactEntry>();
        public readonly List<ProbeImpactEntry> Probes = new List<ProbeImpactEntry>();
        public readonly List<VolumeImpactEntry> Volumes = new List<VolumeImpactEntry>();
        public int RealtimeLights;
        public int ShadowCastingLights;
        public int BakedOrMixed;
        public int ReflectionProbes;
        public int ActiveReflectionProbes;
        public int PostVolumes;
        public int ActivePostVolumes;
        public readonly List<string> PostEffects = new List<string>();
        public float LightingScore;
        public float ShadowScore;
        public float PostScore;
        public string Summary;
    }

    public sealed class ProbeImpactEntry
    {
        public ReflectionProbe Probe;
        public string Name;
        public bool Active;
    }

    public sealed class VolumeImpactEntry
    {
        public MonoBehaviour Volume;
        public string Name;
        public bool Active;
        public string Effects;
    }

    /// <summary>
    /// 分析场景光源、阴影、反射探针与后处理对性能的影响。
    /// </summary>
    public static class RenderImpactAnalyzer
    {
        public static RenderImpactReport Scan()
        {
            var report = new RenderImpactReport();

            var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
            foreach (var light in lights)
            {
                if (light == null) continue;
                bool realtime = light.lightmapBakeType == LightmapBakeType.Realtime
                                || light.lightmapBakeType == LightmapBakeType.Mixed;
                bool shadow = light.shadows != LightShadows.None && light.enabled;
                if (realtime && light.enabled) report.RealtimeLights++;
                if (shadow) report.ShadowCastingLights++;
                if (light.lightmapBakeType != LightmapBakeType.Realtime) report.BakedOrMixed++;

                float score = 0f;
                if (!light.enabled) continue;

                score += light.type == LightType.Directional ? 25f
                    : light.type == LightType.Point ? 18f
                    : light.type == LightType.Spot ? 15f : 8f;
                if (realtime) score += 20f;
                if (shadow)
                    score += light.shadows == LightShadows.Soft ? 35f : 22f;
                score += Mathf.Clamp(light.intensity, 0f, 8f) * 2f;

                string impact =
                    (!realtime ? "Baked " : "Realtime ") +
                    light.type +
                    (shadow ? (light.shadows == LightShadows.Soft ? " SoftShadow" : " HardShadow") : " NoShadow");

                report.Lights.Add(new LightImpactEntry
                {
                    Light = light,
                    Name = light.gameObject.name,
                    Type = light.type,
                    Shadows = light.shadows,
                    Intensity = light.intensity,
                    Realtime = realtime,
                    Score = score,
                    Impact = impact
                });
                report.LightingScore += realtime ? score : score * 0.15f;
                if (shadow) report.ShadowScore += score;
            }

            report.Lights.Sort((a, b) => b.Score.CompareTo(a.Score));

            var probes = UnityEngine.Object.FindObjectsOfType<ReflectionProbe>(true);
            report.ReflectionProbes = probes.Length;
            foreach (var p in probes)
            {
                if (p == null) continue;
                bool active = p.enabled && p.gameObject.activeInHierarchy;
                if (active) report.ActiveReflectionProbes++;
                report.Probes.Add(new ProbeImpactEntry
                {
                    Probe = p,
                    Name = p.gameObject.name,
                    Active = active
                });
            }

            ScanPostProcessing(report);

            report.Summary =
                $"实时灯 {report.RealtimeLights} · 投影灯 {report.ShadowCastingLights} · " +
                $"反射探针 {report.ActiveReflectionProbes}/{report.ReflectionProbes} · " +
                $"后处理 Volume {report.ActivePostVolumes}/{report.PostVolumes}";

            return report;
        }

        private static void ScanPostProcessing(RenderImpactReport report)
        {
            // 不硬依赖 URP/HDRP/PostProcessing 包：按类型名反射扫描
            var behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true);
            var effectSet = new HashSet<string>();

            foreach (var mb in behaviours)
            {
                if (mb == null) continue;
                string typeName = mb.GetType().Name;
                string full = mb.GetType().FullName ?? typeName;

                bool isVolume = typeName == "Volume"
                                || typeName == "PostProcessVolume"
                                || full.IndexOf("PostProcessVolume", StringComparison.Ordinal) >= 0;
                if (!isVolume) continue;

                report.PostVolumes++;
                bool active = mb.isActiveAndEnabled;
                if (active) report.ActivePostVolumes++;

                float score = active ? 20f : 4f;
                var localEffects = new HashSet<string>();
                TryCollectVolumeEffects(mb, localEffects, ref score, active);
                foreach (var e in localEffects)
                    effectSet.Add(e);

                report.Volumes.Add(new VolumeImpactEntry
                {
                    Volume = mb,
                    Name = mb.gameObject.name,
                    Active = active,
                    Effects = localEffects.Count > 0 ? string.Join(", ", localEffects) : typeName
                });

                if (active) report.PostScore += score;
            }

            foreach (var e in effectSet)
                report.PostEffects.Add(e);
            report.PostEffects.Sort();
        }

        private static void TryCollectVolumeEffects(MonoBehaviour volume, HashSet<string> effects, ref float score, bool active)
        {
            var type = volume.GetType();
            var profileProp = type.GetProperty("profile", BindingFlags.Instance | BindingFlags.Public)
                             ?? type.GetProperty("sharedProfile", BindingFlags.Instance | BindingFlags.Public);
            object profile = profileProp?.GetValue(volume);
            if (profile == null)
            {
                var field = type.GetField("sharedProfile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? type.GetField("profile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                profile = field?.GetValue(volume);
            }

            if (profile == null) return;

            var componentsProp = profile.GetType().GetProperty("components", BindingFlags.Instance | BindingFlags.Public);
            var list = componentsProp?.GetValue(profile) as System.Collections.IEnumerable;
            if (list == null)
            {
                var settingsField = profile.GetType().GetField("settings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                list = settingsField?.GetValue(profile) as System.Collections.IEnumerable;
            }

            if (list == null) return;

            foreach (var comp in list)
            {
                if (comp == null) continue;
                string n = comp.GetType().Name;
                // active override?
                bool on = true;
                var activeField = comp.GetType().GetProperty("active", BindingFlags.Instance | BindingFlags.Public);
                if (activeField != null && activeField.PropertyType == typeof(bool))
                    on = (bool)activeField.GetValue(comp);

                if (!on) continue;
                effects.Add(n);
                if (active)
                {
                    score += CostOfEffect(n);
                }
            }
        }

        private static float CostOfEffect(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("bloom")) return 12f;
            if (n.Contains("ambientocclusion") || n.Contains("ssao")) return 18f;
            if (n.Contains("motionblur")) return 14f;
            if (n.Contains("depthoffield") || n.Contains("dof")) return 16f;
            if (n.Contains("shadow") && n.Contains("screen")) return 20f;
            if (n.Contains("reflection")) return 15f;
            if (n.Contains("tonemapping") || n.Contains("coloradjust")) return 4f;
            if (n.Contains("vignette") || n.Contains("filmgrain")) return 3f;
            return 8f;
        }
    }
}
