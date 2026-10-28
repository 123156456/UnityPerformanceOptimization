using System.IO;
using UnityEditor;
using UnityEngine;

namespace TA.EditorCore
{
    /// <summary>
    /// 路径与资源相关的公共工具方法。
    /// </summary>
    public static class TAPathUtils
    {
        /// <summary>将绝对路径转换为以 "Assets/" 开头的工程相对路径；若不在工程内返回 null。</summary>
        public static string AbsoluteToAssetPath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return null;
            absolutePath = absolutePath.Replace('\\', '/');
            var dataPath = Application.dataPath.Replace('\\', '/');
            if (!absolutePath.StartsWith(dataPath)) return null;
            return "Assets" + absolutePath.Substring(dataPath.Length);
        }

        /// <summary>将 "Assets/..." 相对路径转换为磁盘绝对路径。</summary>
        public static string AssetToAbsolutePath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName.Replace('\\', '/');
            return projectRoot + "/" + assetPath;
        }

        /// <summary>确保目录存在（不存在则创建）。</summary>
        public static void EnsureDirectory(string absoluteDir)
        {
            if (!string.IsNullOrEmpty(absoluteDir) && !Directory.Exists(absoluteDir))
                Directory.CreateDirectory(absoluteDir);
        }

        /// <summary>在指定 Assets 文件夹下按类型查找所有资源的 GUID。</summary>
        public static string[] FindAssetGuids(string filter, string assetFolder)
        {
            if (string.IsNullOrEmpty(assetFolder))
                return AssetDatabase.FindAssets(filter);
            return AssetDatabase.FindAssets(filter, new[] { assetFolder });
        }

        /// <summary>把文件名规整为对 Web 友好的 slug（字母数字与下划线/连字符）。</summary>
        public static string Slugify(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString();
        }
    }
}
