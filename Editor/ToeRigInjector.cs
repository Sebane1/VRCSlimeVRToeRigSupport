using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

public class ToeRigInjector : EditorWindow
{
    [Serializable]
    class ToeExtractResult
    {
        public List<ToeLayer> layers = new();
        public List<ParameterInfo> parameters = new();
    }

    [Serializable]
    class ToeLayer
    {
        public string name;
        public List<StateInfo> states;
        public List<TransitionInfo> transitions;
    }

    [Serializable]
    class StateInfo
    {
        public string name;
        public string clipName;
        public string clipPath;
        public bool isBlendTree = false;
        public string blendParameter; // the Animator parameter driving the 1D blend tree
        public List<BlendChild> blendChildren = new List<BlendChild>();
    }

    [Serializable]
    class BlendChild
    {
        public string clipName;
        public float threshold;
    }

    [Serializable]
    class TransitionInfo
    {
        public string from;
        public string to;
        public bool isEntry;
        public bool isExit;
        public bool hasExitTime;
        public float exitTime;
        public float duration;
        public bool fixedDuration;
        public TransitionInterruptionSource interruptionSource;
        public List<ConditionInfo> conditions = new();
    }

    [Serializable]
    class ConditionInfo
    {
        public string parameter;
        public AnimatorConditionMode mode;
        public float threshold;
    }

    [Serializable]
    class ParameterInfo
    {
        public string name;
        public AnimatorControllerParameterType type;
        public float defaultFloat;
        public int defaultInt;
        public bool defaultBool;
    }

    private const string jsonPath = "Assets/Editor/ToeConfiguration.json";
    private TextAsset jsonFile;
    private AnimatorController targetController;
    private string clipOutputFolder = "Assets/Animations/ToeBlendAnimations";
    private Vector2 scroll;
    private ToeExtractResult extracted;
    private Dictionary<string, AnimationClip> remappedClips = new();
    private VRCExpressionParameters selectedExpParams;

    // Drag-and-drop bones for each foot (up to 5 toes)
    private Transform[] leftFootBones = new Transform[5];
    private Transform[] rightFootBones = new Transform[5];

    // Max Z rotation offset (splay) per toe
    private float[] leftFootSplay = new float[5] { 30, -3, -7, -15, -30f };
    private float[] rightFootSplay = new float[5] { -30f, 3f, 7f, 15f, 30f };

    // Min/Max X rotation for curl (from neutral)
    private float curlMinX = -60f;
    private float curlMaxX = 60f;
    private bool useOSCSmoothPath;

