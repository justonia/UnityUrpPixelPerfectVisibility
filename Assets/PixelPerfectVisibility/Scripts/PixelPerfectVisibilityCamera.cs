// MIT License
//
// Copyright (c) 2023 Justin Larrabee <justonia@gmail.com>
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//
// ----
//
// “Commons Clause” License Condition v1.0
//
// The Software is provided to you by the Licensor under the License, as defined
// above, subject to the following condition.
//
// Without limiting other conditions in the License, the grant of rights under
// the License will not include, and the License does not grant to you, the
// right to Sell the Software.
//
// For purposes of the foregoing, “Sell” means practicing any or all of the rights
// granted to you under the License to provide to third parties, for a fee or other
// consideration (including without limitation fees for hosting or consulting/support
// services related to the Software), a product or service whose value derives,
// entirely or substantially, from the functionality of the Software. Any license
// notice or attribution required by the License must also include this Commons Clause
// License Condition notice.

using System.Runtime.InteropServices;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.Universal;
using Unity.Collections;
using Unity.Burst;
using Unity.Jobs;

namespace PixelPerfectVisibility
{
    // PixelPerfectVisibilityCamera - Implements pixel perfect object visibility
    // detection through the use of an off-screen render target.
    //
    // Notes:
    // - Can have >1 of these active at once if you wish.
    //
    // - There are 1+ frames of latency before visibility updates. This implementation
    //   depends on reading back data from the GPU asynchronously so as not to add
    //   GPU stalls during rendering.
    //
    // - Performance characteristics:
    //    - 16 bit R8G8 render target, 32 bit depth/stencil at full/half/or quarter res
    //      of screen resolution.
    //    - Mobile? Maybe? There are no high bandwidth ops happening and the shaders
    //      are dead simple. Draw calls will increase due to needing to render scene
    //      opaques. Ensure that the QualityMode is as low as possible though.
    //    - All renderers that aren't candidates for pixel detection are aggressively
    //      batched by the SRP and rendered using a replacement material.
    //    - All renderers that are candidates for pixel detection require one draw
    //      call each and are rendered with a replacement shader. Unfortunately
    //      URP 14 and 15 do not yet support SRP batching for replacement shaders. :(
    //    - A potential enhancement would be to move the CountPixels job to a
    //      compute shader.
    //
    // - It's entirely up to the game to decide what the ideal script update order is.
    //   Later in the frame will give more time for the CountPixels job to execute in the
    //   background while the main thread continues, since the async GPU callbacks
    //   happen in the EarlyUpdate phase of the PlayerLoop. Earlier in the frame
    //   will reduce latency if component update logic depending on visibility
    //   needs it, at the cost of less efficient CPU usage.
    //
    [DefaultExecutionOrder(5000)]
    public class PixelPerfectVisibilityCamera : MonoBehaviour
    {
        private static List<PixelPerfectVisibilityRenderer> targetRenderers = new List<PixelPerfectVisibilityRenderer>();
        private readonly int objectIdParam = Shader.PropertyToID("_PixelPerfectSelectionObjectId");
        private static List<PixelPerfectVisibilityCamera> cameras = new List<PixelPerfectVisibilityCamera>();

        // Support no-domain reload correctly
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            targetRenderers = new List<PixelPerfectVisibilityRenderer>();
            cameras = new List<PixelPerfectVisibilityCamera>();
        }

        private int resScale = 1;
        private const int stride = 2; // R8G8_UNorm
        private RenderTexture rt;
        private Camera pixelCamera;
        private int pixelPerfectVisibilityLayer;

        // Persistent buffer used for reading RenderTexture contents back async.
        private NativeArray<byte> rtContents;

        // Last rendered targets are the renderers that were used in the most recently
        // finished visibility test.
        private List<PixelPerfectVisibilityRenderer> lastRenderedTargets = new List<PixelPerfectVisibilityRenderer>();
        private Dictionary<PixelPerfectVisibilityRenderer, float> visibility = new Dictionary<PixelPerfectVisibilityRenderer, float>();

        // Last copy of the rtContents NativeArray
        private NativeArray<byte> rtContentsCopy;

        // Pending targets are the renderers that are currently being rendered
        // to test for visibility.
        private List<PixelPerfectVisibilityRenderer> pendingTargets = new List<PixelPerfectVisibilityRenderer>();
        private bool pendingGpuRead;
        private bool disabling;
        private JobHandle countJob;
        private bool scheduledCountJob;
        private NativeArray<int> objectPixels; // temp, no need to dispose

        #region Serialized Fields

