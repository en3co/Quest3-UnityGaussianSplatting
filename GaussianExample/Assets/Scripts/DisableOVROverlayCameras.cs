using GaussianSplatting.Runtime;
using UnityEngine;

[DefaultExecutionOrder(200)]
public class DisableOVROverlayCameras : MonoBehaviour
{
    const float kStartupClampSeconds = 12f;

    OVRCameraRig m_Rig;
    float m_StopStartupClampAt;

    void Awake()
    {
        m_StopStartupClampAt = Time.unscaledTime + kStartupClampSeconds;
        m_Rig = FindAnyObjectByType<OVRCameraRig>();
        ApplyCenterEyeHookMode(true);
    }

    void LateUpdate()
    {
        if (Time.unscaledTime > m_StopStartupClampAt)
            return;

        if (m_Rig == null)
            m_Rig = FindAnyObjectByType<OVRCameraRig>();

        ApplyCenterEyeHookMode(false);
    }

    void ApplyCenterEyeHookMode(bool log)
    {
        if (m_Rig == null)
            return;

        m_Rig.usePerEyeCameras = false;
        m_Rig.disableEyeAnchorCameras = false;

        var centerCam = GetAnchorCamera(m_Rig.centerEyeAnchor);
        var leftCam = GetAnchorCamera(m_Rig.leftEyeAnchor);
        var rightCam = GetAnchorCamera(m_Rig.rightEyeAnchor);
        if (centerCam == null || leftCam == null || rightCam == null)
            return;

        bool centerWasEnabled = centerCam.enabled;
        bool leftWasEnabled = leftCam.enabled;
        bool rightWasEnabled = rightCam.enabled;

        centerCam.enabled = true;
        centerCam.stereoTargetEye = StereoTargetEyeMask.Both;
        leftCam.enabled = false;
        rightCam.enabled = false;

        if (centerCam.GetComponent<GaussianSplatCameraHook>() == null)
            centerCam.gameObject.AddComponent<GaussianSplatCameraHook>();

        if (log || !centerWasEnabled || leftWasEnabled || rightWasEnabled)
            Debug.Log("[DisableOVROverlayCameras] OVRCameraRig restored to center-eye mode with Gaussian per-eye hook.");
    }

    static Camera GetAnchorCamera(Transform anchor) => anchor != null ? anchor.GetComponent<Camera>() : null;
}
