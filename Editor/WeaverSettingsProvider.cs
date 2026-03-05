using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace ILForge.Editor
{
    public static class WeaverSettingsProvider
    {
        private static ReorderableList _assemblyList;
        private static GUIContent _headerIconContent;

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            var provider = new SettingsProvider("Project/IL Forge", SettingsScope.Project)
            {
                keywords = new[] { "IL", "Forge", "Optimization", "Weaver", "Wired" },
                guiHandler = (_) =>
                {
                    var config = WeaverSettings.instance;
                    if (config == null) return;

                    var serializedObject = new SerializedObject(config);
                    serializedObject.Update();

                    SetupAssemblyList(serializedObject);

                    if (_headerIconContent == null)
                    {
                        _headerIconContent = EditorGUIUtility.IconContent("SettingsIcon");
                    }

                    EditorGUILayout.BeginVertical(new GUIStyle { padding = new RectOffset(5, 5, 5, 5) });

                    EditorGUI.BeginChangeCheck();

                    DrawProfessionalGUI(serializedObject);

                    if (EditorGUI.EndChangeCheck())
                    {
                        serializedObject.ApplyModifiedProperties();
                        config.SaveData();
                    }

                    EditorGUILayout.EndVertical();
                }
            };
            return provider;
        }

        private static void DrawProfessionalGUI(SerializedObject serializedObject)
        {
            var enableProp = serializedObject.FindProperty("Enabled");

            DrawMasterToggle(enableProp);
            if (!enableProp.boolValue) return;

            DrawAssembliesListBlock();

            GUILayout.FlexibleSpace();
            DrawFooterBlock();
        }

        private static void DrawAssembliesListBlock()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (_headerIconContent != null && _headerIconContent.image != null)
                {
                    GUILayout.Label(_headerIconContent.image, GUILayout.Width(20), GUILayout.Height(20));
                    GUILayout.Space(2);
                }

                EditorGUILayout.LabelField("Assembly Configuration", EditorStyles.boldLabel, GUILayout.Height(20));
            }

            GUILayout.Space(3);
            _assemblyList.DoLayoutList();
        }

        private static void DrawFooterBlock()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Tip: ILForge injects code after `base.ctor()` for Classes and before `base.ctor()` for MonoBehaviours (inside Awake).",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        private static void SetupAssemblyList(SerializedObject serializedObject)
        {
            if (_assemblyList != null) return;

            var assembliesProp = serializedObject.FindProperty("Assemblies");
            _assemblyList = new ReorderableList(serializedObject, assembliesProp, true, false, true, true);

            _assemblyList.drawHeaderCallback = (rect) => { EditorGUI.LabelField(rect, "Target Assemblies (DLL Names without extension)"); };

            _assemblyList.drawElementCallback = (rect, index, _, _) =>
            {
                var element = _assemblyList.serializedProperty.GetArrayElementAtIndex(index);
                rect.y += 2;

                EditorGUI.PropertyField(new Rect(rect.x, rect.y, rect.width, EditorGUIUtility.singleLineHeight), element, GUIContent.none);
            };

            _assemblyList.elementHeight = EditorGUIUtility.singleLineHeight + 4;
        }

        public static bool DrawMasterToggle(SerializedProperty enableProp)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.Space(5);
                enableProp.boolValue = DrawSwitchToggle(enableProp.boolValue);
                GUILayout.FlexibleSpace();
            }

            GUILayout.Space(15);
            return enableProp.boolValue;
        }

        public static bool DrawSwitchToggle(bool value)
        {
            var rect = GUILayoutUtility.GetRect(50, 24);
            var e = Event.current;

            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                value = !value;
                GUI.changed = true;
                e.Use();
            }

            if (e.type != EventType.Repaint) return value;
            var bgColor = value ? new Color(0.2f, 0.84f, 0.29f) : new Color(0.45f, 0.45f, 0.45f);

            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, bgColor, 0, rect.height / 2f);

            const float padding = 2f;
            var knobSize = rect.height - padding * 2f;

            var knobX = value ? (rect.x + rect.width - knobSize - padding) : (rect.x + padding);
            var knobRect = new Rect(knobX, rect.y + padding, knobSize, knobSize);

            var shadowRect = new Rect(knobRect.x, knobRect.y + 1.5f, knobSize, knobSize);
            GUI.DrawTexture(shadowRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(0f, 0f, 0f, 0.35f), 0, knobSize / 2f);
            GUI.DrawTexture(knobRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, Color.white, 0, knobSize / 2f);

            return value;
        }
    }
}