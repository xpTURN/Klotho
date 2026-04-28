using System.IO;
using UnityEditor;
using UnityEngine;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Json;

namespace xpTURN.Klotho.Editor
{
    public static class JsonToBytesConverter
    {
        [MenuItem("Tools/Klotho/Convert/DataAsset JsonToBytes")]
        private static void ConvertSelectedJsonToBytes()
        {
            var selected = Selection.activeObject as TextAsset;
            if (selected == null)
            {
                EditorUtility.DisplayDialog("JsonToBytes", "Please select a .json TextAsset in the Project window.", "OK");
                return;
            }

            var jsonPath = AssetDatabase.GetAssetPath(selected);
            if (string.IsNullOrEmpty(jsonPath) || !jsonPath.EndsWith(".json"))
            {
                EditorUtility.DisplayDialog("JsonToBytes", "The selected asset is not a .json file.", "OK");
                return;
            }

            var json = selected.text;
            if (string.IsNullOrWhiteSpace(json))
            {
                EditorUtility.DisplayDialog("JsonToBytes", "The JSON file is empty.", "OK");
                return;
            }

            byte[] bytes;
            try
            {
                bytes = DataAssetJsonConverter.ConvertMixedJsonToBytes(json);
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("JsonToBytes", $"Conversion failed:\n{ex.Message}", "OK");
                Debug.LogException(ex);
                return;
            }

            var bytesPath = Path.ChangeExtension(jsonPath, ".bytes");
            DataAssetWriter.SaveToFile(Path.GetFullPath(bytesPath), bytes);
            AssetDatabase.Refresh();

            var bytesAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(bytesPath);
            if (bytesAsset != null)
                EditorGUIUtility.PingObject(bytesAsset);

            Debug.Log($"[JsonToBytes] {jsonPath} → {bytesPath} ({bytes.Length} bytes)");
        }

        [MenuItem("Tools/Convert/JsonToBytes", true)]
        private static bool ConvertSelectedJsonToBytes_Validate()
        {
            var selected = Selection.activeObject as TextAsset;
            if (selected == null) return false;
            var path = AssetDatabase.GetAssetPath(selected);
            return !string.IsNullOrEmpty(path) && path.EndsWith(".json");
        }
    }
}
