#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// Keeps the Quest center-eye camera as the only enabled camera in edit mode,
// so the left and right eye cameras do not draw over the Scene view.
[InitializeOnLoad]
static class GaussianSplatEditorCameraGuard
{
    static double s_NextUpdateTime;

    static GaussianSplatEditorCameraGuard()
    {
        EditorApplication.update += Update;
    }

    static void Update()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        if (EditorApplication.timeSinceStartup < s_NextUpdateTime)
            return;
        s_NextUpdateTime = EditorApplication.timeSinceStartup + 0.2d;

        var rigs = Resources.FindObjectsOfTypeAll<OVRCameraRig>();
        foreach (var rig in rigs)
        {
            if (rig == null || !IsSceneObject(rig.gameObject))
                continue;
            ApplyEditorCameraMode(rig);
        }
    }

    static void ApplyEditorCameraMode(OVRCameraRig rig)
    {
        var centerCam = GetAnchorCamera(rig.centerEyeAnchor);
        var leftCam = GetAnchorCamera(rig.leftEyeAnchor);
        var rightCam = GetAnchorCamera(rig.rightEyeAnchor);
        var centerListener = GetAudioListener(rig.centerEyeAnchor);
        var leftListener = GetAudioListener(rig.leftEyeAnchor);
        var rightListener = GetAudioListener(rig.rightEyeAnchor);

        SetEnabled(centerCam, true);
        if (centerCam != null)
            centerCam.stereoTargetEye = StereoTargetEyeMask.Both;
        SetEnabled(leftCam, false);
        SetEnabled(rightCam, false);
        SetEnabled(centerListener, true);
        SetEnabled(leftListener, false);
        SetEnabled(rightListener, false);
    }

    static Camera GetAnchorCamera(Transform anchor) => anchor != null ? anchor.GetComponent<Camera>() : null;

    static AudioListener GetAudioListener(Transform anchor) => anchor != null ? anchor.GetComponent<AudioListener>() : null;

    static bool IsSceneObject(GameObject go) => go != null && go.scene.IsValid() && go.scene.isLoaded;

    static void SetEnabled(Behaviour behaviour, bool enabled)
    {
        if (behaviour != null && behaviour.enabled != enabled)
            behaviour.enabled = enabled;
    }
}
#endif
