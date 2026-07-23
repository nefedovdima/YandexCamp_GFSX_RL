using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class VastTrainingContract
{
    internal const string SourceScenePath = "Assets/Scenes/SampleScene.unity";
    internal const string TrainingScenePath = "Assets/Scenes/GFSX_Training.unity";
    internal const string BehaviorName = "GFSX_Brain";
    internal const int ArenaCount = 16;
    internal const int ObservationSize = 15;
    internal const int StackedVectors = 4;
    internal const int ContinuousActions = 3;
    internal const int DiscreteBranchSize = 3;
    internal const int DecisionPeriod = 5;

    internal static readonly string[] BuildScenes = { TrainingScenePath };

    internal static string ArenaName(int index)
    {
        return index == 0 ? "Arena" : "Arena (" + index + ")";
    }
}

internal sealed class VastTrainingValidationResult
{
    internal string ScenePath = "UNKNOWN";
    internal int ArenaCount;
    internal int ActiveArenaCount;
    internal int RobotBrainCount;
    internal int ActiveRobotBrainCount;
    internal int BehaviorParametersCount;
    internal int DecisionRequesterCount;
    internal readonly List<string> Violations = new List<string>();

    internal bool Passed
    {
        get { return Violations.Count == 0; }
    }

    internal string FormatReport(string title)
    {
        var report = new StringBuilder();
        report.AppendLine("=== " + title + " ===");
        report.AppendLine("Scene: " + ScenePath);
        report.AppendLine("Arenas: " + ArenaCount + "/" + VastTrainingContract.ArenaCount);
        report.AppendLine("Active arenas: " + ActiveArenaCount + "/" + VastTrainingContract.ArenaCount);
        report.AppendLine("RobotBrain: " + RobotBrainCount + "/" + VastTrainingContract.ArenaCount);
        report.AppendLine("Active RobotBrain: " + ActiveRobotBrainCount + "/" + VastTrainingContract.ArenaCount);
        report.AppendLine("BehaviorParameters: " + BehaviorParametersCount + "/" + VastTrainingContract.ArenaCount);
        report.AppendLine("DecisionRequester: " + DecisionRequesterCount + "/" + VastTrainingContract.ArenaCount);

        if (Violations.Count == 0)
        {
            report.AppendLine("Violations: none");
        }
        else
        {
            report.AppendLine("Violations (" + Violations.Count + "):");
            foreach (string violation in Violations)
                report.AppendLine("  - " + violation);
        }

        report.Append("Result: " + (Passed ? "PASS" : "FAIL"));
        return report.ToString();
    }
}

