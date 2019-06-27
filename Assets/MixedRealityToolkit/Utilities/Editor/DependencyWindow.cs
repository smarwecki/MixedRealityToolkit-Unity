// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

using Object = UnityEngine.Object;

namespace Microsoft.MixedReality.Toolkit.Utilities.Editor
{
    public class DependencyWindow : EditorWindow
    {
        private Object assetSelection = null;
        private int maxDisplayDepth = 8;
        private float assetGraphRefreshTime = 0.0f;
        private Vector2 scrollPosition = Vector2.zero;

        private class AssetGraphNode
        {
            public string guid = null;
            public HashSet<AssetGraphNode> assetsThisDependsOn = new HashSet<AssetGraphNode>();
            public HashSet<AssetGraphNode> assetsDependentOnThis = new HashSet<AssetGraphNode>();
        }

        private Dictionary<string, AssetGraphNode> dependencyGraph = new Dictionary<string, AssetGraphNode>();

        private const string windowTitle = "Dependency Window";
        private const string guidPrefix = "guid: ";
        private const string nullGuid = "00000000000000000000000000000000";
        private const int guidCharacterCount = 32;

        private readonly string[] assetsWithDependencies =
        {
            ".unity",
            ".prefab",
            ".asset",
            ".mat",
            ".anim",
            ".controller"
        };

        [MenuItem("Mixed Reality Toolkit/Utilities/Dependency Window", false, 3)]
        private static void ShowWindow()
        {
            var window = GetWindow<DependencyWindow>();
            window.titleContent = new GUIContent(windowTitle, EditorGUIUtility.IconContent("d_EditCollider").image);
            window.minSize = new Vector2(520.0f, 380.0f);
            window.RefreshAssetGraph();
            window.Show();
        }

