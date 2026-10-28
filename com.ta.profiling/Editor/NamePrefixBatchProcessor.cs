using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TA.Profiling.Editor
{
    public enum NameMatchMode
    {
        /// <summary>名称以此前缀开头，如 P_Tree。</summary>
        Prefix = 0,
        /// <summary>名称包含该字符串。</summary>
        Contains = 1,
        /// <summary>正则匹配（如 ^P_.*）。</summary>
        Regex = 2
    }

    public sealed class BatchMatchEntry
    {
        public GameObject Target;
        public string Path;
        public int RendererCount;
        public bool IsBatchingStatic;
        public string SharedMaterialName;
    }

    public sealed class BatchProcessResult
    {
        public int Matched;
        public int Affected;
        public string Message;
    }

    /// <summary>
    /// 按命名规则筛选场景物体，并一键设置静态合批 / 执行 StaticBatchingUtility.Combine。
    /// </summary>
    public static class NamePrefixBatchProcessor
    {
        public static List<BatchMatchEntry> FindMatches(
            string pattern,
            NameMatchMode mode,
            bool includeInactive,
            bool onlyWithRenderer,
            bool matchRootsOnly)
        {
            var result = new List<BatchMatchEntry>();
            if (string.IsNullOrWhiteSpace(pattern))
                return result;

            pattern = pattern.Trim();
            Regex regex = null;
            if (mode == NameMatchMode.Regex)
            {
                try { regex = new Regex(pattern, RegexOptions.CultureInvariant); }
                catch { return result; }
            }

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
                return result;

            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                var transforms = root.GetComponentsInChildren<Transform>(includeInactive);
                foreach (var t in transforms)
                {
                    if (t == null) continue;
                    var go = t.gameObject;

                    if (matchRootsOnly && t.parent != null && Matches(t.parent.name, pattern, mode, regex))
                        continue; // 父级已匹配时只取最上层匹配节点

                    if (!Matches(go.name, pattern, mode, regex))
                        continue;

                    var renderers = go.GetComponentsInChildren<Renderer>(includeInactive);
                    if (onlyWithRenderer && (renderers == null || renderers.Length == 0))
                        continue;

                    bool batching = false;
                    try
                    {
                        var flags = GameObjectUtility.GetStaticEditorFlags(go);
                        batching = (flags & StaticEditorFlags.BatchingStatic) != 0;
                    }
                    catch { /* play mode 仍可读 flags */ }

                    string matName = "—";
                    if (renderers != null)
                    {
                        for (int i = 0; i < renderers.Length; i++)
                        {
                            if (renderers[i] == null || renderers[i].sharedMaterial == null) continue;
                            matName = renderers[i].sharedMaterial.name;
                            break;
                        }
                    }

                    result.Add(new BatchMatchEntry
                    {
                        Target = go,
                        Path = GetPath(t),
                        RendererCount = renderers != null ? renderers.Length : 0,
                        IsBatchingStatic = batching,
                        SharedMaterialName = matName
                    });
                }
            }

            result.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return result;
        }

        public static BatchProcessResult SetBatchingStatic(IList<BatchMatchEntry> entries, bool enable)
        {
            var result = new BatchProcessResult { Matched = entries?.Count ?? 0 };
            if (entries == null || entries.Count == 0)
            {
                result.Message = "没有匹配对象。";
                return result;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            int n = 0;

            foreach (var e in entries)
            {
                if (e?.Target == null) continue;
                Undo.RecordObject(e.Target, enable ? "Mark Batching Static" : "Clear Batching Static");
                var flags = GameObjectUtility.GetStaticEditorFlags(e.Target);
                if (enable)
                    flags |= StaticEditorFlags.BatchingStatic;
                else
                    flags &= ~StaticEditorFlags.BatchingStatic;
                GameObjectUtility.SetStaticEditorFlags(e.Target, flags);
                // 子物体一并标记，便于合批
                foreach (var child in e.Target.GetComponentsInChildren<Transform>(true))
                {
                    if (child == null || child.gameObject == e.Target) continue;
                    Undo.RecordObject(child.gameObject, enable ? "Mark Batching Static" : "Clear Batching Static");
                    var cf = GameObjectUtility.GetStaticEditorFlags(child.gameObject);
                    if (enable) cf |= StaticEditorFlags.BatchingStatic;
                    else cf &= ~StaticEditorFlags.BatchingStatic;
                    GameObjectUtility.SetStaticEditorFlags(child.gameObject, cf);
                }

                e.IsBatchingStatic = enable;
                n++;
            }

            Undo.CollapseUndoOperations(group);
            result.Affected = n;
            result.Message = enable
                ? $"已为 {n} 个匹配物体（含子级）开启 Batching Static。"
                : $"已为 {n} 个匹配物体清除 Batching Static。";
            return result;
        }

        /// <summary>
        /// 对匹配物体执行静态合批 Combine（运行时或编辑器均可调用；编辑器下建议先 Mark Static）。
        /// 按「共享材质」分组后分别 Combine，提高合批成功率。
        /// </summary>
        public static BatchProcessResult CombineStaticBatches(IList<BatchMatchEntry> entries)
        {
            var result = new BatchProcessResult { Matched = entries?.Count ?? 0 };
            if (entries == null || entries.Count == 0)
            {
                result.Message = "没有匹配对象。";
                return result;
            }

            // 收集带 MeshRenderer 的叶子
            var byMaterial = new Dictionary<Material, List<GameObject>>();
            int skipped = 0;

            foreach (var e in entries)
            {
                if (e?.Target == null) continue;
                var renderers = e.Target.GetComponentsInChildren<MeshRenderer>(true);
                if (renderers == null || renderers.Length == 0)
                {
                    skipped++;
                    continue;
                }

                foreach (var r in renderers)
                {
                    if (r == null || r.GetComponent<MeshFilter>() == null) continue;
                    var mat = r.sharedMaterial;
                    if (mat == null)
                    {
                        skipped++;
                        continue;
                    }

                    if (!byMaterial.TryGetValue(mat, out var list))
                    {
                        list = new List<GameObject>();
                        byMaterial[mat] = list;
                    }

                    if (!list.Contains(r.gameObject))
                        list.Add(r.gameObject);
                }
            }

            int combinedGroups = 0;
            int combinedObjects = 0;
            var root = new GameObject($"[TA] StaticBatch_{DateTime.Now:HHmmss}");
            Undo.RegisterCreatedObjectUndo(root, "Create Static Batch Root");

            foreach (var kv in byMaterial)
            {
                if (kv.Value.Count < 2) continue;
                try
                {
                    var arr = kv.Value.ToArray();
                    StaticBatchingUtility.Combine(arr, root);
                    combinedGroups++;
                    combinedObjects += arr.Length;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[TA.Profiling] Combine 失败 ({kv.Key.name}): {ex.Message}");
                }
            }

            result.Affected = combinedObjects;
            if (combinedGroups == 0)
            {
                Undo.DestroyObjectImmediate(root);
                result.Message = $"未能合批。需要 ≥2 个同材质 MeshRenderer。跳过 {skipped}。请先标记 Batching Static，且物体勿在 Play 中被移动。";
            }
            else
            {
                result.Message = $"已合批 {combinedGroups} 组、共 {combinedObjects} 个 MeshRenderer → {root.name}";
            }

            return result;
        }

        public static void SelectMatches(IList<BatchMatchEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                Selection.objects = Array.Empty<UnityEngine.Object>();
                return;
            }

            var objs = new List<UnityEngine.Object>(entries.Count);
            foreach (var e in entries)
            {
                if (e?.Target != null)
                    objs.Add(e.Target);
            }

            Selection.objects = objs.ToArray();
        }

        private static bool Matches(string name, string pattern, NameMatchMode mode, Regex regex)
        {
            if (string.IsNullOrEmpty(name)) return false;
            switch (mode)
            {
                case NameMatchMode.Contains:
                    return name.IndexOf(pattern, StringComparison.Ordinal) >= 0;
                case NameMatchMode.Regex:
                    return regex != null && regex.IsMatch(name);
                default:
                    return name.StartsWith(pattern, StringComparison.Ordinal);
            }
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
