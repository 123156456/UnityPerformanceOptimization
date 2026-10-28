using UnityEditor;
using UnityEngine;

namespace TA.EditorCore
{
    /// <summary>
    /// 常用 EditorGUI 辅助绘制方法，减少各工具窗口的重复样板。
    /// </summary>
    public static class TAEditorGUI
    {
        /// <summary>绘制一条水平分隔线。</summary>
        public static void Separator(float height = 1f, float spacing = 6f)
        {
            GUILayout.Space(spacing);
            var rect = EditorGUILayout.GetControlRect(false, height);
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.15f));
            GUILayout.Space(spacing);
        }

        /// <summary>绘制一个标题栏（加粗小节标题）。</summary>
        public static void SectionTitle(string title)
        {
            GUILayout.Space(4f);
            GUILayout.Label(title, EditorStyles.boldLabel);
        }

        /// <summary>绘制一个可折叠区块，内容通过 body 回调绘制。</summary>
        public static bool FoldoutGroup(bool expanded, string title, System.Action body)
        {
            expanded = EditorGUILayout.BeginFoldoutHeaderGroup(expanded, title);
            if (expanded && body != null)
            {
                EditorGUI.indentLevel++;
                body.Invoke();
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            return expanded;
        }

        /// <summary>带提示图标的信息条。</summary>
        public static void HelpBox(string message, MessageType type = MessageType.Info)
        {
            EditorGUILayout.HelpBox(message, type);
        }

        /// <summary>一个占满宽度、带主色的执行按钮，返回是否被点击。</summary>
        public static bool PrimaryButton(string label, float height = 28f)
        {
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.30f, 0.55f, 0.95f);
            bool clicked = GUILayout.Button(label, GUILayout.Height(height));
            GUI.backgroundColor = prev;
            return clicked;
        }

        /// <summary>只读的文件夹选择字段，返回可能被修改后的路径。</summary>
        public static string FolderField(string label, string path)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(label);
            EditorGUILayout.SelectableLabel(path, EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (GUILayout.Button("…", GUILayout.Width(28f)))
            {
                var picked = EditorUtility.OpenFolderPanel(label, string.IsNullOrEmpty(path) ? Application.dataPath : path, "");
                if (!string.IsNullOrEmpty(picked)) path = picked;
            }
            EditorGUILayout.EndHorizontal();
            return path;
        }
    }
}