internal static class VastTrainingSceneValidator
{
    internal static bool EnsureNoDirtyOpenScenes(out string reason)
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isDirty)
                continue;

            reason = "Scene '" + scene.path + "' has unsaved changes. Save or discard them before running GFSX tooling.";
            return false;
        }

        reason = null;
        return true;
    }

    internal static bool ValidateSceneAtPath(
        string scenePath,
        bool logReport,
        out VastTrainingValidationResult result)
    {
        result = new VastTrainingValidationResult { ScenePath = scenePath };

        string dirtyReason;
        if (!EnsureNoDirtyOpenScenes(out dirtyReason))
        {
            result.Violations.Add(dirtyReason);
            LogResult(result, "VAST TRAINING SCENE VALIDATION", logReport);
            return false;
        }

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
        {
            result.Violations.Add("Scene asset does not exist: " + scenePath);
            LogResult(result, "VAST TRAINING SCENE VALIDATION", logReport);
            return false;
        }

        try
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            result = ValidateScene(scene, true, true, true);
        }
        catch (Exception exception)
        {
            result = new VastTrainingValidationResult { ScenePath = scenePath };
            result.Violations.Add("Failed to open or inspect scene: " + exception.Message);
        }

        LogResult(result, "VAST TRAINING SCENE VALIDATION", logReport);
        return result.Passed;
    }

    internal static VastTrainingValidationResult ValidateScene(
        Scene scene,
        bool requireTrainingSettings,
        bool requireActiveObjects,
        bool requireBuildInclusion)
    {
        var result = new VastTrainingValidationResult
        {
            ScenePath = scene.IsValid() ? scene.path : "INVALID"
        };

        if (!scene.IsValid() || !scene.isLoaded)
        {
            result.Violations.Add("Scene is invalid or is not loaded.");
            return result;
        }

        List<GameObject> arenas = FindExpectedArenaRoots(scene, result);
        ValidateDistinctPositions(arenas, result);

        foreach (GameObject arena in arenas)
        {
            string prefix = arena.name + ": ";
            if (arena.activeInHierarchy)
                result.ActiveArenaCount++;
            else if (requireActiveObjects)
                result.Violations.Add(prefix + "arena root is inactive.");

            RobotBrain[] robotBrains = arena.GetComponentsInChildren<RobotBrain>(true);
            BehaviorParameters[] behaviors = arena.GetComponentsInChildren<BehaviorParameters>(true);
            DecisionRequester[] requesters = arena.GetComponentsInChildren<DecisionRequester>(true);
            EnvironmentRandomizer[] randomizers = arena.GetComponentsInChildren<EnvironmentRandomizer>(true);
            VirtualSensors[] sensors = arena.GetComponentsInChildren<VirtualSensors>(true);
            SimulatedYoloCamera[] cameras = arena.GetComponentsInChildren<SimulatedYoloCamera>(true);
            Transform[] targetBalls = arena.GetComponentsInChildren<Transform>(true)
                .Where(transform => transform.name == "Target_ball")
                .ToArray();

            result.RobotBrainCount += robotBrains.Length;
            result.BehaviorParametersCount += behaviors.Length;
            result.DecisionRequesterCount += requesters.Length;

            RequireExactlyOne(robotBrains.Length, "RobotBrain", prefix, result);
            RequireExactlyOne(behaviors.Length, "BehaviorParameters", prefix, result);
            RequireExactlyOne(requesters.Length, "DecisionRequester", prefix, result);
            RequireExactlyOne(randomizers.Length, "EnvironmentRandomizer", prefix, result);
            RequireExactlyOne(targetBalls.Length, "Target_ball", prefix, result);
            RequireExactlyOne(sensors.Length, "VirtualSensors", prefix, result);
            RequireExactlyOne(cameras.Length, "SimulatedYoloCamera", prefix, result);

            RobotBrain robotBrain = robotBrains.Length == 1 ? robotBrains[0] : null;
            BehaviorParameters behavior = behaviors.Length == 1 ? behaviors[0] : null;
            DecisionRequester requester = requesters.Length == 1 ? requesters[0] : null;

            if (robotBrain != null)
            {
                bool robotActive = robotBrain.gameObject.activeInHierarchy && robotBrain.enabled;
                if (robotActive)
                    result.ActiveRobotBrainCount++;
                else if (requireActiveObjects)
                    result.Violations.Add(prefix + "RobotBrain GameObject or component is inactive.");
            }

            if (robotBrain != null && behavior != null && behavior.gameObject != robotBrain.gameObject)
                result.Violations.Add(prefix + "BehaviorParameters is not on the RobotBrain GameObject.");

            if (robotBrain != null && requester != null && requester.gameObject != robotBrain.gameObject)
                result.Violations.Add(prefix + "DecisionRequester is not on the RobotBrain GameObject.");

            if (behavior != null)
            {
                ValidateBehaviorContract(behavior, prefix, result);
                if (requireTrainingSettings)
                    ValidateTrainingBehavior(behavior, prefix, result);
            }

            if (requester != null && requireTrainingSettings)
            {
                if (!requester.enabled)
                    result.Violations.Add(prefix + "DecisionRequester is disabled.");
                if (requester.DecisionPeriod != VastTrainingContract.DecisionPeriod)
                    result.Violations.Add(prefix + "Decision Period must be " + VastTrainingContract.DecisionPeriod + ".");
                if (!requester.TakeActionsBetweenDecisions)
                    result.Violations.Add(prefix + "Take Actions Between Decisions must be enabled.");
            }
        }

        if (requireBuildInclusion)
        {
            bool intendedBuildIsExact =
                VastTrainingContract.BuildScenes.Length == 1 &&
                VastTrainingContract.BuildScenes[0] == VastTrainingContract.TrainingScenePath;

            if (!intendedBuildIsExact)
                result.Violations.Add("Intended build scene list must contain only GFSX_Training.unity.");

            if (!VastTrainingContract.BuildScenes.Contains(scene.path))
                result.Violations.Add("Scene is not included in the intended Vast Linux build.");
        }

        return result;
    }

    internal static void LogResult(
        VastTrainingValidationResult result,
        string title,
        bool enabled)
    {
        if (!enabled)
            return;

        string report = result.FormatReport(title);
        if (result.Passed)
            Debug.Log(report);
        else
            Debug.LogError(report);
    }

    private static List<GameObject> FindExpectedArenaRoots(
        Scene scene,
        VastTrainingValidationResult result)
    {
        GameObject[] roots = scene.GetRootGameObjects();
        GameObject[] arenaLikeRoots = roots.Where(root => IsArenaLikeName(root.name)).ToArray();
        result.ArenaCount = arenaLikeRoots.Length;

        if (arenaLikeRoots.Length != VastTrainingContract.ArenaCount)
        {
            result.Violations.Add(
                "Expected exactly " + VastTrainingContract.ArenaCount +
                " Arena root objects, found " + arenaLikeRoots.Length + ".");
        }

        var arenas = new List<GameObject>(VastTrainingContract.ArenaCount);
        for (int i = 0; i < VastTrainingContract.ArenaCount; i++)
        {
            string expectedName = VastTrainingContract.ArenaName(i);
            GameObject[] matches = roots.Where(root => root.name == expectedName).ToArray();
            if (matches.Length != 1)
            {
                result.Violations.Add(
                    "Expected one root named '" + expectedName + "', found " + matches.Length + ".");
                continue;
            }

            arenas.Add(matches[0]);
        }

        return arenas;
    }

    private static bool IsArenaLikeName(string name)
    {
        return name == "Arena" ||
               name.StartsWith("Arena (", StringComparison.Ordinal);
    }

    private static void ValidateDistinctPositions(
        IList<GameObject> arenas,
        VastTrainingValidationResult result)
    {
        for (int i = 0; i < arenas.Count; i++)
        {
            for (int j = i + 1; j < arenas.Count; j++)
            {
                if ((arenas[i].transform.position - arenas[j].transform.position).sqrMagnitude > 0.000001f)
                    continue;

                result.Violations.Add(
                    arenas[i].name + " and " + arenas[j].name +
                    " share the same world position " + arenas[i].transform.position + ".");
            }
        }
    }

    private static void RequireExactlyOne(
        int actual,
        string componentName,
        string prefix,
        VastTrainingValidationResult result)
    {
        if (actual != 1)
            result.Violations.Add(prefix + "expected exactly one " + componentName + ", found " + actual + ".");
    }

    private static void ValidateBehaviorContract(
        BehaviorParameters behavior,
        string prefix,
        VastTrainingValidationResult result)
    {
        if (behavior.BrainParameters == null)
        {
            result.Violations.Add(prefix + "BrainParameters is null.");
            return;
        }

        var brain = behavior.BrainParameters;
        var actionSpec = brain.ActionSpec;
        int[] branches = actionSpec.BranchSizes ?? Array.Empty<int>();

        if (brain.VectorObservationSize != VastTrainingContract.ObservationSize)
            result.Violations.Add(prefix + "Vector Observation Size must be " + VastTrainingContract.ObservationSize + ".");

        if (brain.NumStackedVectorObservations != VastTrainingContract.StackedVectors)
            result.Violations.Add(prefix + "Stacked Vectors must be " + VastTrainingContract.StackedVectors + ".");

        if (actionSpec.NumContinuousActions != VastTrainingContract.ContinuousActions)
            result.Violations.Add(prefix + "Continuous Actions must be " + VastTrainingContract.ContinuousActions + ".");

        if (branches.Length != 1 || branches[0] != VastTrainingContract.DiscreteBranchSize)
            result.Violations.Add(prefix + "Discrete action contract must be one branch of size " +
                                  VastTrainingContract.DiscreteBranchSize + ".");
    }

    private static void ValidateTrainingBehavior(
        BehaviorParameters behavior,
        string prefix,
        VastTrainingValidationResult result)
    {
        if (behavior.BehaviorType != BehaviorType.Default)
            result.Violations.Add(prefix + "Behavior Type must be Default (not Heuristic Only or Inference Only).");

        if (behavior.BehaviorName != VastTrainingContract.BehaviorName)
            result.Violations.Add(prefix + "Behavior Name must be '" + VastTrainingContract.BehaviorName + "'.");

        if (behavior.Model != null)
            result.Violations.Add(prefix + "Model override must be null.");

        if (behavior.TeamId != 0)
            result.Violations.Add(prefix + "TeamId must be 0.");
    }
}

