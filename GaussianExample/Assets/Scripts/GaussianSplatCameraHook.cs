using UnityEngine;

namespace GaussianSplatting.Runtime
{
    [RequireComponent(typeof(Camera))]
    public class GaussianSplatCameraHook : MonoBehaviour
    {
        Camera m_Cam;
        void Awake() => m_Cam = GetComponent<Camera>();

        // Instance OnPreRender fires ONCE PER EYE in Multi-Pass stereo.
        // Static Camera.onPreRender fires once per frame — that is the wrong hook.
        void OnPreRender() =>
            GaussianSplatRenderSystem.instance.OnPerEyePreRender(m_Cam);
    }
}