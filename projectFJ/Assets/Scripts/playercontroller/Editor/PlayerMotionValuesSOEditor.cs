using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// PlayerMotionValuesSO 的分组折叠 Inspector：
/// - 按字段声明顺序读取每个 public 字段的 [Header] 属性，自动把字段分组（与源码【xx】板块一一对应）；
/// - 每组用 Foldout 头部可收起/展开，展开状态按资产 instanceID 存 EditorPrefs（会话内保持）；
/// - 字段本体走 SerializedObject 绘制（板块首字段因带 [Header] 会与 Foldout 标题重复，改用
///   手动“裸绘制”，仅跳过装饰器），Tooltip 悬浮提示保留；Play Mode 修改资产依旧实时生效。
/// </summary>
[CustomEditor(typeof(PlayerMotionValuesSO))]
public class PlayerMotionValuesSOEditor : Editor
{
    /// <summary>一个可折叠板块：标题 + 按声明顺序排列的字段。</summary>
    sealed class FieldGroup
    {
        public string Title;
        public readonly List<FieldInfo> Fields = new List<FieldInfo>();
    }

    static readonly BindingFlags FieldFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;

    readonly List<FieldGroup> groups = new List<FieldGroup>();

    void OnEnable()
    {
        BuildGroups();
    }

    /// <summary>
    /// 反射建组：public 实例字段按 MetadataToken（= 源码声明顺序）排序，
    /// 遇到带 [Header] 的字段就新建板块；未带 Header 的字段并入当前板块（源码约定字段都归属
    /// 上一处 Header 板块；首个字段前无 Header 时归入“其他”兜底组）。
    /// </summary>
    void BuildGroups()
    {
        groups.Clear();

        FieldInfo[] fields = typeof(PlayerMotionValuesSO)
            .GetFields(FieldFlags)
            .OrderBy(f => f.MetadataToken)
            .ToArray();

        FieldGroup current = null;
        foreach (FieldInfo field in fields)
        {
            var header = field.GetCustomAttribute<HeaderAttribute>();
            if (header != null)
            {
                current = new FieldGroup { Title = header.header };
                groups.Add(current);
            }
            else if (current == null)
            {
                current = new FieldGroup { Title = "其他" };
                groups.Add(current);
            }
            current.Fields.Add(field);
        }
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        foreach (FieldGroup group in groups)
        {
            string foldoutKey = FoldoutKey(group);
            bool open = EditorPrefs.GetBool(foldoutKey, true);
            open = EditorGUILayout.Foldout(open, group.Title, true, EditorStyles.foldoutHeader);
            EditorPrefs.SetBool(foldoutKey, open);
            if (!open) continue;

            EditorGUI.indentLevel++;
            foreach (FieldInfo field in group.Fields)
            {
                SerializedProperty property = serializedObject.FindProperty(field.Name);
                if (property == null)
                {
                    EditorGUILayout.HelpBox($"字段 {field.Name} 未找到对应的序列化属性。", MessageType.Warning);
                    continue;
                }

                // 带 [Header] 的字段是板块首字段：其标题已由上方 Foldout 承担，
                // 若再用 PropertyField 绘制，Unity 会把它身上的 [Header] 再画一遍（双标题），
                // 因此这里手动“裸绘制”字段本体，跳过 Header/装饰器。
                if (field.GetCustomAttribute<HeaderAttribute>() != null)
                {
                    DrawBareProperty(field, property);
                }
                else
                {
                    EditorGUILayout.PropertyField(property, true);
                }
            }
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2f);
        }

        serializedObject.ApplyModifiedProperties();
    }

    /// <summary>
    /// 不带任何装饰器（Header/Space 等）地绘制字段本体，保留字段名与 Tooltip 悬浮提示。
    /// 板块首字段的标题由 Foldout 承担，这里只按类型画值。
    /// </summary>
    static void DrawBareProperty(FieldInfo field, SerializedProperty property)
    {
        var tooltip = field.GetCustomAttribute<TooltipAttribute>();
        var label = new GUIContent(property.displayName,
            tooltip != null ? tooltip.tooltip : string.Empty);

        switch (property.propertyType)
        {
            case SerializedPropertyType.Float:
                property.floatValue = EditorGUILayout.FloatField(label, property.floatValue);
                break;
            case SerializedPropertyType.Integer:
                property.intValue = EditorGUILayout.IntField(label, property.intValue);
                break;
            case SerializedPropertyType.Boolean:
                property.boolValue = EditorGUILayout.Toggle(label, property.boolValue);
                break;
            case SerializedPropertyType.Vector2:
                property.vector2Value = EditorGUILayout.Vector2Field(label, property.vector2Value);
                break;
            case SerializedPropertyType.Vector3:
                property.vector3Value = EditorGUILayout.Vector3Field(label, property.vector3Value);
                break;
            default:
                // 兜底（板块首字段暂不会走到）：非基础类型仍走 PropertyField
                EditorGUILayout.PropertyField(property, label, true);
                break;
        }
    }

    /// <summary>折叠状态键：按资产实例（instanceID）+ 板块标题区分，不同资产互不干扰；会话重启后默认展开。</summary>
    string FoldoutKey(FieldGroup group)
        => $"PJ.PlayerMotionValuesSO.Foldout.{target.GetInstanceID()}.{group.Title}";
}
