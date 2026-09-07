using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class PosePreview
{
    private const string BaseClipAssetPath = "Assets/animate/used animation/unweapon/locomotion/idle.anim";
    private const string ClipAssetPath = "Assets/animate/used animation/unweapon/locomotion/idle_climb.anim";
    private const string PrefabAssetPath = "Assets/prefabs/firefly.prefab";
    private static readonly string RootDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    private static readonly string OffsetsPath = Path.Combine(RootDir, "Temp", "pose_offsets.txt");
    private static readonly string OutDir = Path.Combine(RootDir, "Temp", "PosePreview");

    // —— 渲染/相机预览常量（编辑器调试工具专用，语义见命名）——
    private const float MinClipDuration = 0.001f;         // clip 时长下限（防除零）
    private const float FallbackClipEndTime = 8.333334f;  // 无时长 clip 的兜底结束时间（s）
    private const float KeyLightIntensity = 1.4f;         // 主光强度
    private static readonly Color KeyLightColor = new Color(1f, 0.95f, 0.88f);      // 主光颜色（暖色）
    private static readonly Quaternion KeyLightRotation = Quaternion.Euler(45f, -35f, 0f); // 主光方向
    private const float FillLightIntensity = 0.5f;        // 辅光强度
    private static readonly Color FillLightColor = new Color(0.8f, 0.85f, 1f);      // 辅光颜色（冷色）
    private static readonly Quaternion FillLightRotation = Quaternion.Euler(20f, 140f, 0f); // 辅光方向
    private static readonly Color BackgroundColor = new Color(0.22f, 0.22f, 0.24f); // 预览背景色
    private const float PreviewFov = 40f;                 // 预览相机 FOV（°）
    private const float PreviewNearPlane = 0.05f;         // 预览相机近裁剪面
    private const float PreviewFarPlane = 100f;           // 预览相机远裁剪面
    private const int RenderTextureSize = 1024;           // 渲染目标分辨率（像素）
    private const int RenderTextureDepth = 24;            // 渲染目标深度位
    private const float DefaultBoundsExtent = 2f;         // 无渲染器时包围盒默认边长（m）

    // —— 各视角的相机方向（相对包围盒中心）与距离系数 ——
    private static readonly Vector3 FrontViewDir = new Vector3(0f, 0.10f, -1f);        // 正面（略抬）
    private static readonly Vector3 BackViewDir = new Vector3(0f, 0.10f, 1f);          // 背面（略抬）
    private static readonly Vector3 SideViewDir = new Vector3(-1f, 0.08f, 0.10f);      // 侧面
    private static readonly Vector3 ThreeQuarterViewDir = new Vector3(0.7f, 0.35f, -1f); // ¾ 视角
    private const float StandardViewDistScale = 1.15f;    // 正/背/侧视角距离 = 包围盒尺寸 × 该系数
    private const float ThreeQuarterViewDistScale = 1.25f; // ¾ 视角距离系数（略远，容纳对角）

    public static void Capture()
    {
        Directory.CreateDirectory(OutDir);
        try
        {
            var offsets = LoadOffsets(OffsetsPath);

            var baseClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipAssetPath);
            if (baseClip == null)
                throw new Exception("Clip not found: " + ClipAssetPath);

            var original = UnityEngine.Object.Instantiate(
                AssetDatabase.LoadAssetAtPath<AnimationClip>(BaseClipAssetPath));
            if (original == null)
                throw new Exception("Clip not found: " + BaseClipAssetPath);

            var modified = UnityEngine.Object.Instantiate(baseClip);
            modified.name = "modified";
            ApplyOffsets(modified, offsets);

            RenderVariant("orig", original);
            RenderVariant("mod", modified);
            File.WriteAllText(Path.Combine(OutDir, "done.txt"), "OK base=" + BaseClipAssetPath + " mod=" + ClipAssetPath + " offsets=" + offsets.Count);
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(OutDir, "error.txt"), e.ToString());
        }
        finally
        {
            EditorApplication.Exit(0);
        }
    }

    private static Dictionary<string, float> LoadOffsets(string path)
    {
        var result = new Dictionary<string, float>();
        if (!File.Exists(path)) return result;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var parts = line.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;
            float v;
            if (float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                result[parts[0].Trim()] = v;
        }
        return result;
    }

    private static void ApplyOffsets(AnimationClip clip, Dictionary<string, float> offsets)
    {
        float endTime = clip.length > MinClipDuration ? clip.length : FallbackClipEndTime;
        foreach (var kv in offsets)
        {
            var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), kv.Key);
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null)
            {
                var k0 = new Keyframe(0f, kv.Value) { inTangent = 0f, outTangent = 0f };
                var k1 = new Keyframe(endTime, kv.Value) { inTangent = 0f, outTangent = 0f };
                curve = new AnimationCurve(new[] { k0, k1 });
            }
            else
            {
                var keys = curve.keys;
                for (int i = 0; i < keys.Length; i++)
                {
                    var k = keys[i];
                    k.value += kv.Value;
                    keys[i] = k;
                }
                curve = new AnimationCurve(keys);
            }
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }
    }

    private static void RenderVariant(string tag, AnimationClip clip)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabAssetPath);
        if (prefab == null) throw new Exception("prefab not found: " + PrefabAssetPath);
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        if (go == null) throw new Exception("prefab instantiate failed");
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;

        foreach (var cc in go.GetComponentsInChildren<CharacterController>(true)) cc.enabled = false;
        foreach (var cloth in go.GetComponentsInChildren<Cloth>(true)) UnityEngine.Object.DestroyImmediate(cloth);
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb != null && mb.GetType().Namespace != null && mb.GetType().Namespace.Contains("Magica"))
                mb.enabled = false;
        }

        var animator = go.GetComponentInChildren<Animator>(true);
        if (animator == null) throw new Exception("no animator on prefab");
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var ctrl = new AnimatorController();
        var state = ctrl.layers[0].stateMachine.AddState("Pose");
        state.motion = clip;
        animator.runtimeAnimatorController = ctrl;

        animator.Rebind();
        animator.Play("Pose", 0, 0f);
        animator.Update(0f);
        animator.Update(0f);

        var key = new GameObject("KeyLight");
        var k = key.AddComponent<Light>();
        k.type = LightType.Directional;
        k.intensity = KeyLightIntensity;
        k.color = KeyLightColor;
        key.transform.rotation = KeyLightRotation;

        var fill = new GameObject("FillLight");
        var f = fill.AddComponent<Light>();
        f.type = LightType.Directional;
        f.intensity = FillLightIntensity;
        f.color = FillLightColor;
        fill.transform.rotation = FillLightRotation;

        var camGO = new GameObject("PreviewCam");
        var cam = camGO.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = BackgroundColor;

        Bounds b = GetBounds(go);
        Vector3 center = b.center;

        CaptureView(cam, tag, "front", center, FrontViewDir, b.size.magnitude * StandardViewDistScale);
        CaptureView(cam, tag, "back", center, BackViewDir, b.size.magnitude * StandardViewDistScale);
        CaptureView(cam, tag, "side", center, SideViewDir, b.size.magnitude * StandardViewDistScale);
        CaptureView(cam, tag, "34", center, ThreeQuarterViewDir, b.size.magnitude * ThreeQuarterViewDistScale);

        UnityEngine.Object.DestroyImmediate(camGO);
        UnityEngine.Object.DestroyImmediate(key);
        UnityEngine.Object.DestroyImmediate(fill);
        UnityEngine.Object.DestroyImmediate(go);
    }

    private static Bounds GetBounds(GameObject go)
    {
        var rends = go.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return new Bounds(go.transform.position, Vector3.one * DefaultBoundsExtent);
        Bounds b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
        return b;
    }

    private static void CaptureView(Camera cam, string tag, string view, Vector3 center, Vector3 dir, float dist)
    {
        cam.transform.position = center + dir.normalized * dist;
        cam.transform.LookAt(center);
        cam.fieldOfView = PreviewFov;
        cam.nearClipPlane = PreviewNearPlane;
        cam.farClipPlane = PreviewFarPlane;

        var rt = new RenderTexture(RenderTextureSize, RenderTextureSize, RenderTextureDepth);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        File.WriteAllBytes(Path.Combine(OutDir, tag + "_" + view + ".png"), tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);
        UnityEngine.Object.DestroyImmediate(rt);
    }
}