    [MenuItem("Tools/Toe Rig/Add Toe Tracking Compatibility")]
    public static void Open() => GetWindow<ToeRigInjector>("Toe Tracking Configurator");

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.LabelField("Toe Tracking Compatibility Configurator", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        // Load pre-exported json file. We just provide this for the user.
        if (jsonFile == null)
        {
            jsonFile = AssetDatabase.LoadAssetAtPath<TextAsset>(jsonPath);
        }

        if (jsonFile == null)
        {
            EditorGUILayout.HelpBox("JSON file not found at:\n" + jsonPath, MessageType.Error);
            return;
        }

        EditorGUILayout.LabelField("Using JSON:", jsonPath);
        selectedExpParams = (VRCExpressionParameters)EditorGUILayout.ObjectField("VRC Expression Parameters", selectedExpParams, typeof(VRCExpressionParameters), false);
        targetController = (AnimatorController)EditorGUILayout.ObjectField("Target Animator Controller", targetController, typeof(AnimatorController), false);
        clipOutputFolder = EditorGUILayout.TextField("Clip Output Folder", clipOutputFolder);
        EditorGUILayout.Space();

        useOSCSmoothPath = EditorGUILayout.Toggle($"Uses OSC Smooth", useOSCSmoothPath);
        EditorGUILayout.Space();
        curlMinX = EditorGUILayout.FloatField("Curl Min X", curlMinX);
        curlMaxX = EditorGUILayout.FloatField("Curl Max X", curlMaxX);
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Left Foot Splay (big-to-pinky)");
        for (int i = 0; i < 5; i++)
        {
            leftFootSplay[i] = EditorGUILayout.FloatField($"Toe {i + 1}", leftFootSplay[i]);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Right Foot Splay (big-to-pinky)");
        for (int i = 0; i < 5; i++)
        {
            rightFootSplay[i] = EditorGUILayout.FloatField($"Toe {i + 1}", rightFootSplay[i]);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Left Foot Toe Bones");
        for (int i = 0; i < 5; i++)
        {
            leftFootBones[i] = (Transform)EditorGUILayout.ObjectField($"Toe {i + 1}", leftFootBones[i], typeof(Transform), true);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Right Foot Toe Bones");
        for (int i = 0; i < 5; i++)
        {
            rightFootBones[i] = (Transform)EditorGUILayout.ObjectField($"Toe {i + 1}", rightFootBones[i], typeof(Transform), true);
        }

        if (GUILayout.Button("Generate Toe Support"))
        {
            if (jsonFile == null || targetController == null)
            {
                EditorUtility.DisplayDialog("Missing data", "Please target AnimatorController.", "OK");
            } else
            {
                ApplyInjection();
            }
        }

        EditorGUILayout.EndScrollView();
    }

    void PreviewJson()
    {
        extracted = JsonUtility.FromJson<ToeExtractResult>(jsonFile.text);
        string msg = $"Layers: {extracted.layers.Count}\nParameters: {extracted.parameters.Count}\n";
        msg += "Layer names:\n";
        foreach (var l in extracted.layers) msg += $" - {l.name}\n";
        EditorUtility.DisplayDialog("JSON Preview", msg, "OK");
    }

    float GetToeSplay(bool isLeftFoot, int toeIndex)
    {
        if (isLeftFoot) return Mathf.Clamp(leftFootSplay[Mathf.Clamp(toeIndex, 0, 4)], -180f, 180f);
        else return Mathf.Clamp(rightFootSplay[Mathf.Clamp(toeIndex, 0, 4)], -180f, 180f);
    }

    void ApplyInjection()
    {
        extracted = JsonUtility.FromJson<ToeExtractResult>(jsonFile.text);
        if (extracted == null || extracted.layers == null || extracted.layers.Count == 0)
        {
            EditorUtility.DisplayDialog("JSON invalid", "Could not parse layers from JSON.", "OK");
            return;
        }

        var controller = targetController;

        // Add parameters here
        foreach (var p in extracted.parameters)
        {
            if (!controller.parameters.Any(cp => cp.name == p.name))
            {
                controller.AddParameter(p.name, p.type);
            }
            if (p.type == AnimatorControllerParameterType.Bool)
            {
                VRCExpressionUtility.AddMissingParameter(selectedExpParams, p.name, VRCExpressionParameters.ValueType.Bool, true, 0, true);
            }
        }


        if (!AssetDatabase.IsValidFolder(clipOutputFolder))
        {
            string parent = Path.GetDirectoryName(clipOutputFolder.TrimEnd('/'));
            string newFolder = Path.GetFileName(clipOutputFolder);
            if (!AssetDatabase.IsValidFolder(parent)) parent = "Assets";
            AssetDatabase.CreateFolder(parent, newFolder);
        }

        remappedClips.Clear();

        var newLayersList = controller.layers.ToList();
        string controllerPath = AssetDatabase.GetAssetPath(controller);

        foreach (var extractedLayer in extracted.layers)
        {
            // Check if the layer already exists and remove it if it does
            var existingLayer = newLayersList.FirstOrDefault(l => l.name == extractedLayer.name);
            if (existingLayer != null)
            {
                // Remove the existing layer
                newLayersList.Remove(existingLayer);
            }


            var injectedLayer = new AnimatorControllerLayer
            {
                name = extractedLayer.name,
                defaultWeight = 1f,
                stateMachine = new AnimatorStateMachine()
            };
            AssetDatabase.AddObjectToAsset(injectedLayer.stateMachine, controllerPath);

            bool isLeftFoot = extractedLayer.name.ToLower().Contains("left");
            int toeIndex = int.Parse(new string(extractedLayer.name.Where(char.IsDigit).ToArray())) - 1;
            Transform toeBone = isLeftFoot ? leftFootBones[toeIndex] : rightFootBones[toeIndex];

            AnimatorState firstState = null;

            foreach (var s in extractedLayer.states)
            {
                AnimatorState st = injectedLayer.stateMachine.AddState(s.name);

                bool isSplayed = s.name.ToLower().Contains("splayed");

                // Generate 3 clips per toe per state
                GenerateAutoAnim(extractedLayer.name, controller.name, isSplayed, toeBone, isLeftFoot, toeIndex);

                // Create blendtree
                var bt = new BlendTree { name = s.name, blendType = BlendTreeType.Simple1D, useAutomaticThresholds = false };

                if (!string.IsNullOrEmpty(s.blendParameter))
                {
                    // Assign the blend parameter from JSON
                    bt.blendParameter = (useOSCSmoothPath ? "OSCm/Proxy/" : "") + s.blendParameter;
                    if (!controller.parameters.Any(p => p.name == s.blendParameter && p.type == AnimatorControllerParameterType.Float))
                    {
                        controller.AddParameter(s.blendParameter, AnimatorControllerParameterType.Float);
                    }
                    VRCExpressionUtility.AddMissingParameter(selectedExpParams, s.blendParameter, VRCExpressionParameters.ValueType.Float, true, 0, true);
                }

                string bentClipName = controller.name + (isSplayed ? $"Splayed{extractedLayer.name}Bent" : $"{extractedLayer.name}Bent");
                string neutralClipName = controller.name + (isSplayed ? $"Splayed{extractedLayer.name}Neutral" : $"{extractedLayer.name}Neutral");
                string tipClipName = controller.name + (isSplayed ? $"SplayedTip{extractedLayer.name}" : $"TipToes{extractedLayer.name}");

                if (remappedClips.ContainsKey(bentClipName))
                {
                    bt.AddChild(remappedClips[bentClipName], -1f);
                }
                if (remappedClips.ContainsKey(neutralClipName))
                {
                    bt.AddChild(remappedClips[neutralClipName], 0f);
                }
                if (remappedClips.ContainsKey(tipClipName))
                {
                    bt.AddChild(remappedClips[tipClipName], 1f);
                }

                st.motion = bt;

                if (firstState == null) firstState = st;
            }

            if (firstState != null)
                injectedLayer.stateMachine.defaultState = firstState;

            foreach (var t in extractedLayer.transitions)
            {
                AnimatorState fromState = injectedLayer.stateMachine.states
                    .Select(x => x.state)
                    .FirstOrDefault(st => st.name == t.from);
                AnimatorState toState = injectedLayer.stateMachine.states
                    .Select(x => x.state)
                    .FirstOrDefault(st => st.name == t.to);

                if (fromState != null && toState != null)
                {
                    var tr = fromState.AddTransition(toState);
                    tr.hasExitTime = t.hasExitTime;
                    tr.exitTime = t.exitTime;
                    tr.duration = t.duration;
                    tr.hasFixedDuration = t.fixedDuration;
                    tr.interruptionSource = t.interruptionSource;

                    foreach (var c in t.conditions)
                        tr.AddCondition(c.mode, c.threshold, c.parameter);
                } else if (toState != null)
                {
                    var tr = injectedLayer.stateMachine.AddAnyStateTransition(toState);
                    tr.hasExitTime = false;
                    tr.duration = t.duration;
                    foreach (var c in t.conditions)
                        tr.AddCondition(c.mode, c.threshold, c.parameter);
                }
            }

            newLayersList.Add(injectedLayer);
        }

        controller.layers = newLayersList.ToArray();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("Toe configuration completed!", "Toe support has been added.", "OK");
    }

    void GenerateAutoAnim(string toeName, string controllerName, bool isSplayed, Transform toeBone, bool isLeftFoot, int toeIndex)
    {
        if (toeBone == null) return;

        // Clip names
        string bentClipName = controllerName + (isSplayed ? $"Splayed{toeName}Bent" : $"{toeName}Bent");
        string neutralClipName = controllerName + (isSplayed ? $"Splayed{toeName}Neutral" : $"{toeName}Neutral");
        string tipClipName = controllerName + (isSplayed ? $"SplayedTip{toeName}" : $"TipToes{toeName}");

        // Skip if already generated
        if (remappedClips.ContainsKey(bentClipName)) return;

        float splay = isSplayed ? GetToeSplay(isLeftFoot, toeIndex) : 0;

        Vector3 euler = (isLeftFoot ? leftFootBones[toeIndex] : rightFootBones[toeIndex]).transform.localRotation.eulerAngles;
        float x = euler.x;
        float y = euler.y;
        float z = euler.z;

        // Curl curves (X)
        AnimationCurve bentX = new AnimationCurve(new Keyframe(0, curlMinX), new Keyframe(1, curlMinX));
        AnimationCurve neutralX = new AnimationCurve(new Keyframe(0, x), new Keyframe(1, x));
        AnimationCurve tipX = new AnimationCurve(new Keyframe(0, curlMaxX), new Keyframe(1, curlMaxX));

        AnimationCurve bentY = new AnimationCurve(new Keyframe(0, y), new Keyframe(1, y));
        AnimationCurve neutralY = new AnimationCurve(new Keyframe(0, y), new Keyframe(1, y));
        AnimationCurve tipY = new AnimationCurve(new Keyframe(0, y), new Keyframe(1, y));

        // Splay curves (Z)
        AnimationCurve bentZ = new AnimationCurve(new Keyframe(0, z + splay), new Keyframe(1, z + splay));
        AnimationCurve neutralZ = new AnimationCurve(new Keyframe(0, z + splay), new Keyframe(1, z + splay));
        AnimationCurve tipZ = new AnimationCurve(new Keyframe(0, z + splay), new Keyframe(1, z + splay));

        AnimationClip bentClip = new AnimationClip { frameRate = 60, wrapMode = WrapMode.Loop };
        AnimationClip neutralClip = new AnimationClip { frameRate = 60, wrapMode = WrapMode.Loop };
        AnimationClip tipClip = new AnimationClip { frameRate = 60, wrapMode = WrapMode.Loop };

        bentClip.SetCurve(GetBonePath(toeBone), typeof(Transform), "localEulerAnglesRaw.x", bentX);
        bentClip.SetCurve(GetBonePath(toeBone), typeof(Transform), "localEulerAnglesRaw.y", bentY);
        bentClip.SetCurve(GetBonePath(toeBone), typeof(Transform), "localEulerAnglesRaw.z", bentZ);

        neutralClip.SetCurve(GetBonePath(toeBone), typeof(Transform), "localEulerAnglesRaw.x", neutralX);
        neutralClip.SetCurve(GetBonePath(toeBone), typeof(Transform), "localEulerAnglesRaw.y", neutralY);
        neutralClip.SetCurve(GetBonePath(toeBone), typeof(Transform), "localEulerAnglesRaw.z", neutralZ);


        tipClip.SetCurve(GetBonePath(toeBone), typeof(Transform), "localEulerAnglesRaw.x", tipX);
        tipClip.SetCurve(GetBonePath(toeBone), typeof(Transform), "localEulerAnglesRaw.y", tipY);
        tipClip.SetCurve(GetBonePath(toeBone), typeof(Transform), "localEulerAnglesRaw.z", tipZ);

        string bentPath = $"{clipOutputFolder}/{bentClipName}.anim";
        string neutralPath = $"{clipOutputFolder}/{neutralClipName}.anim";
        string tipPath = $"{clipOutputFolder}/{tipClipName}.anim";

        SaveOrOverwriteClip(bentClip, bentPath);
        SaveOrOverwriteClip(neutralClip, neutralPath);
        SaveOrOverwriteClip(tipClip, tipPath);

        remappedClips[bentClipName] = AssetDatabase.LoadAssetAtPath<AnimationClip>(bentPath);
        remappedClips[neutralClipName] = AssetDatabase.LoadAssetAtPath<AnimationClip>(neutralPath);
        remappedClips[tipClipName] = AssetDatabase.LoadAssetAtPath<AnimationClip>(tipPath);
    }
    void SaveOrOverwriteClip(AnimationClip clip, string fullPath)
    {
        string directory = Path.GetDirectoryName(fullPath);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            AssetDatabase.Refresh();
        }

        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(fullPath);
        if (existing != null)
        {
            AssetDatabase.DeleteAsset(fullPath);
        }

        AssetDatabase.CreateAsset(clip, fullPath);
        AssetDatabase.ImportAsset(fullPath, ImportAssetOptions.ForceUpdate);
    }
    string GetBonePath(Transform t)
    {
        if (t == null) return "";
        string path = t.name;
        Transform parent = t.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            if (parent.name == "Armature")
            {
                break;
            }
            parent = parent.parent;
        }
        return path;
    }
}
