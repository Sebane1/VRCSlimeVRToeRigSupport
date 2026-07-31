using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

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

    private TextAsset jsonFile;
    private AnimatorController targetController;
    private string clipOutputFolder = "Assets/Animations/ToeBlendAnimations";
    private Vector2 scroll;
    private ToeExtractResult extracted;
    private Dictionary<string, AnimationClip> remappedClips = new();
    private VRCExpressionParameters selectedExpParams;
    private bool invertValues;

    // Drag-and-drop bones for each foot (up to 5 toes)
    private Transform[] leftFootBones = new Transform[5];
    private Transform[] rightFootBones = new Transform[5];

    // Max Z rotation offset (splay) per toe
    private float[] leftFootSplay = new float[5] { 15, -3, -7, -15, -30f };
    private float[] rightFootSplay = new float[5] { -15, 3f, 7f, 15f, 30f };

    // Min/Max X rotation for curl (from neutral)
    private float curlMinX = 90;
    private float curlMaxX = -90;
    private bool useOSCSmoothPath;

    [MenuItem("Tools/Toe Rig/Add Toe Tracking Compatibility")]
    public static void Open() => GetWindow<ToeRigInjector>("Toe Tracking Configurator");

    void OnEnable()
    {
        targetController = Prefs.GetObject<AnimatorController>("ToeRig_TargetController_" + EditorSceneManager.GetActiveScene().name);
        LoadConfig();
    }
    void LoadConfig()
    {
        if (targetController != null)
        {
            selectedExpParams = Prefs.GetObject<VRCExpressionParameters>("ToeRig_SelectedExpParams_" + targetController.name);

            for (int i = 0; i < 5; i++)
            {
                leftFootBones[i] = Prefs.GetObject<Transform>($"ToeRig_Left_{i}_" + targetController.name);
                rightFootBones[i] = Prefs.GetObject<Transform>($"ToeRig_Right_{i}_" + targetController.name);
            }

            useOSCSmoothPath = EditorPrefs.GetBool("ToeRig_OSCPath_" + targetController.name, false);
            invertValues = EditorPrefs.GetBool("ToeRig_InvertValuePath_" + targetController.name, false);

            for (int i = 0; i < 5; i++)
            {
                leftFootSplay[i] = EditorPrefs.GetFloat($"ToeRig_LeftSplay_{i}_" + targetController.name, leftFootSplay[i]);
                rightFootSplay[i] = EditorPrefs.GetFloat($"ToeRig_RightSplay_{i}_" + targetController.name, rightFootSplay[i]);
            }
            for (int i = 0; i < 5; i++)
            {
                leftFootBones[i] = BonePrefs.LoadBone($"ToeRig_Left_{i}_" + targetController.name);
                rightFootBones[i] = BonePrefs.LoadBone($"ToeRig_Right_{i}_" + targetController.name);
            }
        }
    }
    void OnDisable()
    {
        if (targetController != null)
        {
            Prefs.SetObject("ToeRig_TargetController_" + EditorSceneManager.GetActiveScene().name, targetController);
            Prefs.SetObject("ToeRig_SelectedExpParams_" + targetController.name, selectedExpParams);

            for (int i = 0; i < 5; i++)
            {
                Prefs.SetObject($"ToeRig_Left_{i}_" + targetController.name, leftFootBones[i]);
                Prefs.SetObject($"ToeRig_Right_{i}_" + targetController.name, rightFootBones[i]);
            }
            EditorPrefs.SetBool("ToeRig_OSCPath_" + targetController.name, useOSCSmoothPath);
            EditorPrefs.SetBool("ToeRig_InvertValuePath_" + targetController.name, invertValues);

            for (int i = 0; i < 5; i++)
            {
                EditorPrefs.SetFloat($"ToeRig_LeftSplay_{i}_" + targetController.name, leftFootSplay[i]);
                EditorPrefs.SetFloat($"ToeRig_RightSplay_{i}_" + targetController.name, rightFootSplay[i]);
            }
            for (int i = 0; i < 5; i++)
            {
                BonePrefs.SaveBone($"ToeRig_Left_{i}_" + targetController.name, leftFootBones[i]);
                BonePrefs.SaveBone($"ToeRig_Right_{i}_" + targetController.name, rightFootBones[i]);
            }
        }
    }


    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.LabelField("Toe Tracking Compatibility Configurator", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        // Load pre-exported json file dynamically (handles package installation)
        if (jsonFile == null)
        {
            string[] guids = AssetDatabase.FindAssets("ToeConfiguration t:TextAsset");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                jsonFile = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            }
        }

        if (jsonFile == null)
        {
            EditorGUILayout.HelpBox("JSON file not found! Please ensure ToeConfiguration.json is imported.", MessageType.Error);
            return;
        }

        EditorGUILayout.LabelField("Using JSON:", AssetDatabase.GetAssetPath(jsonFile));
        selectedExpParams = (VRCExpressionParameters)EditorGUILayout.ObjectField("VRC Expression Parameters", selectedExpParams, typeof(VRCExpressionParameters), false);
        targetController = (AnimatorController)EditorGUILayout.ObjectField("Target Animator Controller", targetController, typeof(AnimatorController), false);
        clipOutputFolder = EditorGUILayout.TextField("Clip Output Folder", clipOutputFolder);
        EditorGUILayout.Space();

        useOSCSmoothPath = EditorGUILayout.Toggle($"Uses OSC Smooth", useOSCSmoothPath);
        EditorGUILayout.Space();
        invertValues = EditorGUILayout.Toggle($"Invert Values", invertValues);
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
            }
            else
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
    static void DestroyStateMachineRecursive(AnimatorStateMachine sm)
    {
        if (sm == null) return;

        // Destroy all states and their motions (clips / blendtrees) ---
        foreach (var child in sm.states)
        {
            AnimatorState state = child.state;

            // Remove motion (AnimationClip or BlendTree)
            if (state.motion is BlendTree bt)
            {
                AssetDatabase.RemoveObjectFromAsset(bt);
                Object.DestroyImmediate(bt, true);
            }
            else if (state.motion is AnimationClip clip)
            {
                // Only remove if clip is embedded inside controller (sub-asset)
                string assetPath = AssetDatabase.GetAssetPath(clip);
                if (assetPath == AssetDatabase.GetAssetPath(sm))
                {
                    AssetDatabase.RemoveObjectFromAsset(clip);
                    Object.DestroyImmediate(clip, true);
                }
            }

            // Destroy transitions
            foreach (var t in state.transitions)
            {
                AssetDatabase.RemoveObjectFromAsset(t);
                Object.DestroyImmediate(t, true);
            }

            // Remove the state itself
            AssetDatabase.RemoveObjectFromAsset(state);
            Object.DestroyImmediate(state, true);
        }

        // Destroy any-state transitions
        foreach (var t in sm.anyStateTransitions)
        {
            AssetDatabase.RemoveObjectFromAsset(t);
            Object.DestroyImmediate(t, true);
        }

        // Destroy entry transitions
        foreach (var t in sm.entryTransitions)
        {
            AssetDatabase.RemoveObjectFromAsset(t);
            Object.DestroyImmediate(t, true);
        }

        // Recursively destroy sub-state machines
        foreach (var sub in sm.stateMachines)
        {
            DestroyStateMachineRecursive(sub.stateMachine);
        }

        // Remove this state machine
        AssetDatabase.RemoveObjectFromAsset(sm);
        Object.DestroyImmediate(sm, true);
    }
    void CollectMotions(AnimatorStateMachine sm, HashSet<Motion> motions)
    {
        if (sm == null) return;

        foreach (var state in sm.states)
        {
            if (state.state.motion != null)
            {
                motions.Add(state.state.motion);
                if (state.state.motion is BlendTree bt)
                {
                    CollectBlendTreeMotions(bt, motions);
                }
            }
        }

        foreach (var sub in sm.stateMachines)
        {
            CollectMotions(sub.stateMachine, motions);
        }
    }

    void CollectBlendTreeMotions(BlendTree bt, HashSet<Motion> motions)
    {
        motions.Add(bt);
        foreach (var child in bt.children)
        {
            if (child.motion != null)
            {
                motions.Add(child.motion);
                if (child.motion is BlendTree nested)
                {
                    CollectBlendTreeMotions(nested, motions);
                }
            }
        }
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
        var allObjects = AssetDatabase.LoadAllAssetsAtPath(controllerPath);

        foreach (var extractedLayer in extracted.layers)
        {
            // Check if the layer already exists and remove it if it does
            var existingLayer = newLayersList.FirstOrDefault(l => l.name == extractedLayer.name);
            if (existingLayer != null)
            {
                // Remove the existing layer
                newLayersList.Remove(existingLayer);
                // Destroy its state machine sub-asset
                if (existingLayer.stateMachine != null)
                {
                    DestroyStateMachineRecursive(existingLayer.stateMachine);
                }
            }

            bool isLeftFoot = extractedLayer.name.ToLower().Contains("left");
            int toeIndex = int.Parse(new string(extractedLayer.name.Where(char.IsDigit).ToArray())) - 1;
            Transform toeBone = isLeftFoot ? leftFootBones[toeIndex] : rightFootBones[toeIndex];

            if (toeBone == null)
            {
                continue;
            }

            var injectedLayer = new AnimatorControllerLayer
            {
                name = extractedLayer.name,
                defaultWeight = 1f,
                stateMachine = new AnimatorStateMachine()
            };
            AssetDatabase.AddObjectToAsset(injectedLayer.stateMachine, controllerPath);

            AnimatorState firstState = null;

            foreach (var s in extractedLayer.states)
            {
                AnimatorState st = injectedLayer.stateMachine.AddState(s.name);
                if (s.isBlendTree)
                {
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
                        bt.AddChild(remappedClips[bentClipName], -1);
                    }
                    if (remappedClips.ContainsKey(neutralClipName))
                    {
                        bt.AddChild(remappedClips[neutralClipName], 0f);
                    }
                    if (remappedClips.ContainsKey(tipClipName))
                    {
                        bt.AddChild(remappedClips[tipClipName], 1);
                    }

                    st.motion = bt;

                    AssetDatabase.AddObjectToAsset(bt, controllerPath);
                    EditorUtility.SetDirty(bt);
                }
                else
                {
                    string neutralClipName = controller.name + $"{extractedLayer.name}Neutral";
                    if (remappedClips.ContainsKey(neutralClipName))
                    {
                        st.motion = remappedClips[neutralClipName];
                    }
                }

                if (firstState == null)
                {
                    firstState = st;
                }
            }

            if (firstState != null)
            {
                injectedLayer.stateMachine.defaultState = firstState;
            }

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
                }
                else if (toState != null)
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

    Transform GetAvatarRoot(Transform t)
    {
        if (t == null) return null;
        Transform parent = t.parent;
        Transform lastParent = t;
        while (parent != null)
        {
            if (parent.GetComponent<Animator>() != null)
            {
                return parent;
            }
            lastParent = parent;
            parent = parent.parent;
        }
        return lastParent;
    }

    Vector3 GetContinuousEuler(Vector3 reference, Vector3 newEuler)
    {
        float x = Mathf.DeltaAngle(reference.x, newEuler.x) + reference.x;
        float y = Mathf.DeltaAngle(reference.y, newEuler.y) + reference.y;
        float z = Mathf.DeltaAngle(reference.z, newEuler.z) + reference.z;
        return new Vector3(x, y, z);
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

        float splay = isSplayed ? (isLeftFoot ? GetToeSplay(isLeftFoot, toeIndex) : GetToeSplay(isLeftFoot, toeIndex)) : 0f;

        List<Transform> toeTransforms = new List<Transform>();

        toeTransforms.Add(toeBone);
        if (toeBone.childCount > 0)
        {
            var childBone = toeBone.GetChild(0);
            if (!childBone.name.EndsWith("_end"))
            {
                toeTransforms.Add(childBone);
            }
        }

        AnimationClip bentClip = new AnimationClip { frameRate = 60, wrapMode = WrapMode.Loop };
        AnimationClip neutralClip = new AnimationClip { frameRate = 60, wrapMode = WrapMode.Loop };
        AnimationClip tipClip = new AnimationClip { frameRate = 60, wrapMode = WrapMode.Loop };

        Transform avatarRoot = GetAvatarRoot(toeBone);
        Vector3 curlAxis = avatarRoot != null ? avatarRoot.right : Vector3.right;
        Vector3 splayAxis = avatarRoot != null ? avatarRoot.up : Vector3.up;

        for (int i = 0; i < toeTransforms.Count; i++)
        {
            Transform toeSegment = toeTransforms[i];
            Vector3 originalEuler = toeSegment.localEulerAngles;
            Quaternion originalRot = toeSegment.rotation;

            float curlMinAngle = (invertValues ? -curlMinX : curlMinX) / toeTransforms.Count;
            float curlMaxAngle = i == 0 ? (invertValues ? -curlMaxX : curlMaxX) : 0;
            float splayAngle = i == 0 ? (invertValues ? -splay : splay) : 0;

            // Bent (Curl down)
            toeSegment.Rotate(curlAxis, curlMinAngle, Space.World);
            toeSegment.Rotate(splayAxis, splayAngle, Space.World);
            Vector3 bentEuler = GetContinuousEuler(originalEuler, toeSegment.localEulerAngles);
            toeSegment.rotation = originalRot;

            // Tip (Curl up)
            toeSegment.Rotate(curlAxis, curlMaxAngle, Space.World);
            toeSegment.Rotate(splayAxis, splayAngle, Space.World);
            Vector3 tipEuler = GetContinuousEuler(originalEuler, toeSegment.localEulerAngles);
            toeSegment.rotation = originalRot;

            // Neutral
            toeSegment.Rotate(splayAxis, splayAngle, Space.World);
            Vector3 neutralEuler = GetContinuousEuler(originalEuler, toeSegment.localEulerAngles);
            toeSegment.rotation = originalRot;

            // Apply to curves
            bentClip.SetCurve(GetBonePath(toeSegment), typeof(Transform), "localEulerAnglesRaw.x", new AnimationCurve(new Keyframe(0, bentEuler.x), new Keyframe(1, bentEuler.x)));
            bentClip.SetCurve(GetBonePath(toeSegment), typeof(Transform), "localEulerAnglesRaw.y", new AnimationCurve(new Keyframe(0, bentEuler.y), new Keyframe(1, bentEuler.y)));
            bentClip.SetCurve(GetBonePath(toeSegment), typeof(Transform), "localEulerAnglesRaw.z", new AnimationCurve(new Keyframe(0, bentEuler.z), new Keyframe(1, bentEuler.z)));

            neutralClip.SetCurve(GetBonePath(toeSegment), typeof(Transform), "localEulerAnglesRaw.x", new AnimationCurve(new Keyframe(0, neutralEuler.x), new Keyframe(1, neutralEuler.x)));
            neutralClip.SetCurve(GetBonePath(toeSegment), typeof(Transform), "localEulerAnglesRaw.y", new AnimationCurve(new Keyframe(0, neutralEuler.y), new Keyframe(1, neutralEuler.y)));
            neutralClip.SetCurve(GetBonePath(toeSegment), typeof(Transform), "localEulerAnglesRaw.z", new AnimationCurve(new Keyframe(0, neutralEuler.z), new Keyframe(1, neutralEuler.z)));

            tipClip.SetCurve(GetBonePath(toeSegment), typeof(Transform), "localEulerAnglesRaw.x", new AnimationCurve(new Keyframe(0, tipEuler.x), new Keyframe(1, tipEuler.x)));
            tipClip.SetCurve(GetBonePath(toeSegment), typeof(Transform), "localEulerAnglesRaw.y", new AnimationCurve(new Keyframe(0, tipEuler.y), new Keyframe(1, tipEuler.y)));
            tipClip.SetCurve(GetBonePath(toeSegment), typeof(Transform), "localEulerAnglesRaw.z", new AnimationCurve(new Keyframe(0, tipEuler.z), new Keyframe(1, tipEuler.z)));
        }

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
            if (parent.name.StartsWith("Armature"))
            {
                break;
            }
            parent = parent.parent;
        }
        return path;
    }
}
static class Prefs
{
    public static void SetObject(string key, UnityEngine.Object obj)
    {
        if (obj == null)
        {
            EditorPrefs.DeleteKey(key);
            return;
        }
        string path = AssetDatabase.GetAssetPath(obj);
        string guid = AssetDatabase.AssetPathToGUID(path);
        EditorPrefs.SetString(key, guid);
    }

