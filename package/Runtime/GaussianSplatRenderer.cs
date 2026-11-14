// SPDX-License-Identifier: MIT
// Quest-focused cleanup: restore stable render target handling and full stereo view data.
using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace GaussianSplatting.Runtime
{
    public class GaussianSplatRenderSystem
    {
        const string kCompositeArrayKeyword = "GAUSSIAN_SPLAT_ARRAY_RT";

        // ProfilerMarkers without MarkerFlags to avoid cross‑version issues
        internal static readonly ProfilerMarker s_ProfDraw    = new(ProfilerCategory.Render, "GaussianSplat.Draw");
        internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose");
        internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "GaussianSplat.CalcView");

        public static GaussianSplatRenderSystem instance => ms_Instance ??= new GaussianSplatRenderSystem();
        static GaussianSplatRenderSystem ms_Instance;

        readonly Dictionary<GaussianSplatRenderer, MaterialPropertyBlock> m_Splats = new();
        readonly List<(GaussianSplatRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();
        readonly Dictionary<Camera, CommandBuffer> m_PerCameraCommandBuffers = new();

        internal struct RenderItem
        {
            public GaussianSplatRenderer renderer;
            public Material displayMat;
            public MaterialPropertyBlock mpb;
            public int indexCount;
            public int instanceCount;
            public MeshTopology topology;
        }

        internal sealed class PreparedRenderData
        {
            public Material matComposite;
            public readonly List<RenderItem> renderItems = new();
        }

        PreparedRenderData m_LastPreparedData;
        const float kFixedEyeTextureResolutionScale = 1.0f;
        int m_LastSortedFrame = -1;
        int m_LastXrStabilizeFrame = -1;
        int m_LastOverlayCameraSweepFrame = -30;
        int m_LastOvrLateControllerClampFrame = -30;
        int m_LastControllerRendererCacheFrame = -30;
        bool m_FoveationClampAttempted;
        GaussianSplatOccluders[] m_CachedOccluders;
        Renderer[] m_CachedControllerRenderers = Array.Empty<Renderer>();
        Material m_AutoControllerMaskMaterial;
        int m_OccluderCacheFrame = -1;
        static readonly PropertyInfo s_ScaledPixelWidthProp = typeof(Camera).GetProperty("scaledPixelWidth", BindingFlags.Instance | BindingFlags.Public);
        static readonly PropertyInfo s_ScaledPixelHeightProp = typeof(Camera).GetProperty("scaledPixelHeight", BindingFlags.Instance | BindingFlags.Public);

        GaussianSplatOccluders[] GetOccluders()
        {
            if (m_OccluderCacheFrame != Time.frameCount)
            {
                m_CachedOccluders = UnityEngine.Object.FindObjectsByType<GaussianSplatOccluders>(FindObjectsSortMode.None);
                m_OccluderCacheFrame = Time.frameCount;
            }
            return m_CachedOccluders;
        }

        Renderer[] GetControllerRenderers()
        {
            if (Time.frameCount - m_LastControllerRendererCacheFrame < 30 && m_CachedControllerRenderers != null)
                return m_CachedControllerRenderers;

            m_LastControllerRendererCacheFrame = Time.frameCount;
            var allRenderers = Resources.FindObjectsOfTypeAll<Renderer>();
            var controllerRenderers = new List<Renderer>();
            if (!TryCollectOvrControllerHelperRenderers(controllerRenderers))
            {
                foreach (var renderer in allRenderers)
                {
                    if (!IsControllerRenderer(renderer))
                        continue;
                    controllerRenderers.Add(renderer);
                }
            }

            m_CachedControllerRenderers = controllerRenderers.ToArray();
            return m_CachedControllerRenderers;
        }

        static bool TryCollectOvrControllerHelperRenderers(List<Renderer> controllerRenderers)
        {
            if (!TryGetType("OVRControllerHelper", out var helperType))
                return false;

            var helpers = Resources.FindObjectsOfTypeAll(helperType);
            if (helpers == null || helpers.Length == 0)
                return false;

            var uniqueRenderers = new HashSet<Renderer>();
            foreach (var helper in helpers)
            {
                var component = helper as Component;
                if (component == null || !component.gameObject.activeInHierarchy)
                    continue;

                foreach (var renderer in component.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || !renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy)
                        continue;
                    if (uniqueRenderers.Add(renderer))
                        controllerRenderers.Add(renderer);
                }
            }

            return controllerRenderers.Count > 0;
        }

        static bool IsControllerRenderer(Renderer renderer)
        {
            if (renderer == null || !renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy)
                return false;

            bool foundController = false;
            bool foundEligibleControllerRoot = false;

            var tr = renderer.transform;
            while (tr != null)
            {
                string name = tr.name;
                if (name.IndexOf("synthetic", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;

                if (name.IndexOf("controller", StringComparison.OrdinalIgnoreCase) >= 0)
                    foundController = true;

                if (name.IndexOf("controllervisual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("controlleranchor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(name, "OVRControllerPrefab", StringComparison.OrdinalIgnoreCase))
                    foundEligibleControllerRoot = true;

                tr = tr.parent;
            }

            if (!foundController)
                return false;

            // Quest's interaction rigs keep extra synthetic/hidden controller trees alive.
            // Restrict the automatic splat stencil mask to the real visible controller hierarchy.
            if (Application.isPlaying && XRSettings.enabled)
                return foundEligibleControllerRoot;

            return true;
        }

        Material GetAutoControllerMaskMaterial()
        {
            if (m_AutoControllerMaskMaterial != null)
                return m_AutoControllerMaskMaterial;

            var shader = Shader.Find("Hidden/Gaussian Splatting/StencilMask");
            if (shader == null)
                return null;

            m_AutoControllerMaskMaterial = new Material(shader) { name = "GaussianAutoControllerStencilMask" };
            var zFunc = SystemInfo.usesReversedZBuffer ? CompareFunction.GreaterEqual : CompareFunction.LessEqual;
            m_AutoControllerMaskMaterial.SetFloat("_ZTest", (float)zFunc);
            return m_AutoControllerMaskMaterial;
        }

        void DrawOcclusionMasks(CommandBuffer cmb)
        {
            foreach (var occ in GetOccluders())
            {
                var mat = occ.GetOrCreateMaterial();
                if (mat == null || occ.occluderRenderers == null)
                    continue;
                foreach (var r in occ.occluderRenderers)
                    if (r != null && r.enabled && r.gameObject.activeInHierarchy)
                        cmb.DrawRenderer(r, mat);
            }

            var autoMaskMat = GetAutoControllerMaskMaterial();
            if (autoMaskMat == null)
                return;

            foreach (var renderer in GetControllerRenderers())
                cmb.DrawRenderer(renderer, autoMaskMat);
        }

        public void RegisterSplat(GaussianSplatRenderer r)
        {
            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
            }
            m_Splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(GaussianSplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r)) return;
            m_Splats.Remove(r);
            if (m_Splats.Count == 0)
            {
                foreach (var kvp in m_PerCameraCommandBuffers)
                {
                    if (kvp.Key)
                        kvp.Key.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, kvp.Value);
                    kvp.Value.Dispose();
                }
                if (m_AutoControllerMaskMaterial != null)
                    UnityEngine.Object.DestroyImmediate(m_AutoControllerMaskMaterial);
                m_AutoControllerMaskMaterial = null;
                m_PerCameraCommandBuffers.Clear();
                m_ActiveSplats.Clear();
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        internal static bool IsSinglePassStereoCamera(Camera cam)
        {
            return
                cam != null &&
                ShouldUseRuntimeXr() &&
                cam.stereoEnabled &&
                (XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced ||
                 XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassMultiview);
        }

        static int GetCameraDimension(Camera cam, PropertyInfo prop, int fallback)
        {
            if (cam == null)
                return 1;

            if (prop != null)
            {
                try
                {
                    object value = prop.GetValue(cam, null);
                    if (value is int scaledValue && scaledValue > 0)
                        return scaledValue;
                }
                catch
                {
                    // Fall back to the regular camera size if this Unity version does not expose scaled dimensions.
                }
            }

            return Mathf.Max(fallback, 1);
        }

        internal static void GetCameraRenderSize(Camera cam, out int width, out int height)
        {
            int fallbackWidth = cam != null ? cam.pixelWidth : 1;
            int fallbackHeight = cam != null ? cam.pixelHeight : 1;

            // In the Unity editor Game view, scaledPixelWidth/Height can report the
            // docked panel size instead of the actual selected render resolution.
            // The composite shader samples _GaussianSplatRT with full camera-target
            // pixel coordinates, so any mismatch here shows up as striped/static output.
            if (!ShouldUseRuntimeXr())
            {
                width = Mathf.Max(fallbackWidth, 1);
                height = Mathf.Max(fallbackHeight, 1);
                return;
            }

            width = GetCameraDimension(cam, s_ScaledPixelWidthProp, fallbackWidth);
            height = GetCameraDimension(cam, s_ScaledPixelHeightProp, fallbackHeight);
        }

        void StabilizeXrRenderScale(Camera cam)
        {
            if (!Application.isPlaying || !ShouldUseRuntimeXr() || cam == null)
                return;

            cam.allowDynamicResolution = false;

            if (m_LastXrStabilizeFrame == Time.frameCount)
                return;
            m_LastXrStabilizeFrame = Time.frameCount;

            if (!Mathf.Approximately(XRSettings.eyeTextureResolutionScale, kFixedEyeTextureResolutionScale))
                XRSettings.eyeTextureResolutionScale = kFixedEyeTextureResolutionScale;

            // Quest can quietly resize the eye buffers a few seconds after startup.
            // Keep the XR buffers pinned so the splat RT and composite pass stay aligned.
            ScalableBufferManager.ResizeBuffers(1f, 1f);

            if (!m_FoveationClampAttempted)
            {
                m_FoveationClampAttempted = true;
                TryEnableOvrFoveation();
            }

            if (Time.frameCount - m_LastOvrLateControllerClampFrame >= 30)
            {
                m_LastOvrLateControllerClampFrame = Time.frameCount;
                TryDisableOvrLateControllerUpdate();
            }

            if (Time.frameCount - m_LastOverlayCameraSweepFrame >= 30)
            {
                m_LastOverlayCameraSweepFrame = Time.frameCount;
                NeutralizeOverlayEyeCameras();
            }
        }

        static bool NeutralizeOverlayEyeCameras()
        {
            if (IsUsingOvrPerEyeCameras())
                return false;

            bool foundOverlayCamera = false;
            var cameras = Resources.FindObjectsOfTypeAll<Camera>();
            foreach (var overlayCam in cameras)
            {
                if (overlayCam == null || overlayCam.cameraType == CameraType.Preview)
                    continue;

                if (overlayCam.stereoTargetEye != StereoTargetEyeMask.Left &&
                    overlayCam.stereoTargetEye != StereoTargetEyeMask.Right)
                    continue;

                overlayCam.cullingMask = 0;
                overlayCam.enabled = false;
                foundOverlayCamera = true;
            }

            return foundOverlayCamera;
        }

        static void TryEnableOvrFoveation()
        {
            if (!TryGetOvrManager(out var ovrManagerType, out var instance))
                return;

            TrySetMember(ovrManagerType, instance, "useDynamicFixedFoveatedRendering", true);
            TrySetMember(ovrManagerType, instance, "useDynamicFoveatedRendering", true);
            TrySetMember(ovrManagerType, instance, "useDynamicResolution", false);
            TrySetMember(ovrManagerType, instance, "enableDynamicResolution", false);
            TrySetMember(ovrManagerType, instance, "fixedFoveatedRenderingLevel", 3);
            TrySetMember(ovrManagerType, instance, "foveatedRenderingLevel", 3);
        }

        static void TryDisableOvrLateControllerUpdate()
        {
            if (!TryGetOvrManager(out var ovrManagerType, out var instance))
                return;

            // OVRManager can redraw/update controllers using a later pose than the
            // main scene camera. With this BiRP splat composite path that produces
            // the remaining offset controller ghost on Quest.
            TrySetMember(ovrManagerType, instance, "LateControllerUpdate", false);
            TrySetMember(ovrManagerType, instance, "lateControllerUpdate", false);
            TrySetMember(ovrManagerType, instance, "useLateControllerUpdate", false);
        }

        static bool TryGetOvrManager(out Type ovrManagerType, out object instance)
        {
            instance = null;
            if (!TryGetType("OVRManager", out ovrManagerType))
                return false;

            var instances = Resources.FindObjectsOfTypeAll(ovrManagerType);
            instance = instances != null && instances.Length > 0 ? instances[0] : null;
            return true;
        }

        static bool IsUsingOvrPerEyeCameras()
        {
            if (!TryGetType("OVRCameraRig", out var cameraRigType))
                return false;

            var instances = Resources.FindObjectsOfTypeAll(cameraRigType);
            if (instances == null || instances.Length == 0)
                return false;

            return TryGetBoolMember(cameraRigType, instances[0], "usePerEyeCameras");
        }

        static bool TryGetType(string typeName, out Type foundType)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foundType = asm.GetType(typeName);
                if (foundType != null)
                    return true;
            }

            foundType = null;
            return false;
        }

        static bool TryGetBoolMember(Type type, object instance, string memberName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            var prop = type.GetProperty(memberName, flags);
            if (prop != null && prop.PropertyType == typeof(bool) && prop.GetGetMethod(true) != null)
            {
                try
                {
                    return (bool)prop.GetValue(instance);
                }
                catch
                {
                    return false;
                }
            }

            var field = type.GetField(memberName, flags);
            if (field != null && field.FieldType == typeof(bool))
            {
                try
                {
                    return (bool)field.GetValue(instance);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        static bool TrySetMember(Type type, object instance, string memberName, object value)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            var prop = type.GetProperty(memberName, flags);
            if (prop != null && prop.CanWrite)
            {
                var setter = prop.GetSetMethod(true);
                if (setter == null)
                    return false;

                object convertedValue = ConvertMemberValue(prop.PropertyType, value);
                object target = setter.IsStatic ? null : instance;
                if (target != null || setter.IsStatic)
                {
                    try
                    {
                        setter.Invoke(target, new[] { convertedValue });
                        return true;
                    }
                    catch
                    {
                        // Ignore package-version differences in Meta's runtime.
                    }
                }
                return false;
            }

            var field = type.GetField(memberName, flags);
            if (field == null)
                return false;

            object convertedFieldValue = ConvertMemberValue(field.FieldType, value);
            object fieldTarget = field.IsStatic ? null : instance;
            if (fieldTarget == null && !field.IsStatic)
                return false;

            try
            {
                field.SetValue(fieldTarget, convertedFieldValue);
                return true;
            }
            catch
            {
                // Ignore package-version differences in Meta's runtime.
            }
            return false;
        }

        static object ConvertMemberValue(Type memberType, object fallbackValue)
        {
            if (memberType.IsEnum)
                return Enum.ToObject(memberType, 0);
            if (memberType == typeof(bool))
                return false;
            if (memberType == typeof(int))
                return 0;
            if (memberType == typeof(float))
                return 0f;
            return fallbackValue;
        }

        public bool GatherSplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;

            if (ShouldUseRuntimeXr())
            {
                bool usePerEyeCameras = IsUsingOvrPerEyeCameras();
                if (usePerEyeCameras)
                {
                    if (cam.stereoTargetEye != StereoTargetEyeMask.Left &&
                        cam.stereoTargetEye != StereoTargetEyeMask.Right)
                        return false;
                }
                else if (cam.stereoTargetEye != StereoTargetEyeMask.Both)
                {
                    return false;
                }
            }

            m_ActiveSplats.Clear();
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                m_ActiveSplats.Add((kvp.Key, kvp.Value));
            }
            if (m_ActiveSplats.Count == 0)
                return false;

            var camTr = cam.transform;
            m_ActiveSplats.Sort((a, b) =>
            {
                var orderA = a.Item1.m_RenderOrder;
                var orderB = b.Item1.m_RenderOrder;
                if (orderA != orderB)
                    return orderB.CompareTo(orderA);
                var posA = camTr.InverseTransformPoint(a.Item1.transform.position);
                var posB = camTr.InverseTransformPoint(b.Item1.transform.position);
                return posA.z.CompareTo(posB.z);
            });
            return true;
        }

        internal PreparedRenderData PrepareSplats(Camera cam, CommandBuffer cmb, bool doSort = true)
        {
            m_LastPreparedData ??= new PreparedRenderData();
            m_LastPreparedData.renderItems.Clear();

            Material matComposite = null;
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                gs.EnsureMaterials();
                matComposite = gs.m_MatComposite;
                var mpb = kvp.Item2;
                var matrix = gs.transform.localToWorldMatrix;

                int sortEvery = Mathf.Max(1, gs.m_SortNthFrame);
                if (doSort)
                {
                    if (gs.m_FrameCounter % sortEvery == 0)
                        gs.SortPoints(cmb, cam, matrix);
                    ++gs.m_FrameCounter;
                }

                mpb.Clear();
                Material displayMat = gs.m_RenderMode switch
                {
                    GaussianSplatRenderer.RenderMode.DebugPoints => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugPointIndices => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugBoxes => gs.m_MatDebugBoxes,
                    GaussianSplatRenderer.RenderMode.DebugChunkBounds => gs.m_MatDebugBoxes,
                    _ => gs.m_MatSplats
                };
                if (displayMat == null) continue;

                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.m_GpuChunks);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);
                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, gs.m_PointDisplaySize);
                int runtimeShOrder = ShouldUseRuntimeXr() ? Mathf.Min(gs.m_SHOrder, GaussianSplatRenderer.kRuntimeQuestMaxShOrder) : gs.m_SHOrder;
                float fragmentAlphaCutoff = ShouldUseRuntimeXr() ? GaussianSplatRenderer.kRuntimeQuestFragmentAlphaCutoff : 1.0f / 255.0f;
                float minSplatAxisPx = ShouldUseRuntimeXr() ? GaussianSplatRenderer.kRuntimeQuestMinSplatAxisPx : 0f;
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, runtimeShOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.m_SHOnly ? 1 : 0);
                mpb.SetFloat(GaussianSplatRenderer.Props.FragmentAlphaCutoff, fragmentAlphaCutoff);
                mpb.SetFloat(GaussianSplatRenderer.Props.MinSplatAxisPx, minSplatAxisPx);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

                cmb.BeginSample(s_ProfCalcView);
                gs.CalcViewData(cmb, cam);
                cmb.EndSample(s_ProfCalcView);

                int indexCount = 6;
                int instanceCount = gs.splatCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (gs.m_RenderMode is GaussianSplatRenderer.RenderMode.DebugBoxes or GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    instanceCount = gs.m_GpuChunksValid ? gs.m_GpuChunks.count : 0;

                if (ShouldUseRuntimeXr() && gs.m_RenderMode == GaussianSplatRenderer.RenderMode.Splats)
                    instanceCount = Mathf.Min(instanceCount, GaussianSplatRenderer.kRuntimeQuestMaxRenderedSplats);

                m_LastPreparedData.renderItems.Add(new RenderItem
                {
                    renderer = gs,
                    displayMat = displayMat,
                    mpb = mpb,
                    indexCount = indexCount,
                    instanceCount = Mathf.Min(instanceCount, 600000),
                    topology = topology,
                });
            }

            m_LastPreparedData.matComposite = matComposite;
            return m_LastPreparedData;
        }

        internal void RenderPreparedSplats(CommandBuffer cmb, int eyeIndex)
        {
            if (m_LastPreparedData == null || m_LastPreparedData.renderItems.Count == 0)
                return;

            int isStereo = eyeIndex >= 0 ? 1 : 0;
            int safeEyeIndex = Mathf.Max(eyeIndex, 0);

            foreach (var item in m_LastPreparedData.renderItems)
            {
                item.mpb.SetInteger(GaussianSplatRenderer.Props.IsStereo, isStereo);
                item.mpb.SetInteger(GaussianSplatRenderer.Props.EyeIndex, safeEyeIndex);

                cmb.BeginSample(s_ProfDraw);
                cmb.DrawProcedural(
                    item.renderer.m_GpuIndexBuffer,
                    item.renderer.transform.localToWorldMatrix,
                    item.displayMat,
                    0,
                    item.topology,
                    item.indexCount,
                    item.instanceCount,
                    item.mpb);
                cmb.EndSample(s_ProfDraw);
            }
        }

        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb, int eyeIndex = -1, bool doSort = true)
        {
            var renderData = PrepareSplats(cam, cmb, doSort);
            RenderPreparedSplats(cmb, eyeIndex);
            return renderData.matComposite;
        }

        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            if (!m_PerCameraCommandBuffers.TryGetValue(cam, out var cmb))
            {
                cmb = new CommandBuffer { name = "RenderGaussianSplats" };
                m_PerCameraCommandBuffers[cam] = cmb;
                if (GraphicsSettings.currentRenderPipeline == null && cam != null)
                    cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, cmb);
            }
            cmb.Clear();
            return cmb;
        }

        public void OnPreCullCamera(Camera cam)
        {
            if (ShouldUseBiRPPerEyeHook(cam) &&
                HasGaussianSplatCameraHook(cam))
                return;

            if (!GatherSplatsForCamera(cam))
                return;

            if (!ShouldUseRuntimeXr())
            {
                RenderBiRPCameraLegacy(cam);
                return;
            }

            StabilizeXrRenderScale(cam);

            var cmb = InitialClearCmdBuffer(cam);
            GetCameraRenderSize(cam, out int rtW, out int rtH);
            GraphicsFormat splatRtFormat = GetSplatRenderTextureFormat(true);

            cmb.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, rtW, rtH, 0, FilterMode.Point, splatRtFormat);
            cmb.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            cmb.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            cmb.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            Material matComposite = SortAndRenderSplats(cam, cmb);
            if (matComposite == null)
            {
                cmb.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
                return;
            }
            SetCompositeArrayKeyword(matComposite, false);

            cmb.BeginSample(s_ProfCompose);
            cmb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.Depth);
            DrawOcclusionMasks(cmb);

            cmb.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            cmb.EndSample(s_ProfCompose);
            cmb.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
        }

        void RenderBiRPCameraLegacy(Camera cam)
        {
            var cmb = InitialClearCmdBuffer(cam);
            GraphicsFormat splatRtFormat = GetSplatRenderTextureFormat(false);
            cmb.GetTemporaryRT(
                GaussianSplatRenderer.Props.GaussianSplatRT,
                -1,
                -1,
                0,
                FilterMode.Point,
                splatRtFormat);
            cmb.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            cmb.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);
            cmb.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            Material matComposite = SortAndRenderSplats(cam, cmb);
            if (matComposite == null)
            {
                cmb.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
                return;
            }

            SetCompositeArrayKeyword(matComposite, false);
            cmb.BeginSample(s_ProfCompose);
            cmb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            cmb.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            cmb.EndSample(s_ProfCompose);
            cmb.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
        }

        // GaussianExample's CenterEyeAnchor hook calls this once per eye in BiRP multi-pass.
        public void OnPerEyePreRender(Camera cam)
        {
            if (!ShouldUseBiRPPerEyeHook(cam))
                return;
            if (!GatherSplatsForCamera(cam))
                return;

            StabilizeXrRenderScale(cam);

            var cmb = InitialClearCmdBuffer(cam);
            GetCameraRenderSize(cam, out int rtW, out int rtH);
            GraphicsFormat splatRtFormat = GetSplatRenderTextureFormat(true);
            cmb.GetTemporaryRT(
                GaussianSplatRenderer.Props.GaussianSplatRT,
                rtW, rtH,
                0,
                FilterMode.Point,
                splatRtFormat);

            cmb.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT);
            cmb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
            cmb.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            bool doSort = Time.frameCount != m_LastSortedFrame;
            if (doSort)
                m_LastSortedFrame = Time.frameCount;

            Material matComposite = SortAndRenderSplats(cam, cmb, -1, doSort);
            if (matComposite == null)
            {
                cmb.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
                return;
            }
            SetCompositeArrayKeyword(matComposite, false);

            cmb.BeginSample(s_ProfCompose);
            cmb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.Depth);
            DrawOcclusionMasks(cmb);

            cmb.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            cmb.EndSample(s_ProfCompose);
            cmb.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
        }

        static bool HasGaussianSplatCameraHook(Camera cam)
        {
            var behaviours = cam.GetComponents<MonoBehaviour>();
            foreach (var behaviour in behaviours)
            {
                if (behaviour == null)
                    continue;
                if (behaviour.GetType().FullName == "GaussianSplatting.Runtime.GaussianSplatCameraHook")
                    return true;
            }
            return false;
        }

        static bool ShouldUseBiRPPerEyeHook(Camera cam)
        {
            return
                GraphicsSettings.currentRenderPipeline == null &&
                ShouldUseRuntimeXr() &&
                cam != null &&
                cam.stereoEnabled &&
                cam.stereoTargetEye == StereoTargetEyeMask.Both;
        }

        internal static bool ShouldUseRuntimeXr()
        {
            return XRSettings.enabled && !Application.isEditor;
        }

        static GraphicsFormat GetSplatRenderTextureFormat(bool runtimeXr)
        {
            if (runtimeXr)
            {
                const GraphicsFormat questFormat = GraphicsFormat.R8G8B8A8_UNorm;
                if (SystemInfo.IsFormatSupported(questFormat, GraphicsFormatUsage.Render))
                    return questFormat;
            }

            return GraphicsFormat.R16G16B16A16_SFloat;
        }

        internal static void SetCompositeArrayKeyword(Material matComposite, bool useArrayTexture)
        {
            if (matComposite == null)
                return;

            if (useArrayTexture)
                matComposite.EnableKeyword(kCompositeArrayKeyword);
            else
                matComposite.DisableKeyword(kCompositeArrayKeyword);
        }
    }

    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        internal const int kRuntimeQuestMaxShOrder = 2;
        internal const int kRuntimeQuestMaxRenderedSplats = 500000;
        internal const float kRuntimeQuestFragmentAlphaCutoff = 5.0f / 255.0f;
        internal const float kRuntimeQuestMinSplatAxisPx = 0.5f;

        public enum RenderMode
        {
            Splats,
            DebugPoints,
            DebugPointIndices,
            DebugBoxes,
            DebugChunkBounds,
        }
        public GaussianSplatAsset m_Asset;

        [Tooltip("Rendering order compared to other splats. Within same order splats are sorted by distance. Higher order splats render 'on top of' lower order splats.")]
        public int m_RenderOrder;
        [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;
        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        public int m_SortNthFrame = 20;
        [Tooltip("Quest/runtime only: cull the one-asset splat by chunk bounds before sorting and drawing.")]
        public bool m_EnableRuntimeChunkSubsetCulling = true;
        [Range(0.0f, 2.0f)] [Tooltip("Extra padding added to chunk bounds during runtime chunk culling.")]
        public float m_RuntimeChunkCullPadding = 0.35f;

        public RenderMode m_RenderMode = RenderMode.Splats;
        [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;

        public GaussianCutout[] m_Cutouts;

        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public Shader m_ShaderDebugPoints;
        public Shader m_ShaderDebugBoxes;
        [Tooltip("Gaussian splatting compute shader")]
        public ComputeShader m_CSSplatUtilities;

        int m_SplatCount;
        GraphicsBuffer m_GpuSortDistances;
        internal GraphicsBuffer m_GpuSortKeys;
        GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        Texture m_GpuColorData;
        internal GraphicsBuffer m_GpuChunks;
        internal bool m_GpuChunksValid;
        internal GraphicsBuffer m_GpuView;
        internal GraphicsBuffer m_GpuIndexBuffer;

        GraphicsBuffer m_GpuEditCutouts;
        GraphicsBuffer m_GpuEditCountsBounds;
        GraphicsBuffer m_GpuEditSelected;
        GraphicsBuffer m_GpuEditDeleted;
        GraphicsBuffer m_GpuEditSelectedMouseDown;
        GraphicsBuffer m_GpuEditPosMouseDown;
        GraphicsBuffer m_GpuEditOtherMouseDown;

        GpuSorting m_Sorter;
        GpuSorting.Args m_SorterArgs;

        Bounds[] m_LocalChunkBounds;
        Bounds m_LocalChunkBoundsUnion;
        bool m_HasLocalChunkBoundsUnion;
        uint[] m_VisibleSplatIndicesCpu;
        int m_VisibleSplatCount;
        int m_LastVisibilityFrame = -1;

        internal Material m_MatSplats;
        internal Material m_MatComposite;
        internal Material m_MatDebugPoints;
        internal Material m_MatDebugBoxes;

        internal int m_FrameCounter;

        GaussianSplatAsset m_PrevAsset;
        Hash128 m_PrevHash;
        bool m_Registered;

        static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort");

        internal static class Props
        {
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
            public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
            public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
            public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
            public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
            public static readonly int EyeIndex = Shader.PropertyToID("_EyeIndex");
            public static readonly int IsStereo = Shader.PropertyToID("_IsStereo");
            public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int CameraTargetTexture = Shader.PropertyToID("_CameraTargetTexture");
            public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
            public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
            public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
            public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
            public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
            public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
            public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
            public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");
            public static readonly int ViewProjMatrixLeft = Shader.PropertyToID("_ViewProjMatrixLeft");
            public static readonly int ViewProjMatrixRight = Shader.PropertyToID("_ViewProjMatrixRight");
            public static readonly int UseOrderBufferSubset = Shader.PropertyToID("_UseOrderBufferSubset");
            public static readonly int FragmentAlphaCutoff = Shader.PropertyToID("_FragmentAlphaCutoff");
            public static readonly int MinSplatAxisPx = Shader.PropertyToID("_MinSplatAxisPx");
        }

        [field: NonSerialized] public bool editModified { get; private set; }
        [field: NonSerialized] public uint editSelectedSplats { get; private set; }
        [field: NonSerialized] public uint editDeletedSplats { get; private set; }
        [field: NonSerialized] public uint editCutSplats { get; private set; }
        [field: NonSerialized] public Bounds editSelectedBounds { get; private set; }

        public GaussianSplatAsset asset => m_Asset;
        public int splatCount => m_SplatCount;

        enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
            UpdateEditData,
            InitEditData,
            ClearBuffer,
            InvertSelection,
            SelectAll,
            OrBuffers,
            SelectionUpdate,
            TranslateSelection,
            RotateSelection,
            ScaleSelection,
            ExportData,
            CopySplats,
        }

        int[] m_UtilsKernelIndices;
        bool m_UtilsKernelIndicesInitialized;

        static string GetKernelName(KernelIndices kernel)
        {
            switch (kernel)
            {
                case KernelIndices.SetIndices: return "CSSetIndices";
                case KernelIndices.CalcDistances: return "CSCalcDistances";
                case KernelIndices.CalcViewData: return "CSCalcViewData";
                case KernelIndices.UpdateEditData: return "CSUpdateEditData";
                case KernelIndices.InitEditData: return "CSInitEditData";
                case KernelIndices.ClearBuffer: return "CSClearBuffer";
                case KernelIndices.InvertSelection: return "CSInvertSelection";
                case KernelIndices.SelectAll: return "CSSelectAll";
                case KernelIndices.OrBuffers: return "CSOrBuffers";
                case KernelIndices.SelectionUpdate: return "CSSelectionUpdate";
                case KernelIndices.TranslateSelection: return "CSTranslateSelection";
                case KernelIndices.RotateSelection: return "CSRotateSelection";
                case KernelIndices.ScaleSelection: return "CSScaleSelection";
                case KernelIndices.ExportData: return "CSExportData";
                case KernelIndices.CopySplats: return "CSCopySplats";
                default: throw new ArgumentOutOfRangeException(nameof(kernel), kernel, null);
            }
        }

        void EnsureUtilityKernelIndices()
        {
            if (m_UtilsKernelIndicesInitialized && m_UtilsKernelIndices != null)
                return;

            int kernelCount = (int)KernelIndices.CopySplats + 1;
            if (m_UtilsKernelIndices == null || m_UtilsKernelIndices.Length != kernelCount)
                m_UtilsKernelIndices = new int[kernelCount];

            for (int i = 0; i < kernelCount; ++i)
            {
                var kernel = (KernelIndices)i;
                m_UtilsKernelIndices[i] = m_CSSplatUtilities.FindKernel(GetKernelName(kernel));
            }

            m_UtilsKernelIndicesInitialized = true;
        }

        int GetKernelIndex(KernelIndices kernel)
        {
            EnsureUtilityKernelIndices();
            return m_UtilsKernelIndices[(int)kernel];
        }

        public bool HasValidAsset =>
            m_Asset != null &&
            m_Asset.splatCount > 0 &&
            m_Asset.formatVersion == GaussianSplatAsset.kCurrentVersion &&
            m_Asset.posData != null &&
            m_Asset.otherData != null &&
            m_Asset.shData != null &&
            m_Asset.colorData != null;
        public bool HasValidRenderSetup => m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null;

        const int kGpuViewDataSize = 40;
        const int kUtilityKernelGroupSize = 1024;

        void CreateResourcesForAsset()
        {
            if (!HasValidAsset)
                return;

            m_SplatCount = asset.splatCount;
            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
            m_GpuPosData.SetData(asset.posData.GetData<uint>());
            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
            m_GpuOtherData.SetData(asset.otherData.GetData<uint>());
            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)(asset.shData.dataSize / 4), 4) { name = "GaussianSHData" };
            m_GpuSHData.SetData(asset.shData.GetData<uint>());
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(asset.splatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
            tex.Apply(false, true);
            m_GpuColorData = tex;
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int)(asset.chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) { name = "GaussianChunkData" };
                m_GpuChunks.SetData(asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
                m_GpuChunksValid = true;
            }
            else
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) { name = "GaussianChunkData" };
                m_GpuChunksValid = false;
            }

            m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Asset.splatCount * 2, kGpuViewDataSize);
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });

            InitSortBuffers(splatCount);
            CacheLocalChunkBounds();
            m_VisibleSplatCount = m_SplatCount;
            m_LastVisibilityFrame = -1;
        }

        void InitSortBuffers(int count)
        {
            m_GpuSortDistances?.Dispose();
            m_GpuSortKeys?.Dispose();
            m_SorterArgs.resources.Dispose();

            EnsureSorterAndRegister();

            m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortDistances" };
            m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortIndices" };

            int setIndicesKernel = GetKernelIndex(KernelIndices.SetIndices);
            m_CSSplatUtilities.SetBuffer(setIndicesKernel, Props.SplatSortKeys, m_GpuSortKeys);
            m_CSSplatUtilities.SetInt(Props.SplatCount, m_GpuSortDistances.count);
            m_CSSplatUtilities.Dispatch(setIndicesKernel, (m_GpuSortDistances.count + kUtilityKernelGroupSize - 1) / kUtilityKernelGroupSize, 1, 1);

            m_SorterArgs.inputKeys = m_GpuSortDistances;
            m_SorterArgs.inputValues = m_GpuSortKeys;
            m_SorterArgs.count = (uint)count;
            if (m_Sorter.Valid)
                m_SorterArgs.resources = GpuSorting.SupportResources.Load((uint)count);
        }

        bool resourcesAreSetUp => m_ShaderSplats != null && m_ShaderComposite != null && m_ShaderDebugPoints != null &&
                                  m_ShaderDebugBoxes != null && m_CSSplatUtilities != null && SystemInfo.supportsComputeShaders;

        public void EnsureMaterials()
        {
            if (m_MatSplats == null && resourcesAreSetUp)
            {
                m_MatSplats      = new Material(m_ShaderSplats)      { name = "GaussianSplats" };
                m_MatComposite   = new Material(m_ShaderComposite)   { name = "GaussianClearDstAlpha" };
                m_MatDebugPoints = new Material(m_ShaderDebugPoints) { name = "GaussianDebugPoints" };
                m_MatDebugBoxes  = new Material(m_ShaderDebugBoxes)  { name = "GaussianDebugBoxes" };
            }
        }

        public void EnsureSorterAndRegister()
        {
            if (m_Sorter == null && resourcesAreSetUp)
            {
                m_Sorter = new GpuSorting(m_CSSplatUtilities);
            }

            if (!m_Registered && resourcesAreSetUp)
            {
                GaussianSplatRenderSystem.instance.RegisterSplat(this);
                m_Registered = true;
            }
        }

        public void OnEnable()
        {
            m_FrameCounter = 0;
            if (!resourcesAreSetUp)
                return;

            EnsureMaterials();
            EnsureSorterAndRegister();

            CreateResourcesForAsset();
        }

        void SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
        {
            ComputeShader cs = m_CSSplatUtilities;
            int kernelIndex = GetKernelIndex(kernel);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, m_GpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, m_GpuView);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, m_GpuSortKeys);

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            UpdateCutoutsBuffer();
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, m_Cutouts?.Length ?? 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, m_GpuEditCutouts);
        }

        internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
        {
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetTexture(Props.SplatColor, m_GpuColorData);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            mat.SetInteger(Props.SplatFormat, (int)format);
            mat.SetInteger(Props.SplatCount, m_SplatCount);
            mat.SetInteger(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
        }

        void CacheLocalChunkBounds()
        {
            m_LocalChunkBounds = null;
            m_HasLocalChunkBoundsUnion = false;
            m_LocalChunkBoundsUnion = default;

            if (asset == null || asset.chunkData == null || asset.chunkData.dataSize == 0)
                return;

            var chunks = asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>();
            if (chunks.Length == 0)
                return;

            m_LocalChunkBounds = new Bounds[chunks.Length];
            for (int i = 0; i < chunks.Length; ++i)
            {
                var chunk = chunks[i];
                Vector3 min = new Vector3(chunk.posX.x, chunk.posY.x, chunk.posZ.x);
                Vector3 max = new Vector3(chunk.posX.y, chunk.posY.y, chunk.posZ.y);
                Bounds bounds = default;
                bounds.SetMinMax(min, max);
                m_LocalChunkBounds[i] = bounds;
                if (!m_HasLocalChunkBoundsUnion)
                {
                    m_LocalChunkBoundsUnion = bounds;
                    m_HasLocalChunkBoundsUnion = true;
                }
                else
                {
                    m_LocalChunkBoundsUnion.Encapsulate(bounds.min);
                    m_LocalChunkBoundsUnion.Encapsulate(bounds.max);
                }
            }

            if (m_VisibleSplatIndicesCpu == null || m_VisibleSplatIndicesCpu.Length < m_SplatCount)
                m_VisibleSplatIndicesCpu = new uint[m_SplatCount];
        }

        static Bounds TransformBounds(Matrix4x4 localToWorld, Bounds localBounds)
        {
            Vector3 center = localToWorld.MultiplyPoint3x4(localBounds.center);
            Vector3 extents = localBounds.extents;
            Vector3 axisX = localToWorld.MultiplyVector(new Vector3(extents.x, 0f, 0f));
            Vector3 axisY = localToWorld.MultiplyVector(new Vector3(0f, extents.y, 0f));
            Vector3 axisZ = localToWorld.MultiplyVector(new Vector3(0f, 0f, extents.z));
            Vector3 worldExtents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(center, worldExtents * 2f);
        }

        static Matrix4x4 GetCameraCullMatrix(Camera cam)
        {
            Matrix4x4 viewMatrix = GetCameraViewMatrix(cam);
            Matrix4x4 projMatrix = TryGetActiveStereoEye(cam, out var eye)
                ? cam.GetStereoProjectionMatrix(eye)
                : cam.projectionMatrix;
            return projMatrix * viewMatrix;
        }

        void FillVisibleSplatIndicesWithAllSplats()
        {
            if (m_VisibleSplatIndicesCpu == null || m_VisibleSplatIndicesCpu.Length < m_SplatCount)
                m_VisibleSplatIndicesCpu = new uint[m_SplatCount];

            for (int i = 0; i < m_SplatCount; ++i)
                m_VisibleSplatIndicesCpu[i] = (uint)i;

            m_VisibleSplatCount = m_SplatCount;
            if (m_GpuSortKeys != null && m_SplatCount > 0)
                m_GpuSortKeys.SetData(m_VisibleSplatIndicesCpu, 0, 0, m_SplatCount);
        }

        bool ShouldUseRuntimeChunkSubsetCulling(Camera cam)
        {
            return false;
        }

        bool UpdateRuntimeVisibleSubset(Camera cam)
        {
            if (!ShouldUseRuntimeChunkSubsetCulling(cam))
            {
                if (m_VisibleSplatCount != m_SplatCount)
                    FillVisibleSplatIndicesWithAllSplats();
                else
                    m_VisibleSplatCount = m_SplatCount;
                return false;
            }

            if (m_LastVisibilityFrame == Time.frameCount)
                return true;

            m_LastVisibilityFrame = Time.frameCount;

            if (m_VisibleSplatIndicesCpu == null || m_VisibleSplatIndicesCpu.Length < m_SplatCount)
                m_VisibleSplatIndicesCpu = new uint[m_SplatCount];

            Plane[] frustumPlanes = GeometryUtility.CalculateFrustumPlanes(GetCameraCullMatrix(cam));
            Matrix4x4 localToWorld = transform.localToWorldMatrix;
            float padding = m_RuntimeChunkCullPadding * 2f;
            int visibleCount = 0;

            for (int chunkIdx = 0; chunkIdx < m_LocalChunkBounds.Length; ++chunkIdx)
            {
                Bounds worldBounds = TransformBounds(localToWorld, m_LocalChunkBounds[chunkIdx]);
                if (padding > 0f)
                    worldBounds.Expand(padding);
                if (!GeometryUtility.TestPlanesAABB(frustumPlanes, worldBounds))
                    continue;

                int splatStart = chunkIdx * GaussianSplatAsset.kChunkSize;
                int splatEnd = Mathf.Min(splatStart + GaussianSplatAsset.kChunkSize, m_SplatCount);
                for (int splatIdx = splatStart; splatIdx < splatEnd; ++splatIdx)
                    m_VisibleSplatIndicesCpu[visibleCount++] = (uint)splatIdx;
            }

            if (visibleCount == 0 && m_HasLocalChunkBoundsUnion)
            {
                Bounds worldUnion = TransformBounds(localToWorld, m_LocalChunkBoundsUnion);
                if (padding > 0f)
                    worldUnion.Expand(padding);
                if (GeometryUtility.TestPlanesAABB(frustumPlanes, worldUnion))
                {
                    FillVisibleSplatIndicesWithAllSplats();
                    return true;
                }
            }

            if (visibleCount == 0)
            {
                FillVisibleSplatIndicesWithAllSplats();
                return true;
            }

            m_VisibleSplatCount = visibleCount;
            if (visibleCount > 0)
                m_GpuSortKeys.SetData(m_VisibleSplatIndicesCpu, 0, 0, visibleCount);
            return true;
        }

        int GetActiveSplatCount(Camera cam)
        {
            return ShouldUseRuntimeChunkSubsetCulling(cam) ? m_VisibleSplatCount : m_SplatCount;
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        void DisposeResourcesForAsset()
        {
            DestroyImmediate(m_GpuColorData);

            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);
            DisposeBuffer(ref m_GpuChunks);

            DisposeBuffer(ref m_GpuView);
            DisposeBuffer(ref m_GpuIndexBuffer);
            DisposeBuffer(ref m_GpuSortDistances);
            DisposeBuffer(ref m_GpuSortKeys);

            DisposeBuffer(ref m_GpuEditSelectedMouseDown);
            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);
            DisposeBuffer(ref m_GpuEditSelected);
            DisposeBuffer(ref m_GpuEditDeleted);
            DisposeBuffer(ref m_GpuEditCountsBounds);
            DisposeBuffer(ref m_GpuEditCutouts);

            m_SorterArgs.resources.Dispose();

            m_SplatCount = 0;
            m_GpuChunksValid = false;
            m_LocalChunkBounds = null;
            m_HasLocalChunkBoundsUnion = false;
            m_LocalChunkBoundsUnion = default;
            m_VisibleSplatIndicesCpu = null;
            m_VisibleSplatCount = 0;
            m_LastVisibilityFrame = -1;

            editSelectedSplats = 0;
            editDeletedSplats = 0;
            editCutSplats = 0;
            editModified = false;
            editSelectedBounds = default;
        }

        public void OnDisable()
        {
            DisposeResourcesForAsset();
            GaussianSplatRenderSystem.instance.UnregisterSplat(this);
            m_Registered = false;

            DestroyImmediate(m_MatSplats);
            DestroyImmediate(m_MatComposite);
            DestroyImmediate(m_MatDebugPoints);
            DestroyImmediate(m_MatDebugBoxes);
        }

        internal void CalcViewData(CommandBuffer cmb, Camera cam)
        {
            if (cam.cameraType == CameraType.Preview) return;

            var tr = transform;
            Matrix4x4 matView = GetCameraViewMatrix(cam);
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            GaussianSplatRenderSystem.GetCameraRenderSize(cam, out int rtW, out int rtH);

            Vector4 screenPar = new Vector4(rtW, rtH, 0, 0);
            Vector4 camPos = matView.inverse.GetColumn(3);
            bool isSinglePassStereo =
                GraphicsSettings.currentRenderPipeline != null &&
                GaussianSplatRenderSystem.IsSinglePassStereoCamera(cam);

            SetAssetDataOnCS(cmb, KernelIndices.CalcViewData);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);
            if (isSinglePassStereo)
            {
                Matrix4x4 stereoViewLeft = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
                Matrix4x4 stereoProjLeft = GL.GetGPUProjectionMatrix(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left), true);
                Matrix4x4 stereoViewRight = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right);
                Matrix4x4 stereoProjRight = GL.GetGPUProjectionMatrix(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right), true);

                cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.ViewProjMatrixLeft, stereoProjLeft * stereoViewLeft);
                cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.ViewProjMatrixRight, stereoProjRight * stereoViewRight);
                cmb.SetComputeIntParam(m_CSSplatUtilities, Props.IsStereo, 1);
            }
            else
            {
                cmb.SetComputeIntParam(m_CSSplatUtilities, Props.IsStereo, 0);
            }
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatOpacityScale, m_OpacityScale);
            int runtimeShOrder = GaussianSplatRenderSystem.ShouldUseRuntimeXr() ? Mathf.Min(m_SHOrder, kRuntimeQuestMaxShOrder) : m_SHOrder;
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOrder, runtimeShOrder);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOnly, m_SHOnly ? 1 : 0);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.UseOrderBufferSubset, 0);

            int calcViewDataKernel = GetKernelIndex(KernelIndices.CalcViewData);
            cmb.DispatchCompute(m_CSSplatUtilities, calcViewDataKernel, (m_SplatCount + kUtilityKernelGroupSize - 1) / kUtilityKernelGroupSize, 1, 1);
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            Matrix4x4 worldToCamMatrix = GetCameraViewMatrix(cam);
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            cmd.BeginSample(s_ProfSort);
            int calcDistancesKernel = GetKernelIndex(KernelIndices.CalcDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, calcDistancesKernel, Props.SplatSortDistances, m_GpuSortDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, calcDistancesKernel, Props.SplatSortKeys, m_GpuSortKeys);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, calcDistancesKernel, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, calcDistancesKernel, Props.SplatPos, m_GpuPosData);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)m_Asset.posFormat);
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            cmd.DispatchCompute(m_CSSplatUtilities, calcDistancesKernel, (m_GpuSortDistances.count + kUtilityKernelGroupSize - 1) / kUtilityKernelGroupSize, 1, 1);

            EnsureSorterAndRegister();
            if (m_Sorter.Valid)
                m_Sorter.Dispatch(cmd, m_SorterArgs);
            cmd.EndSample(s_ProfSort);
        }

        static Matrix4x4 GetCameraViewMatrix(Camera cam)
        {
            if (TryGetActiveStereoEye(cam, out var eye))
                return cam.GetStereoViewMatrix(eye);
            return cam.worldToCameraMatrix;
        }

        static bool TryGetActiveStereoEye(Camera cam, out Camera.StereoscopicEye eye)
        {
            eye = Camera.StereoscopicEye.Left;
            if (cam == null || !GaussianSplatRenderSystem.ShouldUseRuntimeXr() || !cam.stereoEnabled)
                return false;

            switch (cam.stereoActiveEye)
            {
                case Camera.MonoOrStereoscopicEye.Left:
                    eye = Camera.StereoscopicEye.Left;
                    return true;
                case Camera.MonoOrStereoscopicEye.Right:
                    eye = Camera.StereoscopicEye.Right;
                    return true;
                default:
                    return false;
            }
        }

        public void Update()
        {
            var curHash = m_Asset ? m_Asset.dataHash : new Hash128();
            if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
            {
                m_PrevAsset = m_Asset;
                m_PrevHash = curHash;
                if (resourcesAreSetUp)
                {
                    DisposeResourcesForAsset();
                    CreateResourcesForAsset();
                }
                else
                {
                    Debug.LogError($"{nameof(GaussianSplatRenderer)} component is not set up correctly (Resource references are missing), or platform does not support compute shaders");
                }
            }
        }

        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (!m_Asset || m_Asset.cameras == null)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = m_Asset.cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.pos;
            camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }

        void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            int clearBufferKernel = GetKernelIndex(KernelIndices.ClearBuffer);
            m_CSSplatUtilities.SetBuffer(clearBufferKernel, Props.DstBuffer, buf);
            m_CSSplatUtilities.SetInt(Props.BufferSize, buf.count);
            m_CSSplatUtilities.Dispatch(clearBufferKernel, (buf.count + kUtilityKernelGroupSize - 1) / kUtilityKernelGroupSize, 1, 1);
        }

        void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            int orBuffersKernel = GetKernelIndex(KernelIndices.OrBuffers);
            m_CSSplatUtilities.SetBuffer(orBuffersKernel, Props.SrcBuffer, src);
            m_CSSplatUtilities.SetBuffer(orBuffersKernel, Props.DstBuffer, dst);
            m_CSSplatUtilities.SetInt(Props.BufferSize, dst.count);
            m_CSSplatUtilities.Dispatch(orBuffersKernel, (dst.count + kUtilityKernelGroupSize - 1) / kUtilityKernelGroupSize, 1, 1);
        }

        static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        public void UpdateEditCountsAndBounds()
        {
            if (m_GpuEditSelected == null)
            {
                editSelectedSplats = 0;
                editDeletedSplats = 0;
                editCutSplats = 0;
                editModified = false;
                editSelectedBounds = default;
                return;
            }

            int initEditDataKernel = GetKernelIndex(KernelIndices.InitEditData);
            m_CSSplatUtilities.SetBuffer(initEditDataKernel, Props.DstBuffer, m_GpuEditCountsBounds);
            m_CSSplatUtilities.Dispatch(initEditDataKernel, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            SetAssetDataOnCS(cmb, KernelIndices.UpdateEditData);
            int updateEditDataKernel = GetKernelIndex(KernelIndices.UpdateEditData);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, updateEditDataKernel, Props.DstBuffer, m_GpuEditCountsBounds);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            cmb.DispatchCompute(m_CSSplatUtilities, updateEditDataKernel, (m_GpuEditSelected.count + kUtilityKernelGroupSize - 1) / kUtilityKernelGroupSize, 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[m_GpuEditCountsBounds.count];
            m_GpuEditCountsBounds.GetData(res);
            editSelectedSplats = res[0];
            editDeletedSplats = res[1];
            editCutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f, 0.1f, 0.1f);
            editSelectedBounds = bounds;
        }

        void UpdateCutoutsBuffer()
        {
            int bufferSize = m_Cutouts?.Length ?? 0;
            if (bufferSize == 0)
                bufferSize = 1;
            if (m_GpuEditCutouts == null || m_GpuEditCutouts.count != bufferSize)
            {
                m_GpuEditCutouts?.Dispose();
                m_GpuEditCutouts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, UnsafeUtility.SizeOf<GaussianCutout.ShaderData>()) { name = "GaussianCutouts" };
            }

            NativeArray<GaussianCutout.ShaderData> data = new(bufferSize, Allocator.Temp);
            if (m_Cutouts != null)
            {
                var matrix = transform.localToWorldMatrix;
                for (var i = 0; i < m_Cutouts.Length; ++i)
                {
                    data[i] = GaussianCutout.GetShaderData(m_Cutouts[i], matrix);
                }
            }

            m_GpuEditCutouts.SetData(data);
            data.Dispose();
        }

        bool EnsureEditingBuffers()
        {
            if (!HasValidAsset || !HasValidRenderSetup)
                return false;

            if (m_GpuEditSelected == null)
            {
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (m_SplatCount + 31) / 32;
                m_GpuEditSelected = new GraphicsBuffer(target, size, 4) { name = "GaussianSplatSelected" };
                m_GpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) { name = "GaussianSplatSelectedInit" };
                m_GpuEditDeleted = new GraphicsBuffer(target, size, 4) { name = "GaussianSplatDeleted" };
                m_GpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) { name = "GaussianSplatEditData" };
                ClearGraphicsBuffer(m_GpuEditSelected);
                ClearGraphicsBuffer(m_GpuEditSelectedMouseDown);
                ClearGraphicsBuffer(m_GpuEditDeleted);
            }
            return m_GpuEditSelected != null;
        }

        public void EditStoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(m_GpuEditSelected, m_GpuEditSelectedMouseDown);
        }

        public void EditStorePosMouseDown()
        {
            if (m_GpuEditPosMouseDown == null)
            {
                m_GpuEditPosMouseDown = new GraphicsBuffer(m_GpuPosData.target | GraphicsBuffer.Target.CopyDestination, m_GpuPosData.count, m_GpuPosData.stride) { name = "GaussianSplatEditPosMouseDown" };
            }
            Graphics.CopyBuffer(m_GpuPosData, m_GpuEditPosMouseDown);
        }

        public void EditStoreOtherMouseDown()
        {
            if (m_GpuEditOtherMouseDown == null)
            {
                m_GpuEditOtherMouseDown = new GraphicsBuffer(m_GpuOtherData.target | GraphicsBuffer.Target.CopyDestination, m_GpuOtherData.count, m_GpuOtherData.stride) { name = "GaussianSplatEditOtherMouseDown" };
            }
            Graphics.CopyBuffer(m_GpuOtherData, m_GpuEditOtherMouseDown);
        }

        public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(m_GpuEditSelectedMouseDown, m_GpuEditSelected);

            var tr = transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectionUpdate);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_SelectionRect", new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SelectionMode, subtract ? 0 : 1);

            DispatchUtilsAndExecute(cmb, KernelIndices.SelectionUpdate, m_SplatCount);
            UpdateEditCountsAndBounds();
        }

        public void EditTranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.TranslateSelection);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, localSpacePosDelta);

            DispatchUtilsAndExecute(cmb, KernelIndices.TranslateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditRotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null || m_GpuEditOtherMouseDown == null) return;

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.RotateSelection);

            int rotateSelectionKernel = GetKernelIndex(KernelIndices.RotateSelection);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, rotateSelectionKernel, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, rotateSelectionKernel, Props.SplatOtherMouseDown, m_GpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            DispatchUtilsAndExecute(cmb, KernelIndices.RotateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null) return;

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.ScaleSelection);

            int scaleSelectionKernel = GetKernelIndex(KernelIndices.ScaleSelection);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, scaleSelectionKernel, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, scale);

            DispatchUtilsAndExecute(cmb, KernelIndices.ScaleSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditDeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(m_GpuEditDeleted, m_GpuEditSelected);
            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        public void EditSelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectAll);
            int selectAllKernel = GetKernelIndex(KernelIndices.SelectAll);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, selectAllKernel, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.SelectAll, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public void EditDeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(m_GpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        public void EditInvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.InvertSelection);
            int invertSelectionKernel = GetKernelIndex(KernelIndices.InvertSelection);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, invertSelectionKernel, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.InvertSelection, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            SetAssetDataOnCS(cmb, KernelIndices.ExportData);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            int exportDataKernel = GetKernelIndex(KernelIndices.ExportData);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, exportDataKernel, "_ExportBuffer", dstData);

            DispatchUtilsAndExecute(cmb, KernelIndices.ExportData, m_SplatCount);
            return true;
        }

        public void EditSetSplatCount(int newSplatCount)
        {
            if (newSplatCount <= 0 || newSplatCount > GaussianSplatAsset.kMaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (asset.chunkData != null)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == splatCount)
                return;

            int posStride = (int)(asset.posData.dataSize / asset.splatCount);
            int otherStride = (int)(asset.otherData.dataSize / asset.splatCount);
            int shStride = (int)(asset.shData.dataSize / asset.splatCount);

            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(newSplatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) { name = "GaussianSplatSelected" };
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) { name = "GaussianSplatSelectedInit" };
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) { name = "GaussianSplatDeleted" };
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            var newGpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newSplatCount * 2, kGpuViewDataSize);
            InitSortBuffers(newSplatCount);

            EditCopySplats(transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, m_SplatCount);

            m_GpuPosData.Dispose();
            m_GpuOtherData.Dispose();
            m_GpuSHData.Dispose();
            DestroyImmediate(m_GpuColorData);
            m_GpuView.Dispose();

            m_GpuEditSelected?.Dispose();
            m_GpuEditSelectedMouseDown?.Dispose();
            m_GpuEditDeleted?.Dispose();

            m_GpuPosData = newPosData;
            m_GpuOtherData = newOtherData;
            m_GpuSHData = newSHData;
            m_GpuColorData = newColorData;
            m_GpuView = newGpuView;
            m_GpuEditSelected = newEditSelected;
            m_GpuEditSelectedMouseDown = newEditSelectedMouseDown;
            m_GpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);

            m_SplatCount = newSplatCount;
            editModified = true;
        }

        public void EditCopySplatsInto(GaussianSplatRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            EditCopySplats(
                dst.transform,
                dst.m_GpuPosData, dst.m_GpuOtherData, dst.m_GpuSHData, dst.m_GpuColorData, dst.m_GpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            dst.editModified = true;
        }

        public void EditCopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            SetAssetDataOnCS(cmb, KernelIndices.CopySplats);

            int copySplatsKernel = GetKernelIndex(KernelIndices.CopySplats);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, copySplatsKernel, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, copySplatsKernel, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, copySplatsKernel, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(m_CSSplatUtilities, copySplatsKernel, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, copySplatsKernel, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, KernelIndices.CopySplats, copyCount);
        }

        void DispatchUtilsAndExecute(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            int kernelIndex = GetKernelIndex(kernel);
            cmb.DispatchCompute(m_CSSplatUtilities, kernelIndex, (count + kUtilityKernelGroupSize - 1) / kUtilityKernelGroupSize, 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
        }

        public GraphicsBuffer GpuEditDeleted => m_GpuEditDeleted;
    }
}
