using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(VirtualSensors))]
[CanEditMultipleObjects]
public class VirtualSensorsEditor : Editor
{
    private void OnEnable()
    {
        EditorApplication.update += Repaint;
    }

    private void OnDisable()
    {
        EditorApplication.update -= Repaint;
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawDefaultInspector();
        serializedObject.ApplyModifiedProperties();

        EditorGUILayout.Space();
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("Live Sensor Values", EditorStyles.boldLabel);

        if (targets.Length != 1)
        {
            EditorGUILayout.HelpBox(
                "Select one VirtualSensors component to view live values.",
                MessageType.Info);
        }
        else if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "Live values are available in Play Mode.",
                MessageType.Info);
        }
        else
        {
            VirtualSensors sensors = target as VirtualSensors;
            if (sensors == null)
            {
                EditorGUILayout.HelpBox(
                    "The selected VirtualSensors component is no longer available.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.LabelField("Robot", sensors.gameObject.name);
                EditorGUILayout.LabelField("Instance ID", sensors.GetInstanceID().ToString());
                EditorGUILayout.LabelField("Ultrasonic", sensors.Ultrasonic.ToString("F3"));
                EditorGUILayout.LabelField("Left IR", sensors.LeftIR.ToString("F0"));
                EditorGUILayout.LabelField("Right IR", sensors.RightIR.ToString("F0"));
                EditorGUILayout.LabelField("Gripper IR", sensors.GripperIR.ToString("F0"));

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(
                        "Last Seen Ball",
                        sensors.LastSeenBall,
                        typeof(GameObject),
                        true);
                }
            }
        }

        EditorGUILayout.EndVertical();
    }
}
