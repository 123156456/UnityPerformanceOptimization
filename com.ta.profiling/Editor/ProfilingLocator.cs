using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TA.Profiling.Editor
{
    /// <summary>把 Profiler 样本名 / 场景对象定位到 Hierarchy 或 Project。</summary>
    public static class ProfilingLocator
    {
        private static readonly HashSet<string> NonLocatable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EditorLoop", "PlayerLoop", "GC.Alloc", "GC.Collect", "Main Thread",
            "Update.ScriptRunBehaviourUpdate",
            "LateUpdate.ScriptRunBehaviourLateUpdate",
            "FixedUpdate.ScriptRunBehaviourFixedUpdate",
            "PreLateUpdate", "PostLateUpdate", "EarlyUpdate", "Initialization"
        };

        private static readonly Dictionary<string, bool> AssetsScriptCache =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        public static void ClearAssetsScriptCache() => AssetsScriptCache.Clear();

        /// <summary>
        /// 判断 Profiler 样本是否对应工程 Assets/ 下自写脚本（不含 Packages / 引擎）。
        /// </summary>
        public static bool IsAssetsScriptSample(string sampleName)
        {
            if (string.IsNullOrWhiteSpace(sampleName)) return false;
            if (AssetsScriptCache.TryGetValue(sampleName, out bool cached))
                return cached;

            bool result = ComputeIsAssetsScript(sampleName);
            AssetsScriptCache[sampleName] = result;
            return result;
        }

        private static bool ComputeIsAssetsScript(string sampleName)
        {
            string raw = sampleName.Trim();
            if (IsClearlyEngineInternal(raw))
                return false;

            string typeHint = ExtractTypeHint(raw);
            if (string.IsNullOrEmpty(typeHint) || IsClearlyEngineInternal(typeHint))
                return false;

            if (TryFindMonoScriptInAssets(typeHint, out _))
                return true;

            // 场景里有该类型，且脚本文件在 Assets/
            if (TryFindSceneBehaviour(typeHint, out var behaviour) && behaviour != null)
            {
                var ms = MonoScript.FromMonoBehaviour(behaviour);
                if (ms != null)
                {
                    string path = AssetDatabase.GetAssetPath(ms);
                    return IsUnderAssetsFolder(path);
                }
            }

            return false;
        }

        public static bool IsClearlyEngineInternal(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (NonLocatable.Contains(name)) return true;
            if (name.StartsWith("Inl_", StringComparison.Ordinal)) return true;
            if (name.StartsWith("UIEvents.", StringComparison.Ordinal)) return true;
            if (name.StartsWith("SRPB", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("Profiler.", StringComparison.Ordinal)) return true;
            if (name.StartsWith("Render Pipeline", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.IndexOf("UnityEngine.", StringComparison.Ordinal) >= 0) return true;
            if (name.IndexOf("UnityEditor.", StringComparison.Ordinal) >= 0) return true;
            if (name.IndexOf("Unity.", StringComparison.Ordinal) >= 0) return true;
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.IndexOf(".dll!", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool IsUnderAssetsFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path = path.Replace('\\', '/');
            return path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(path, "Assets", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryLocate(Object obj, out string message)
        {
            if (obj == null)
            {
                message = "对象为空或已销毁";
                return false;
            }

            var go = obj as GameObject;
            if (go == null && obj is Component c)
                go = c.gameObject;

            if (go != null)
            {
                Selection.activeGameObject = go;
                EditorGUIUtility.PingObject(go);
                if (SceneView.lastActiveSceneView != null)
                {
                    SceneView.lastActiveSceneView.FrameSelected();
                    SceneView.lastActiveSceneView.Repaint();
                }

                message = "已定位场景对象：" + go.name;
                return true;
            }

            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
            message = "已定位资源：" + obj.name;
            return true;
        }

        /// <summary>
        /// 解析 Profiler Hierarchy 样本名，优先选中场景中的脚本实例，其次 Project 中的 MonoScript。
        /// </summary>
        public static bool TryLocateProfilerSample(string sampleName, out string message)
        {
            if (string.IsNullOrWhiteSpace(sampleName))
            {
                message = "空样本名";
                return false;
            }

            string raw = sampleName.Trim();
            if (IsClearlyEngineInternal(raw))
            {
                message = "引擎内部采样，无法定位到工程文件：" + raw;
                return false;
            }

            string typeHint = ExtractTypeHint(raw);
            if (string.IsNullOrEmpty(typeHint))
            {
                message = "无法从名称解析类型：" + raw;
                return false;
            }

            if (TryFindSceneBehaviour(typeHint, out var behaviour))
            {
                TryLocate(behaviour, out _);
                message = "场景实例 · " + behaviour.GetType().Name + " @ " + behaviour.gameObject.name;
                return true;
            }

            if (TryFindMonoScriptInAssets(typeHint, out var script) || TryFindMonoScript(typeHint, out script))
            {
                Selection.activeObject = script;
                EditorGUIUtility.PingObject(script);
                message = "已定位脚本文件：" + AssetDatabase.GetAssetPath(script);
                return true;
            }

            message = "未找到可定位对象（可能是引擎/包内部）：" + typeHint;
            return false;
        }

        public static string ExtractTypeHint(string sampleName)
        {
            string s = sampleName.Trim();

            int bang = s.IndexOf('!');
            if (bang >= 0 && bang < s.Length - 1)
                s = s.Substring(bang + 1);

            int colon = s.IndexOf(':');
            if (colon > 0)
                s = s.Substring(0, colon);

            int paren = s.IndexOf('(');
            if (paren > 0)
                s = s.Substring(0, paren);

            if (s.StartsWith("Behaviour.", StringComparison.Ordinal))
                s = s.Substring("Behaviour.".Length);

            string[] methods =
            {
                ".Update", ".LateUpdate", ".FixedUpdate", ".OnEnable", ".OnDisable",
                ".Start", ".Awake", ".OnDestroy", ".OnGUI", ".OnRenderObject"
            };
            foreach (var m in methods)
            {
                if (s.EndsWith(m, StringComparison.Ordinal))
                {
                    s = s.Substring(0, s.Length - m.Length);
                    break;
                }
            }

            return s.Trim().TrimEnd('.');
        }

        private static bool TryFindSceneBehaviour(string typeHint, out MonoBehaviour behaviour)
        {
            behaviour = null;
            string shortName = ShortName(typeHint);

            var all = Object.FindObjectsOfType<MonoBehaviour>(true);
            MonoBehaviour best = null;
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (!TypeMatches(t, typeHint, shortName)) continue;
                if (mb.isActiveAndEnabled)
                {
                    behaviour = mb;
                    return true;
                }

                if (best == null) best = mb;
            }

            behaviour = best;
            return behaviour != null;
        }

        private static bool TryFindMonoScriptInAssets(string typeHint, out MonoScript script)
        {
            script = null;
            string shortName = ShortName(typeHint);
            string[] guids = AssetDatabase.FindAssets("t:MonoScript " + shortName);
            if (guids == null || guids.Length == 0) return false;

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!IsUnderAssetsFolder(path)) continue;
                var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (ms == null) continue;
                var cls = ms.GetClass();
                if (cls == null) continue;
                if (!TypeMatches(cls, typeHint, shortName)) continue;
                script = ms;
                return true;
            }

            return false;
        }

        private static bool TryFindMonoScript(string typeHint, out MonoScript script)
        {
            script = null;
            string shortName = ShortName(typeHint);
            string[] guids = AssetDatabase.FindAssets("t:MonoScript " + shortName);
            if (guids == null || guids.Length == 0) return false;

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (ms == null) continue;
                var cls = ms.GetClass();
                if (cls == null) continue;
                if (!TypeMatches(cls, typeHint, shortName)) continue;
                script = ms;
                return true;
            }

            return false;
        }

        private static bool TypeMatches(Type t, string typeHint, string shortName)
        {
            if (t == null) return false;
            if (string.Equals(t.FullName, typeHint, StringComparison.Ordinal)) return true;
            if (string.Equals(t.Name, typeHint, StringComparison.Ordinal)) return true;
            if (string.Equals(t.Name, shortName, StringComparison.Ordinal)) return true;
            return t.FullName != null &&
                   t.FullName.EndsWith("." + shortName, StringComparison.Ordinal);
        }

        private static string ShortName(string typeHint)
        {
            if (string.IsNullOrEmpty(typeHint)) return typeHint;
            int dot = typeHint.LastIndexOf('.');
            return dot >= 0 && dot < typeHint.Length - 1
                ? typeHint.Substring(dot + 1)
                : typeHint;
        }
    }
}