        public enum QualityMode
        {
            Full,
            Half,
            Quarter,
        }

        [Tooltip("Which camera should be used as the reference for culling masks, FoV, etc")]
        public Camera ReferenceCamera;

        [Tooltip("What % of the camera resolution should be used. It's highly recommended to go as low as possible until accuracy isn't high enough.")]
        public QualityMode Quality = QualityMode.Half;

        #endregion

        #region Public API

        public static PixelPerfectVisibilityCamera main
        {
            get {
                var mainCam = Camera.main;
                foreach (var pixelCamera in cameras) {
                    if (pixelCamera.ReferenceCamera == mainCam) {
                        return pixelCamera;
                    }
                }
                return null;
            }
        }

        public event System.Action OnVisibilityUpdated;

        public static IReadOnlyList<PixelPerfectVisibilityRenderer> Renderers => targetRenderers;

        public static void RegisterTargetRenderer(PixelPerfectVisibilityRenderer r)
        {
            if (!targetRenderers.Contains(r)) {
                targetRenderers.Add(r);
            }
        }

        public static void DeregisterTargetRenderer(PixelPerfectVisibilityRenderer r)
        {
            targetRenderers.Remove(r);
        }

        public bool IsVisible(Renderer renderer)
        {
            // At some point could get smarter and recurse up the hierarchy.
            return IsVisible(renderer.GetComponent<PixelPerfectVisibilityRenderer>());
        }

        public bool IsVisible(PixelPerfectVisibilityRenderer renderer)
        {
            if (renderer == null) {
                return false;
            }

            return visibility.TryGetValue(renderer, out var amount) && amount > 0f;
        }

        public bool TryGetVisiblity(PixelPerfectVisibilityRenderer renderer, out bool isVisible, out float pixelCoveragePercent)
        {
            isVisible = false;
            pixelCoveragePercent = 0f;

            if (renderer == null) {
                return false;
            }

            if (visibility.TryGetValue(renderer, out var amount)) {
                pixelCoveragePercent = amount;
                isVisible = amount > 0f;
                return true;
            }

            return false;
        }

        public PixelPerfectVisibilityRenderer GetRendererAtScreenPosition(float x, float y)
        {
            return GetRendererAtScreenPosition((int)x, (int)y);
        }

        public PixelPerfectVisibilityRenderer GetRendererAtScreenPosition(int x, int y)
        {
            if (!rtContentsCopy.IsCreated || !enabled) {
                return null;
            }

            x = (int)Mathf.Round((float)x / (float)resScale);
            y = (int)Mathf.Round((float)y / (float)resScale);

            if (x < 0 || x >= rt.width || y < 0 || y >= rt.height) {
                return null;
            }

            var idx = y * rt.width * stride + x * stride;
            var r = rtContentsCopy[idx] | rtContentsCopy[idx+1] << 8;
            if (r == 0) {
                return null;
            }

            // Check for Unity object == null
            if (lastRenderedTargets[r-1] == null) {
                return null;
            }

            return lastRenderedTargets[r-1];
        }

        #endregion

        #region Implementation
        private void Awake()
        {
            cameras.Add(this);
        }

        private void OnDestroy()
        {
            cameras.Remove(this);
        }

        private void OnEnable()
        {
            switch (Quality) {
                case QualityMode.Quarter:
                    resScale = 4;
                    break;

                case QualityMode.Half:
                    resScale = 2;
                    break;

                default:
                    resScale = 1;
                    break;
            }

            pixelCamera = GetComponent<Camera>();

            // Don't let this camera render normally, it is manually rendered in OnBeginCameraRender
            pixelCamera.enabled = false;

            pixelPerfectVisibilityLayer = LayerMask.NameToLayer("PixelPerfectVisibility");

            if (pixelPerfectVisibilityLayer < 0) {
                Debug.LogError("Missing PixelPerfectVisibility layer in Project Settings");
                enabled = false;
                return;
            }

            if (ReferenceCamera == null && transform.parent != null) {
                ReferenceCamera = transform.parent.GetComponentInParent<Camera>();
            }

            if (ReferenceCamera == null) {
                Debug.LogError("Missing ReferenceCamera");
                enabled = false;
                return;
            }
            else if (ReferenceCamera == pixelCamera) {
                Debug.LogError("PixelPerfectSelectionCamera must not be put on the reference camera GameObject");
                enabled = false;
                return;
            }

            RenderPipelineManager.beginCameraRendering += OnBeginCameraRender;

            CreateRtIfNecessary();
        }

