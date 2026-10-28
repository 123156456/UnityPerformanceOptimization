using System;
using UnityEditor;
using UnityEngine;

namespace TA.EditorCore
{
    /// <summary>
    /// TA 工具集统一的 EditorWindow 基类。
    /// 提供统一标题栏、内边距、滚动区域与常用生命周期钩子，
    /// 子类只需重写 <see cref="DrawContent"/> 即可专注于内容绘制。
    /// </summary>
    public abstract class TAEditorWindow : EditorWindow
    {
        private Vector2 _scroll;

        /// <summary>窗口顶部展示的标题，默认取窗口 titleContent。</summary>
        protected virtual string HeaderTitle => titleContent != null ? titleContent.text : name;

        /// <summary>标题下方的副标题/说明，返回 null 则不显示。</summary>
        protected virtual string HeaderSubtitle => null;

        /// <summary>是否为内容区域自动包裹一个纵向滚动视图。</summary>
        protected virtual bool UseScrollView => true;

        /// <summary>内容区域四周的内边距。</summary>
        protected virtual RectOffset ContentPadding => new RectOffset(12, 12, 8, 12);

        private void OnGUI()
        {
            DrawHeader();

            var padding = ContentPadding;
            GUILayout.Space(padding.top);

            if (UseScrollView)
                _scroll = EditorGUILayout.BeginScrollView(_scroll);

            GUILayout.BeginHorizontal();
            GUILayout.Space(padding.left);
            GUILayout.BeginVertical();

            DrawContent();

            GUILayout.EndVertical();
            GUILayout.Space(padding.right);
            GUILayout.EndHorizontal();

            if (UseScrollView)
                EditorGUILayout.EndScrollView();

            GUILayout.Space(padding.bottom);
        }

        private void DrawHeader()
        {
            var rect = EditorGUILayout.GetControlRect(false, HeaderSubtitle != null ? 44f : 30f);
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.06f));

            var titleRect = new Rect(rect.x + 12f, rect.y + 6f, rect.width - 24f, 18f);
            GUI.Label(titleRect, HeaderTitle, EditorStyles.boldLabel);

            if (HeaderSubtitle != null)
            {
                var subRect = new Rect(rect.x + 12f, rect.y + 24f, rect.width - 24f, 16f);
                var style = new GUIStyle(EditorStyles.miniLabel) { wordWrap = false };
                GUI.Label(subRect, HeaderSubtitle, style);
            }
        }

        /// <summary>子类实现：绘制窗口主体内容。</summary>
        protected abstract void DrawContent();
    }
}