    public static T GetObject<T>(string key) where T : UnityEngine.Object
    {
        if (!EditorPrefs.HasKey(key)) return null;
        string guid = EditorPrefs.GetString(key);
        string path = AssetDatabase.GUIDToAssetPath(guid);
        return AssetDatabase.LoadAssetAtPath<T>(path);
    }
}
static class BonePrefs
{
    private static Transform GetRoot(Transform t)
    {
        if (t == null) return null;
        Transform root = t;
        while (root.parent != null) root = root.parent;
        return root;
    }

    public static void SaveBone(string key, Transform t)
    {
        if (t == null)
        {
            EditorPrefs.DeleteKey(key);
            return;
        }

        Transform root = GetRoot(t);
        string path = GetPathRelativeToRoot(t, root);
        EditorPrefs.SetString(key, path);
    }

    public static Transform LoadBone(string key)
    {
        if (!EditorPrefs.HasKey(key)) return null;
        string path = EditorPrefs.GetString(key);

        // Try to find the bone by searching all transforms in the scene
        foreach (var t in GameObject.FindObjectsOfType<Transform>())
        {
            if (t.name == path.Split('/')[^1] && GetPathRelativeToRoot(t, GetRoot(t)) == path)
                return t;
        }

        return null;
    }

    private static string GetPathRelativeToRoot(Transform t, Transform root)
    {
        if (t == root) return "";
        string path = t.name;
        Transform parent = t.parent;
        while (parent != null && parent != root)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return path;
    }
}