        private void CreateRtIfNecessary()
        {
            var targetWidth = Screen.width / resScale;
            var targetHeight = Screen.height / resScale;

            // Ensure that this class can support cameras rendering off-screen as well.
            if (ReferenceCamera.targetTexture != null) {
                targetWidth = ReferenceCamera.targetTexture.width / resScale;
                targetHeight = ReferenceCamera.targetTexture.height / resScale;
            }

            if (rt != null) {
                if (rt.width == targetWidth && rt.height == targetHeight) {
                    return;
                }

                Destroy(rt);
            }

            rt = new RenderTexture(targetWidth, targetHeight, 24)
            {
                antiAliasing = 1,
                filterMode = FilterMode.Point,
                autoGenerateMips = false,
                depth = 24,
                graphicsFormat = GraphicsFormat.R8G8_UNorm,
                depthStencilFormat = GraphicsFormat.D24_UNorm_S8_UInt,
            };
            rt.Create();

            if (rtContents.IsCreated) {
                rtContents.Dispose();
            }

            rtContents = new NativeArray<byte>(rt.width * rt.height * stride, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            if (rtContentsCopy.IsCreated) {
                rtContentsCopy.Dispose();
            }

            rtContentsCopy = new NativeArray<byte>(rt.width * rt.height * stride, Allocator.Persistent);

            pixelCamera.targetTexture = rt;
        }

        private void OnDisable()
        {
            disabling = true;

            // If we don't force this to finish we can't deallocate the native array...
            // See: https://forum.unity.com/threads/asyncgpureadback-requestintonativearray-causes-invalidoperationexception-on-nativearray.1011955/page-2
            if (pendingGpuRead) {
                AsyncGPUReadback.WaitAllRequests();
                pendingGpuRead = false;
            }

            // Ensure that the job finishes so we can dispose memory correctly.
            if (scheduledCountJob) {
                countJob.Complete();
                objectPixels.Dispose();
                objectPixels = default;
                scheduledCountJob = false;
            }

            lastRenderedTargets.Clear();
            pendingTargets.Clear();

            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRender;

            if (rt != null) {
                Destroy(rt);
            }

            if (rtContents.IsCreated) {
                rtContents.Dispose();
                rtContents = default;
            }

            if (rtContentsCopy.IsCreated) {
                rtContentsCopy.Dispose();
                rtContentsCopy = default;
            }

            pixelCamera.targetTexture = null;

            disabling = false;
        }


        private void OnBeginCameraRender(ScriptableRenderContext context, Camera renderingCamera)
        {
            if (ReferenceCamera != renderingCamera || pendingGpuRead) {
                return;
            }

            CreateRtIfNecessary();
            rt.DiscardContents();

            pixelCamera.nearClipPlane = ReferenceCamera.nearClipPlane;
            pixelCamera.farClipPlane = ReferenceCamera.farClipPlane;
            pixelCamera.fieldOfView = ReferenceCamera.fieldOfView;
            pixelCamera.targetTexture = rt;
            pixelCamera.cullingMask = ReferenceCamera.cullingMask | (1 << pixelPerfectVisibilityLayer);
            pixelCamera.transform.position = ReferenceCamera.transform.position;
            pixelCamera.transform.rotation = ReferenceCamera.transform.rotation;

            // Update logic:
            // * Temporarily modify the layer of all PixelPerfectSelectionRenderer gameObjects.
            //   When URP RenderObjects pass supports rendering layers (not in URP 14 in 2022 seriously?)
            //   those can be used instead of having to hack normal layers like this.
            // * Assign each renderer an 'object ID' that will be rendered as a color into the render texture.
            // * Instruct URP to render the camera
            // * Restore layers to the state they were before first step.
            // * Queue an async GPU readback operation to access contents of the render target
            //   without adding a GPU stall (like using GetPixels would).
            // * Wait some number of frames for async OnCompleteReadback callback. This seems to happen
            //   in the EarlyUpdate part of the player loop.
            // * Schedule a Burst compiled job to count pixels for each object id in the render texture data.
            // * Let it run in the background while main thread continues.
            // * In Update(), force complete the job if it hasn't finished.
            // * Cache visibility info for each renderer and invoke OnVisibilityUpdated

            pendingTargets.Clear();

            var targetCount = targetRenderers.Count;
            if (targetCount > 65536) {
                Debug.LogError("Max target renderers are 65536, you have way too many.");
                targetCount = 65536;
            }

            var layersBefore = new NativeArray<int>(targetCount, Allocator.Temp);

            for (int i = 0; i < targetCount; i++) {
                layersBefore[i] = targetRenderers[i].gameObject.layer;
                targetRenderers[i].gameObject.layer = pixelPerfectVisibilityLayer;
                pendingTargets.Add(targetRenderers[i]);

                var objId = new UnionInt{ Value = i + 1 };

                targetRenderers[i].TargetRenderer.material.SetColor(objectIdParam, new Color32(objId.byte0, objId.byte1, 0, 255));
            }

            UniversalRenderPipeline.RenderSingleCamera(context, pixelCamera);

            // The console complains that RenderSingleCamera is obsolete and suggests this method.
            // Unfortunately you get this error and I cannot find docs explaining why:
            //   'Recursive rendering is not supported in SRP (are you calling Camera.Render from within a render pipeline?).'
            /*
            RenderPipeline.SubmitRenderRequest(pixelCamera, new UniversalRenderPipeline.SingleCameraRequest{
                destination = rt,
            });
            */

            // Restore layers
            for (int i = 0; i < targetCount; i++) {
                targetRenderers[i].gameObject.layer = layersBefore[i];
            }

            layersBefore.Dispose();

            // Queue up reading back the results asynchronously.
            pendingGpuRead = true;

            AsyncGPUReadback.RequestIntoNativeArray
                  (ref rtContents, pixelCamera.targetTexture, 0, OnCompleteReadback);
        }

        void OnCompleteReadback(AsyncGPUReadbackRequest request)
        {
            if (request.hasError) {
                Debug.Log("GPU readback error detected.");
                return;
            }

            if (disabling) {
                return;
            }

            // Still a stupid bug that you can't read from the native array in a job
            // without invalidating it for future use in the async readback api...
            rtContentsCopy.CopyFrom(rtContents);

            objectPixels = new NativeArray<int>(pendingTargets.Count + 1, Allocator.TempJob);
            if (pendingTargets.Count > 255) {
                countJob = new CountPixels2Bytes{
                    Rt = rtContentsCopy,
                    ObjectPixels = objectPixels,
                    Stride = stride,
                }.Schedule();
            }
            else {
                countJob = new CountPixels1Byte{
                    Rt = rtContentsCopy,
                    ObjectPixels = objectPixels,
                    Stride = stride,
                }.Schedule();
            }

            scheduledCountJob = true;
        }

        private void Update()
        {
            // Update order will determine where in the frame this job gets force-completed.
            // As mentioned above, have this script update as late in the frame as your game
            // will allow so the CountPixels job can run on a job thread while the main thread
            // game logic updates.
            if (scheduledCountJob) {
                countJob.Complete();

                scheduledCountJob = false;
                pendingGpuRead = false;

                FinishCountingPixels();
            }
        }

        private void FinishCountingPixels()
        {
            lastRenderedTargets.Clear();

            foreach (var target in pendingTargets) {
                // Intentional to not check null here since the array indexes need to match.
                lastRenderedTargets.Add(target);
            }

            visibility.Clear();

            var screenPixelsDiv = 1f / (float)(rtContentsCopy.Length / stride);

            for (int i = 1; i < objectPixels.Length; i++) {
                var t = pendingTargets[i-1];
                if (t != null && objectPixels[i] > 0) {
                    visibility[t] = (float)objectPixels[i] * screenPixelsDiv;
                }
            }

            objectPixels.Dispose();
            objectPixels = default;

            OnVisibilityUpdated?.Invoke();
        }

        // Trying to make this a parallel job would be a huge headache.
        [BurstCompile]
        private struct CountPixels1Byte : IJob
        {
            [ReadOnly]
            public NativeArray<byte> Rt;

            public NativeArray<int> ObjectPixels;

            public int Stride;

            public void Execute()
            {
                for (int i = 0; i < Rt.Length; i += Stride) {
                    ObjectPixels[Rt[i]] = ObjectPixels[Rt[i]] + 1;
                }
            }
        }

        [BurstCompile]
        private struct CountPixels2Bytes : IJob
        {
            [ReadOnly]
            public NativeArray<byte> Rt;

            public NativeArray<int> ObjectPixels;

            public int Stride;

            public void Execute()
            {
                for (int i = 0; i < Rt.Length; i += Stride) {
                    var id = Rt[i] | Rt[i+1] << 8;
                    ObjectPixels[id] = ObjectPixels[id] + 1;
                }
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct UnionInt
        {
            [FieldOffset(0)]
            public int Value;

            [FieldOffset(0)]
            public readonly byte byte0;

            [FieldOffset(1)]
            public readonly byte byte1;

            [FieldOffset(2)]
            public readonly byte byte2;

            [FieldOffset(3)]
            public readonly byte byte3;
        }

        #endregion
    }
}