public static class VastTrainingSceneTool
{
    [MenuItem("Tools/GFSX/Prepare Vast Training Scene")]
    public static void PrepareVastTrainingScene()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("Prepare Vast Training Scene cannot run in Play Mode.");
            return;
        }

        string dirtyReason;
        if (!VastTrainingSceneValidator.EnsureNoDirtyOpenScenes(out dirtyReason))
        {
            Debug.LogError(dirtyReason);
            return;
        }

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(VastTrainingContract.SourceScenePath) == null)
        {
            Debug.LogError("Source scene does not exist: " + VastTrainingContract.SourceScenePath);
            return;
        }

        try
        {
            Scene sourceScene = EditorSceneManager.OpenScene(
                VastTrainingContract.SourceScenePath,
                OpenSceneMode.Single);

            VastTrainingValidationResult sourceResult =
                VastTrainingSceneValidator.ValidateScene(sourceScene, false, false, false);
            VastTrainingSceneValidator.LogResult(
                sourceResult,
                "VAST SOURCE SCENE STRUCTURE",
                true);

            if (!sourceResult.Passed)
                throw new InvalidOperationException("Source scene contract validation failed.");

            bool copied = EditorSceneManager.SaveScene(
                sourceScene,
                VastTrainingContract.TrainingScenePath,
                true);
            if (!copied)
                throw new InvalidOperationException("Unity failed to recreate the training scene copy.");

            AssetDatabase.Refresh();
            Scene trainingScene = EditorSceneManager.OpenScene(
                VastTrainingContract.TrainingScenePath,
                OpenSceneMode.Single);
            ConfigureTrainingOverrides(trainingScene);

            VastTrainingValidationResult configuredResult =
                VastTrainingSceneValidator.ValidateScene(trainingScene, true, true, true);
            VastTrainingSceneValidator.LogResult(
                configuredResult,
                "VAST CONFIGURED SCENE BEFORE SAVE",
                true);

            if (!configuredResult.Passed)
                throw new InvalidOperationException("Configured training scene validation failed.");

            bool saved = EditorSceneManager.SaveScene(trainingScene);
            if (!saved)
                throw new InvalidOperationException("Unity failed to save the configured training scene.");

            VastTrainingValidationResult finalResult =
                VastTrainingSceneValidator.ValidateScene(trainingScene, true, true, true);
            VastTrainingSceneValidator.LogResult(
                finalResult,
                "VAST TRAINING SCENE PREPARATION",
                true);

            if (!finalResult.Passed)
                throw new InvalidOperationException("Saved training scene validation failed.");

            Debug.Log(
                "Prepared " + VastTrainingContract.TrainingScenePath +
                " from " + VastTrainingContract.SourceScenePath +
                ". The source scene and Arena prefab were not saved.");
        }
        catch (Exception exception)
        {
            Debug.LogError("Prepare Vast Training Scene failed: " + exception);
        }
    }

    [MenuItem("Tools/GFSX/Validate Vast Training Scene")]
    public static void ValidateVastTrainingScene()
    {
        VastTrainingValidationResult result;
        VastTrainingSceneValidator.ValidateSceneAtPath(
            VastTrainingContract.TrainingScenePath,
            true,
            out result);
    }

    private static void ConfigureTrainingOverrides(Scene scene)
    {
        GameObject[] roots = scene.GetRootGameObjects();

        for (int i = 0; i < VastTrainingContract.ArenaCount; i++)
        {
            string arenaName = VastTrainingContract.ArenaName(i);
            GameObject arena = roots.Single(root => root.name == arenaName);
            SetActiveAndRecord(arena);

            RobotBrain robotBrain = arena.GetComponentsInChildren<RobotBrain>(true).Single();
            BehaviorParameters behavior = arena.GetComponentsInChildren<BehaviorParameters>(true).Single();
            DecisionRequester requester = arena.GetComponentsInChildren<DecisionRequester>(true).Single();

            SetHierarchyActive(robotBrain.transform, arena.transform);
            robotBrain.enabled = true;
            EditorUtility.SetDirty(robotBrain);
            PrefabUtility.RecordPrefabInstancePropertyModifications(robotBrain);

            var serializedBehavior = new SerializedObject(behavior);
            serializedBehavior.FindProperty("m_BehaviorType").enumValueIndex = (int)BehaviorType.Default;
            serializedBehavior.FindProperty("m_BehaviorName").stringValue = VastTrainingContract.BehaviorName;
            serializedBehavior.FindProperty("m_Model").objectReferenceValue = null;
            serializedBehavior.FindProperty("TeamId").intValue = 0;
            serializedBehavior.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(behavior);
            PrefabUtility.RecordPrefabInstancePropertyModifications(behavior);

            requester.enabled = true;
            requester.DecisionPeriod = VastTrainingContract.DecisionPeriod;
            requester.TakeActionsBetweenDecisions = true;
            EditorUtility.SetDirty(requester);
            PrefabUtility.RecordPrefabInstancePropertyModifications(requester);
        }

        EditorSceneManager.MarkSceneDirty(scene);
    }

    private static void SetHierarchyActive(Transform child, Transform arenaRoot)
    {
        Transform current = child;
        while (current != null)
        {
            SetActiveAndRecord(current.gameObject);
            if (current == arenaRoot)
                break;
            current = current.parent;
        }
    }

    private static void SetActiveAndRecord(GameObject gameObject)
    {
        if (gameObject.activeSelf)
            return;

        gameObject.SetActive(true);
        EditorUtility.SetDirty(gameObject);
        PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject);
    }
}