        private void OnGUI()
        {
            MixedRealityEditorUtility.RenderMixedRealityToolkitLogo();

            EditorGUILayout.LabelField("Mixed Reality Toolkit Dependency Window", new GUIStyle() {fontSize = 12, fontStyle = FontStyle.Bold });
            EditorGUILayout.LabelField("This tool displays how assets reference and depend on each other. Dependencies are calculated by parsing guids within project YAML files, code dependencies are not considered.", EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space();

            if (EditorSettings.serializationMode != SerializationMode.ForceText)
            {
                EditorGUILayout.HelpBox("Dependencies can only be tracked with text assets. Please change the project serialization mode to \"Force Text\" via Edit->Project Settings->Editor->Asset Serialization", MessageType.Error);
            }

            assetSelection = EditorGUILayout.ObjectField("Asset Selection", assetSelection, typeof(Object), false);
            maxDisplayDepth = EditorGUILayout.IntSlider("Max Display Depth", maxDisplayDepth, 1, 32);

            EditorGUILayout.BeginHorizontal();
            {
                if (dependencyGraph.Count == 0)
                {
                    GUILayout.Label(string.Format("The dependency graph contains 0 assets, please refresh the dependency graph.", dependencyGraph.Count));
                }
                else
                {
                    GUILayout.Label(string.Format("The dependency graph contains {0:n0} assets and took {1:0.00} seconds to build.", dependencyGraph.Count, assetGraphRefreshTime));
                }

                if (GUILayout.Button("Refresh"))
                {
                    RefreshAssetGraph();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            GUILayout.BeginVertical("Box");
            {
                GUILayout.Label("Dependency Graph", EditorStyles.boldLabel);

                EditorGUILayout.LabelField(string.Empty, GUI.skin.horizontalSlider);

                var file = AssetDatabase.GetAssetPath(assetSelection);
                AssetGraphNode node;

                if (dependencyGraph.TryGetValue(AssetDatabase.AssetPathToGUID(file), out node))
                {
                    scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
                    {
                        GUILayout.Label("This asset depends on:", EditorStyles.boldLabel);

                        if (node.assetsThisDependsOn.Count != 0)
                        {
                            foreach (var dependency in node.assetsThisDependsOn)
                            {
                                DrawDependency(dependency, 0, maxDisplayDepth);
                            }
                        }
                        else
                        {
                            EditorGUILayout.HelpBox("Nothing.", MessageType.Info);
                        }

                        EditorGUILayout.LabelField(string.Empty, GUI.skin.horizontalSlider);

                        GUILayout.Label("Assets that depend on this:", EditorStyles.boldLabel);

                        if (node.assetsDependentOnThis.Count != 0)
                        {
                            foreach (var dependency in node.assetsDependentOnThis)
                            {
                                DrawDependencyRecurse(dependency, 0, maxDisplayDepth);
                                GUILayout.Space(8);
                            }
                        }
                        else
                        {
                            EditorGUILayout.HelpBox("Nothing, you could consider deleting this asset if it isn't referenced programmatically.", MessageType.Warning);
                        }
                    }
                    EditorGUILayout.EndScrollView();
                }
                else
                {
                    if (IsAsset(file))
                    {
                        EditorGUILayout.HelpBox(string.Format("Failed to find data for {0} try refreshing the dependency graph.", file), MessageType.Warning);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("Please select an asset above to see the dependency graph.", MessageType.Info);
                    }
                }
            }
            GUILayout.EndVertical();
        }

        private void OnSelectionChange()
        {
            if (Selection.objects.Length != 0)
            {
                if (IsAsset(AssetDatabase.GetAssetPath(Selection.objects[0])))
                {
                    assetSelection = Selection.objects[0];
                    Repaint();
                }
            }
        }

        private void RefreshAssetGraph()
        {
            var beginTime = DateTime.UtcNow;

            dependencyGraph.Clear();

            var metaExtension = ".meta";
            string[] metaFiles = Directory.GetFiles(Application.dataPath, "*" + metaExtension, SearchOption.AllDirectories);

            for (int i = 0; i < metaFiles.Length; ++i)
            {
                var progress = (float)i / metaFiles.Length;

                if (EditorUtility.DisplayCancelableProgressBar(windowTitle, "Building dependency graph...", progress))
                {
                    break;
                }

                var metaFile = metaFiles[i];
                var file = metaFile.Substring(0, metaFile.Length - metaExtension.Length);

                if (!IsAsset(file))
                {
                    continue;
                }

                string guid = GetGuidFromMeta(metaFile);

                if (!IsGuidValid(guid))
                {
                    continue;
                }

                var node = EnsureNode(guid);

                // Check if this asset can have dependencies.
                var extension = Path.GetExtension(file).ToLowerInvariant();

                if (Array.IndexOf(assetsWithDependencies, extension) >= 0)
                {
                    var dependencies = GetDependenciesInFile(file);

                    // Add linkage between this node and it's dependencies and the dependency and this node.
                    foreach (var dependency in dependencies)
                    {
                        node.assetsThisDependsOn.Add(EnsureNode(dependency));
                        EnsureNode(dependency).assetsDependentOnThis.Add(node);
                    }
                }
            }

            EditorUtility.ClearProgressBar();

            assetGraphRefreshTime = (float)((DateTime.UtcNow - beginTime).TotalSeconds);
        }

        private AssetGraphNode EnsureNode(string guid)
        {
            AssetGraphNode node;

            if (!dependencyGraph.TryGetValue(guid, out node))
            {
                node = new AssetGraphNode();
                node.guid = guid;
                dependencyGraph.Add(guid, node);
            }

            return node;
        }

        private static bool IsAsset(string file)
        {
            return File.Exists(file);
        }

        private static bool IsGuidValid(string guid)
        {
            return !string.IsNullOrEmpty(guid) && guid != nullGuid;
        }

        private static string GetGuidFromMeta(string file)
        {
            try
            {
                string[] lines = File.ReadAllLines(file);

                foreach (var line in lines)
                {
                    if (line.StartsWith(guidPrefix))
                    {
                        return line.Split(' ')[1];
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            return null;
        }

        private static List<string> GetDependenciesInFile(string file)
        {
            var guids = new List<string>();

            try
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    var index = line.IndexOf(guidPrefix);

                    if (index > 0)
                    {
                        var guid = line.Substring(index + guidPrefix.Length, guidCharacterCount);

                        if (IsGuidValid(guid) && !guids.Contains(guid))
                        {
                            guids.Add(guid);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            return guids;
        }

        private static void DrawDependency(AssetGraphNode node, int depth, int maxDepth)
        {
            EditorGUILayout.BeginHorizontal();
            {
                GUILayout.Space(depth * 16);

                if (depth != maxDepth)
                {
                    var path = AssetDatabase.GUIDToAssetPath(node.guid);

                    if (GUILayout.Button("Select", GUILayout.ExpandWidth(false)))
                    {
                        Selection.objects = new Object[] { AssetDatabase.LoadMainAssetAtPath(path) };
                    }

                    GUILayout.Label(string.IsNullOrEmpty(path) ? string.Format("Missing Asset: {0}", node.guid) : path, GUILayout.ExpandWidth(false));
                }
                else
                {
                    GUILayout.Label("Max display depth was exceeded...", GUILayout.ExpandWidth(false));
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawDependencyRecurse(AssetGraphNode node, int depth, int maxDepth)
        {
            // To avoid infinite recursion with circular dependencies, stop at a max depth.
            if (depth != maxDepth)
            {
                DrawDependency(node, depth, maxDepth);

                foreach (var dependency in node.assetsDependentOnThis)
                {
                    DrawDependencyRecurse(dependency, depth + 1, maxDepth);
                }
            }
            else
            {
                DrawDependency(null, depth, maxDepth);
            }
        }
    }
}
