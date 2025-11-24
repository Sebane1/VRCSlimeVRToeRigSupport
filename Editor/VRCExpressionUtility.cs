using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

public static class VRCExpressionUtility
{
    public static void AddMissingParameter(
        VRCExpressionParameters expressionParams,
        string paramName,
        VRCExpressionParameters.ValueType type,
        bool saved = true,
        float defaultValue = 0f,
        bool networkSynced = true)
    {
        if (expressionParams == null)
        {
            Debug.LogError("Expression Parameters asset is null!");
            return;
        }

        foreach (var p in expressionParams.parameters)
        {
            if (p != null && p.name == paramName)
            {
                return;
            }
        }

        var newParam = new VRCExpressionParameters.Parameter
        {
            name = paramName,
            valueType = type,
            saved = saved,
            defaultValue = defaultValue,
            networkSynced = networkSynced
        };

        var list = new System.Collections.Generic.List<VRCExpressionParameters.Parameter>(expressionParams.parameters);
        list.Add(newParam);

        expressionParams.parameters = list.ToArray();

        EditorUtility.SetDirty(expressionParams);
        AssetDatabase.SaveAssets();

        Debug.Log($"Added new expression parameter: {paramName} ({type})");
    }
}