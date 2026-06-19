using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public class ResourcesBuildHideFlagSanitizer : IPreprocessBuildWithReport
{
    public int callbackOrder => -1000;

    public void OnPreprocessBuild(BuildReport report)
    {
        // Clear DontSave flags from Resources assets before Unity packs resources.assets.
        int changedCount = ClearDontSaveFlagsInResources();
        if (changedCount > 0)
        {
            AssetDatabase.SaveAssets();
            Debug.Log($"[ResourcesBuildHideFlagSanitizer] Cleared DontSave hide flags from {changedCount} Resources asset object(s).");
        }
    }

    private static int ClearDontSaveFlagsInResources()
    {
        // Visit every asset object loaded from Resources paths and make it eligible for player builds.
        string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { "Assets/Resources" });
        int changedCount = 0;

        for (int i = 0; i < guids.Length; i++)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
            if (!IsSanitizableResourceAssetPath(assetPath))
            {
                continue;
            }

            changedCount += ClearDontSaveFlagsAtPath(assetPath);
        }

        return changedCount;
    }

    private static bool IsSanitizableResourceAssetPath(string assetPath)
    {
        // Skip folders, scenes, scripts, and packages so build preprocessing never reads scene objects.
        if (string.IsNullOrWhiteSpace(assetPath) || AssetDatabase.IsValidFolder(assetPath))
        {
            return false;
        }

        string extension = Path.GetExtension(assetPath);
        return extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".asset", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mat", StringComparison.OrdinalIgnoreCase);
    }

    private static int ClearDontSaveFlagsAtPath(string assetPath)
    {
        // Load all sub-assets at one path and clear build-blocking hide flags.
        UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
        int changedCount = 0;

        for (int i = 0; i < assets.Length; i++)
        {
            UnityEngine.Object asset = assets[i];
            if (asset == null || !HasDontSaveFlag(asset.hideFlags))
            {
                continue;
            }

            asset.hideFlags &= ~(HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor | HideFlags.DontUnloadUnusedAsset);
            EditorUtility.SetDirty(asset);
            changedCount++;
        }

        return changedCount;
    }

    private static bool HasDontSaveFlag(HideFlags hideFlags)
    {
        // Detect any flag combination that Unity refuses to serialize into resources.assets.
        const HideFlags dontSaveMask = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor | HideFlags.DontUnloadUnusedAsset;
        return (hideFlags & dontSaveMask) != 0;
    }
}
