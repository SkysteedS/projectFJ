using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 小工具：给定模型(Prefab) + 需要修改的动画(AnimationClip)，
/// 采集该动画在指定帧下的【各 humanoid 骨骼信息】与【肌肉值】。
/// 用法A(肌肉值)：直接从剪辑曲线读取，始终可靠。
/// 用法B(骨骼姿势)：CreateAnimatorControllerAtPath 建资产控制器 → 装剪辑 → 播放(两次Update)。
/// 输出到 Temp/anim_bone_info.txt 并打开所在目录。
/// 菜单：Tools/Animate Compute/骨骼信息采集
/// </summary>
public class AnimComputeWindow : EditorWindow
{
    // Editor 窗口外观常量
    const float MinWindowWidth = 460f;
    const float MinWindowHeight = 240f;
    const float CollectButtonHeight = 28f;

    public GameObject prefab;
    public AnimationClip clip;
    public float time;

    [MenuItem("Tools/Animate Compute/骨骼信息采集")]
    public static void Open()
    {
        var w = GetWindow<AnimComputeWindow>("Anim Compute");
        w.minSize = new Vector2(MinWindowWidth, MinWindowHeight);
    }

    private void OnGUI()
    {
        GUILayout.Label("给定模型与动画，采集各骨骼信息 + 肌肉值", EditorStyles.boldLabel);
        prefab = (GameObject)EditorGUILayout.ObjectField("模型 (Prefab)", prefab, typeof(GameObject), false);
        clip = (AnimationClip)EditorGUILayout.ObjectField("动画 (Anim)", clip, typeof(AnimationClip), false);
        time = EditorGUILayout.FloatField("采样时间 (秒)", time);

        if (GUILayout.Button("采集骨骼信息并保存", GUILayout.Height(CollectButtonHeight)))
            Collect();
        EditorGUILayout.HelpBox(
            "要求：模型为 Humanoid，动画应为 humanoid .anim。\n" +
            "输出：剪辑肌肉值（直接读取）+ 应用后骨骼信息 + 回读肌肉值（交叉验证）。",
            MessageType.Info);
    }

    private void Collect()
    {
        if (prefab == null) { Debug.LogError("请先选择模型 Prefab"); return; }
        if (clip == null) { Debug.LogError("请先选择 AnimationClip"); return; }

        var sb = new StringBuilder();
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        try
        {
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            foreach (var cc in go.GetComponentsInChildren<CharacterController>(true)) cc.enabled = false;
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetType().Namespace != null && mb.GetType().Namespace.Contains("Magica"))
                    mb.enabled = false;

            var animator = go.GetComponentInChildren<Animator>(true);
            if (animator == null) { Debug.LogError("模型上没有 Animator"); return; }
            if (animator.avatar == null || !animator.avatar.isHuman) { Debug.LogError("该模型不是 Humanoid（无 humanoid avatar）"); return; }
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            int mc = HumanTrait.MuscleCount;

            // —— A) 剪辑肌肉值：直接读曲线（始终可靠） ——
            sb.AppendLine("===== A. 剪辑肌肉值（直接读取，time=" + time.ToString("F3") + "s）=====");
            var clipValues = new float[mc];
            for (int i = 0; i < mc; i++)
            {
                var c = AnimationUtility.GetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve("", typeof(Animator), HumanTrait.MuscleName[i]));
                clipValues[i] = c != null ? c.Evaluate(time) : 0f;
            }
            sb.AppendFormat("bodyPos(RootT)=({0:F4},{1:F4},{2:F4})  RootQ=({3:F4},{4:F4},{5:F4},{6:F4})\n",
                Eval(clip, "RootT.x", time), Eval(clip, "RootT.y", time), Eval(clip, "RootT.z", time),
                Eval(clip, "RootQ.x", time), Eval(clip, "RootQ.y", time), Eval(clip, "RootQ.z", time), Eval(clip, "RootQ.w", time));
            for (int i = 0; i < mc; i++)
                sb.AppendFormat("muscles[{0,2}] {1,-32} = {2:F4}\n", i, HumanTrait.MuscleName[i], clipValues[i]);

            // —— B) 用 HumanPoseHandler.SetHumanPose 直接把剪辑姿势写到骨骼（不依赖 Animator 评估） ——
            animator.enabled = true;
            animator.Rebind();                       // 让骨架处于“可写”初始态
            var handler = new HumanPoseHandler(animator.avatar, animator.transform); // root = 拥有骨骼的 Transform
            var pose = new HumanPose();
            handler.GetHumanPose(ref pose);          // 以当前(bind)姿态为基底
            for (int i = 0; i < mc; i++) pose.muscles[i] = clipValues[i];
            pose.bodyRotation = new Quaternion(Eval(clip, "RootQ.x", time), Eval(clip, "RootQ.y", time),
                                               Eval(clip, "RootQ.z", time), Eval(clip, "RootQ.w", time));
            pose.bodyPosition = new Vector3(Eval(clip, "RootT.x", time), Eval(clip, "RootT.y", time), Eval(clip, "RootT.z", time));
            handler.SetHumanPose(ref pose);          // 应用到骨骼

            // —— C) 骨骼信息（应用后的实际骨骼） ——
            sb.AppendLine();
            sb.AppendLine("===== B. 应用后骨骼信息 =====");
            var names = Enum.GetNames(typeof(HumanBodyBones));
            for (int i = 0; i < names.Length; i++)
            {
                if ((HumanBodyBones)i == HumanBodyBones.LastBone) continue;
                var t = animator.GetBoneTransform((HumanBodyBones)i);
                if (t == null) continue;
                sb.AppendFormat("{0,-22} parent={1,-22} localPos={2}  localRotEul={3}\n",
                    names[i], t.parent != null ? t.parent.name : "-",
                    t.localPosition.ToString("F4"), t.localRotation.eulerAngles.ToString("F1"));
                sb.AppendFormat("    worldPos={0}  worldRotEul={1}\n",
                    t.position.ToString("F4"), t.rotation.eulerAngles.ToString("F1"));
            }

            // —— D) 回读肌肉值（与 A 对比，验证姿势是否应用成功） ——
            sb.AppendLine();
            sb.AppendLine("===== C. 应用后回读(GetHumanPose)，应与 A 一致 =====");
            var poseBack = new HumanPose();
            handler.GetHumanPose(ref poseBack);
            for (int i = 0; i < Mathf.Min(mc, poseBack.muscles.Length); i++)
                sb.AppendFormat("muscles[{0,2}] {1,-32} = {2:F4}   (A={3:F4})\n",
                    i, HumanTrait.MuscleName[i], poseBack.muscles[i], clipValues[i]);
        }
        catch (Exception e)
        {
            Debug.LogError("采集失败: " + e);
            return;
        }
        finally
        {
            if (go != null) DestroyImmediate(go);
        }

        var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Temp"));
        Directory.CreateDirectory(dir);
        var outFile = Path.Combine(dir, "anim_bone_info.txt");
        File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        Debug.Log("已保存骨骼信息: " + outFile);
        EditorUtility.RevealInFinder(outFile);
    }

    private static float Eval(AnimationClip clip, string muscle, float t)
    {
        var c = AnimationUtility.GetEditorCurve(clip,
            EditorCurveBinding.FloatCurve("", typeof(Animator), muscle));
        return c != null ? c.Evaluate(t) : 0f;
    }
}
