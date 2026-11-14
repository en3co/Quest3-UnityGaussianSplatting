// GaussianSplatOccluders.cs
// Attach this to the same GameObject as GaussianSplatRenderer.
// Drag controller renderers in here — no custom editor to fight with.
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public class GaussianSplatOccluders : MonoBehaviour
    {
        [Header("Controller Ghost Fix")]
        [Tooltip("Drag the shader asset 'Hidden/Gaussian Splatting/StencilMask' here.")]
        public Shader stencilMaskShader;

        [Tooltip("Drag every controller MeshRenderer/SkinnedMeshRenderer here.")]
        public Renderer[] occluderRenderers;

        Material m_Mat;

        public Material GetOrCreateMaterial()
        {
            if (m_Mat != null) return m_Mat;
            if (stencilMaskShader == null) return null;

            m_Mat = new Material(stencilMaskShader) { name = "GaussianStencilMask" };
            var zFunc = SystemInfo.usesReversedZBuffer
                ? UnityEngine.Rendering.CompareFunction.GreaterEqual
                : UnityEngine.Rendering.CompareFunction.LessEqual;
            m_Mat.SetFloat("_ZTest", (float)zFunc);
            return m_Mat;
        }

        void OnDisable()
        {
            if (m_Mat != null) Object.DestroyImmediate(m_Mat);
            m_Mat = null;
        }
    }
}