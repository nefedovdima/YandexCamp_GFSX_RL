using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

public static class VastLinuxBuilder
{
    private const string BuildDirectory = "Build_Vast";
    private const string ExecutablePath = "Build_Vast/GFSX_Training.x86_64";
    private const string ManifestPath = "Build_Vast/BUILD_MANIFEST.txt";

    [MenuItem("Tools/GFSX/Build Vast Linux Player")]
    public static void BuildFromMenu()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("Build Vast Linux Player cannot run in Play Mode.");
            return;
        }

        try
        {
            Build();
        }
        catch (Exception exception)
        {
            Debug.LogError("Vast Linux build failed: " + exception);
        }
    }

    public static void BuildFromCommandLine()
    {
        try
        {
            Build();
        }
        catch (Exception exception)
        {
            Debug.LogError("Vast Linux command-line build failed: " + exception);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(1);
                return;
            }

            throw;
        }
    }

    private static void Build()
    {
        VastTrainingValidationResult validation;
        if (!VastTrainingSceneValidator.ValidateSceneAtPath(
                VastTrainingContract.TrainingScenePath,
                true,
                out validation))
        {
            throw new InvalidOperationException(
                "Training scene validation failed; Linux build was cancelled.");
        }

        GitMetadata git = ReadGitMetadata();
        if (!git.Available)
        {
            Debug.LogWarning(
                "Git metadata is unavailable. BUILD_MANIFEST.txt will record UNKNOWN values.");
        }
        else if (git.Dirty == true)
        {
            Debug.LogWarning(
                "DIRTY WORKING TREE: this build cannot be reproduced from its Git SHA alone.");
        }

        string projectRoot = GetProjectRoot();
        Directory.CreateDirectory(Path.Combine(projectRoot, BuildDirectory));

        bool previousRunInBackground = PlayerSettings.runInBackground;
        try
        {
            PlayerSettings.runInBackground = true;

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneLinux64)
            {
                bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildTargetGroup.Standalone,
                    BuildTarget.StandaloneLinux64);
                if (!switched)
                {
                    throw new InvalidOperationException(
                        "Could not switch the active build target to StandaloneLinux64. " +
                        "Verify that Linux Build Support (IL2CPP/Mono as required) is installed.");
                }
            }

            var options = new BuildPlayerOptions
            {
                scenes = VastTrainingContract.BuildScenes,
                locationPathName = ExecutablePath,
                target = BuildTarget.StandaloneLinux64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report == null || report.summary.result != BuildResult.Succeeded)
            {
                string result = report == null ? "NO_REPORT" : report.summary.result.ToString();
                int errors = report == null ? -1 : report.summary.totalErrors;
                throw new InvalidOperationException(
                    "BuildPipeline failed. Result=" + result + ", errors=" + errors + ".");
            }

            WriteManifest(projectRoot, git, report);
            Debug.Log(
                "Vast Linux Player build succeeded: " + ExecutablePath +
                "\nManifest: " + ManifestPath);
        }
        finally
        {
            PlayerSettings.runInBackground = previousRunInBackground;
        }
    }

    private static void WriteManifest(
        string projectRoot,
        GitMetadata git,
        BuildReport report)
    {
        string dirtyValue = git.Dirty.HasValue
            ? (git.Dirty.Value ? "TRUE" : "FALSE")
            : "UNKNOWN";

        string packageVersion = GetMlAgentsVersion();
        var manifest = new StringBuilder();

        if (git.Dirty == true)
        {
            manifest.AppendLine(
                "WARNING: DIRTY WORKING TREE - this build is not reproducible from Git SHA alone.");
        }

        manifest.AppendLine("Git SHA: " + git.Sha);
        manifest.AppendLine("Git branch: " + git.Branch);
        manifest.AppendLine("Dirty working tree: " + dirtyValue);
        manifest.AppendLine("Unity version: " + Application.unityVersion);
        manifest.AppendLine("com.unity.ml-agents version: " + packageVersion);
        manifest.AppendLine("Build UTC timestamp: " + DateTime.UtcNow.ToString("O"));
        manifest.AppendLine("Build target: Linux x86_64 Player (StandaloneLinux64)");
        manifest.AppendLine("Scene path: " + VastTrainingContract.TrainingScenePath);
        manifest.AppendLine("Executable path: " + ExecutablePath);
        manifest.AppendLine("Behavior Name: " + VastTrainingContract.BehaviorName);
        manifest.AppendLine("Expected arenas: " + VastTrainingContract.ArenaCount);
        manifest.AppendLine("Observations: " + VastTrainingContract.ObservationSize);
        manifest.AppendLine("Stacked vectors: " + VastTrainingContract.StackedVectors);
        manifest.AppendLine("Continuous actions: " + VastTrainingContract.ContinuousActions);
        manifest.AppendLine("Discrete branch: " + VastTrainingContract.DiscreteBranchSize);
        manifest.AppendLine("Decision Period: " + VastTrainingContract.DecisionPeriod);
        manifest.AppendLine("Recommended training config: config_stage1.yaml");
        manifest.AppendLine("Build result: " + report.summary.result);
        manifest.AppendLine("Build size bytes: " + report.summary.totalSize);

        string fullManifestPath = Path.Combine(projectRoot, ManifestPath);
        File.WriteAllText(
            fullManifestPath,
            manifest.ToString(),
            new UTF8Encoding(false));
    }

    private static string GetMlAgentsVersion()
    {
        try
        {
            PackageInfo package = PackageInfo.FindForAssetPath("Packages/com.unity.ml-agents");
            return package == null || string.IsNullOrEmpty(package.version)
                ? "UNKNOWN"
                : package.version;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Could not read com.unity.ml-agents version: " + exception.Message);
            return "UNKNOWN";
        }
    }

    private static GitMetadata ReadGitMetadata()
    {
        string projectRoot = GetProjectRoot();
        string sha;
        string branch;
        string status;

        bool shaOk = TryRunGit(projectRoot, "rev-parse HEAD", out sha);
        bool branchOk = TryRunGit(projectRoot, "branch --show-current", out branch);
        bool statusOk = TryRunGit(
            projectRoot,
            "status --porcelain --untracked-files=all",
            out status);

        bool available = shaOk && branchOk && statusOk;
        if (!available)
        {
            return new GitMetadata
            {
                Available = false,
                Sha = "UNKNOWN",
                Branch = "UNKNOWN",
                Dirty = null
            };
        }

        if (string.IsNullOrWhiteSpace(branch))
            branch = "DETACHED";

        return new GitMetadata
        {
            Available = true,
            Sha = sha.Trim(),
            Branch = branch.Trim(),
            Dirty = !string.IsNullOrWhiteSpace(status)
        };
    }

    private static bool TryRunGit(
        string workingDirectory,
        string arguments,
        out string output)
    {
        output = string.Empty;

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (System.Diagnostics.Process process =
                   System.Diagnostics.Process.Start(startInfo))
            {
                if (process == null)
                    return false;

                output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();

                if (!process.WaitForExit(10000))
                {
                    process.Kill();
                    Debug.LogWarning("Git command timed out: git " + arguments);
                    return false;
                }

                if (process.ExitCode == 0)
                    return true;

                Debug.LogWarning(
                    "Git command failed: git " + arguments + "\n" + error.Trim());
                return false;
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "Git command is unavailable: git " + arguments + "\n" + exception.Message);
            return false;
        }
    }

    private static string GetProjectRoot()
    {
        return Directory.GetParent(Application.dataPath).FullName;
    }

    private sealed class GitMetadata
    {
        internal bool Available;
        internal string Sha;
        internal string Branch;
        internal bool? Dirty;
    }
}
