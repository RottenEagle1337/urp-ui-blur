using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace RottenEagle
{
public class UiBlurFeature : ScriptableRendererFeature
{
    private const string BlurShaderName = "Custom/UiBlurKawase";
    private const string PanelBlitShaderName = "Custom/UiBlurPanelBlit";

    [Range(1, 6)]
    [SerializeField] private int iterations = 4;

    [Range(1, 4)]
    [SerializeField] private int downsample = 2;

    [Range(0.5f, 6f)]
    [SerializeField] private float offset = 2.0f;

    [SerializeField] private Shader blurShader;

    [SerializeField] private Shader panelBlitShader;

    private Material runtimeBlurMaterial;
    private Material runtimePanelBlitMaterial;
    private UiBlurPass blurPass;
    private UiBlurPanelDrawPass panelDrawPass;

    public override void Create()
    {
        blurPass = new UiBlurPass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing
        };
        panelDrawPass = new UiBlurPanelDrawPass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing + 1
        };
        CreateRuntimeMaterials();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!CreateRuntimeMaterials())
        {
            return;
        }

        Camera renderingCamera = renderingData.cameraData.camera;
        if (renderingCamera.cameraType == CameraType.Preview ||
            renderingCamera.cameraType == CameraType.Reflection)
        {
            return;
        }

        blurPass.Setup(runtimeBlurMaterial, iterations, downsample, offset);
        renderer.EnqueuePass(blurPass);

        panelDrawPass.Setup(runtimePanelBlitMaterial, renderingCamera, downsample);
        renderer.EnqueuePass(panelDrawPass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(runtimeBlurMaterial);
        CoreUtils.Destroy(runtimePanelBlitMaterial);
        runtimeBlurMaterial = null;
        runtimePanelBlitMaterial = null;
        blurPass?.Dispose();
        panelDrawPass?.Dispose();
        base.Dispose(disposing);
    }

    private void OnValidate()
    {
        CreateRuntimeMaterials();
    }

    private bool CreateRuntimeMaterials()
    {
        if (blurShader == null)
        {
            blurShader = Shader.Find(BlurShaderName);
        }

        if (panelBlitShader == null)
        {
            panelBlitShader = Shader.Find(PanelBlitShaderName);
        }

        if (blurShader == null || panelBlitShader == null)
        {
            return false;
        }

        if (runtimeBlurMaterial == null || runtimeBlurMaterial.shader != blurShader)
        {
            CoreUtils.Destroy(runtimeBlurMaterial);
            runtimeBlurMaterial = CoreUtils.CreateEngineMaterial(blurShader);
        }

        if (runtimePanelBlitMaterial == null || runtimePanelBlitMaterial.shader != panelBlitShader)
        {
            CoreUtils.Destroy(runtimePanelBlitMaterial);
            runtimePanelBlitMaterial = CoreUtils.CreateEngineMaterial(panelBlitShader);
        }

        return true;
    }

    private static bool TryGetPanelForRenderingCamera(
        UiBlurPanel panel,
        Camera renderingCamera,
        out Camera projectionCamera)
    {
        projectionCamera = null;

        if (panel == null || panel.Image == null || renderingCamera == null)
        {
            return false;
        }

        Canvas panelCanvas = panel.Image.canvas;
        if (panelCanvas == null || panelCanvas.renderMode != RenderMode.ScreenSpaceCamera)
        {
            return false;
        }

        Camera worldCamera = panelCanvas.worldCamera;
        if (worldCamera == null)
        {
            return false;
        }

        if (worldCamera == renderingCamera)
        {
            projectionCamera = renderingCamera;
            return true;
        }

#if UNITY_EDITOR
        if (renderingCamera.cameraType == CameraType.SceneView)
        {
            projectionCamera = renderingCamera;
            return true;
        }
#endif

        return false;
    }

    private static bool HasPanelsForRenderingCamera(IReadOnlyList<UiBlurPanel> panels, Camera renderingCamera)
    {
        for (int panelIndex = 0; panelIndex < panels.Count; panelIndex++)
        {
            UiBlurPanel panel = panels[panelIndex];
            if (TryGetPanelForRenderingCamera(panel, renderingCamera, out Camera _))
            {
                return true;
            }
        }

        return false;
    }

    private static void GetPanelBlurSize(
        Rect renderRect,
        int downsample,
        out int blurWidth,
        out int blurHeight)
    {
        blurWidth = Mathf.Max(1, Mathf.RoundToInt(renderRect.width) / downsample);
        blurHeight = Mathf.Max(1, Mathf.RoundToInt(renderRect.height) / downsample);
    }

    private static bool TryCreatePanelBlurSlice(
        RenderGraph renderGraph,
        IReadOnlyList<UiBlurPanel> panels,
        int panelIndex,
        Camera camera,
        int downsample,
        int screenWidth,
        int screenHeight,
        int renderWidth,
        int renderHeight,
        ref TextureDesc pingTextureDesc,
        out PanelBlurSlice panelBlurSlice)
    {
        panelBlurSlice = default;

        UiBlurPanel panel = panels[panelIndex];
        if (!TryGetPanelForRenderingCamera(panel, camera, out Camera projectionCamera))
        {
            return false;
        }

        if (!panel.TryGetScreenRect(projectionCamera, out Rect pixelRect))
        {
            return false;
        }

        Rect renderRect = PixelRectToRenderRect(
            pixelRect,
            screenWidth,
            screenHeight,
            renderWidth,
            renderHeight);
        GetPanelBlurSize(renderRect, downsample, out int blurWidth, out int blurHeight);

        pingTextureDesc.width = blurWidth;
        pingTextureDesc.height = blurHeight;
        pingTextureDesc.name = "UI Blur Ping A " + panelIndex;
        TextureHandle pingA = renderGraph.CreateTexture(pingTextureDesc);
        pingTextureDesc.name = "UI Blur Ping B " + panelIndex;
        TextureHandle pingB = renderGraph.CreateTexture(pingTextureDesc);

        panelBlurSlice = new PanelBlurSlice
        {
            panelIndex = panelIndex,
            pingA = pingA,
            pingB = pingB
        };
        return true;
    }

    private static Vector4 GetCameraCropScaleBias(Rect renderRect, int renderWidth, int renderHeight)
    {
        float scaleX = renderRect.width / renderWidth;
        float scaleY = renderRect.height / renderHeight;
        float biasX = renderRect.x / renderWidth;
        float biasY = renderRect.y / renderHeight;
        return new Vector4(scaleX, scaleY, biasX, biasY);
    }

    private static Rect PixelRectToRenderRect(
        Rect pixelRect,
        int screenWidth,
        int screenHeight,
        int renderWidth,
        int renderHeight)
    {
        float scaleX = renderWidth / (float)screenWidth;
        float scaleY = renderHeight / (float)screenHeight;
        return new Rect(
            pixelRect.x * scaleX,
            pixelRect.y * scaleY,
            pixelRect.width * scaleX,
            pixelRect.height * scaleY);
    }

    private static Vector4 GetPanelAtlasUv(Rect renderRect, int renderWidth, int renderHeight)
    {
        float atlasMinX = renderRect.x / renderWidth;
        float atlasMinY = renderRect.y / renderHeight;
        float atlasSizeX = renderRect.width / renderWidth;
        float atlasSizeY = renderRect.height / renderHeight;
        return new Vector4(atlasMinX, atlasMinY, atlasSizeX, atlasSizeY);
    }

    private static Vector4 GetBlurTexelSize(int blurWidth, int blurHeight)
    {
        return new Vector4(1.0f / blurWidth, 1.0f / blurHeight, blurWidth, blurHeight);
    }

    private struct PanelBlurSlice
    {
        public int panelIndex;
        public TextureHandle pingA;
        public TextureHandle pingB;
    }

    private class UiBlurPass : ScriptableRenderPass
    {
        private const string PassName = "UI Blur";
        private const string PingAName = "UI Blur Ping A";
        private const string PingBName = "UI Blur Ping B";
        private const string AtlasName = "UI Blur Atlas";

        private static readonly int BlurOffsetShaderId = Shader.PropertyToID("_BlurOffset");
        private static readonly int BlurTexelSizeShaderId = Shader.PropertyToID("_BlurTexelSize");
        private static readonly int GlobalUiBlurTextureId = Shader.PropertyToID("_GlobalUiBlurTexture");
        private static readonly Vector4 FullScreenScaleBias = new Vector4(1f, 1f, 0f, 0f);

        private Material material;
        private int passIterations;
        private int passDownsample;
        private float passOffset;

        public UiBlurPass()
        {
            requiresIntermediateTexture = true;
            ConfigureInput(ScriptableRenderPassInput.Color);
        }

        public void Setup(Material blurMaterial, int iterations, int downsampleFactor, float blurOffset)
        {
            material = blurMaterial;
            passIterations = Mathf.Clamp(iterations, 1, 6);
            passDownsample = Mathf.Clamp(downsampleFactor, 1, 4);
            passOffset = blurOffset;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (material == null)
            {
                return;
            }

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            Camera camera = cameraData.camera;

            TextureHandle cameraColor = resourceData.isActiveTargetBackBuffer
                ? resourceData.cameraColor
                : resourceData.activeColorTexture;

            if (!cameraColor.IsValid())
            {
                return;
            }

            IReadOnlyList<UiBlurPanel> panels = UiBlurPanelRegistry.ActivePanels;
            if (!HasPanelsForRenderingCamera(panels, camera))
            {
                return;
            }

            RenderTextureDescriptor cameraDescriptor = cameraData.cameraTargetDescriptor;
            int atlasWidth = Mathf.Max(1, cameraDescriptor.width / passDownsample);
            int atlasHeight = Mathf.Max(1, cameraDescriptor.height / passDownsample);

            TextureDesc pingTextureDesc = new TextureDesc(atlasWidth, atlasHeight)
            {
                format = GraphicsFormat.R8G8B8A8_UNorm,
                depthBufferBits = 0,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = PingAName
            };

            List<PanelBlurSlice> panelBlurSlices = new List<PanelBlurSlice>(panels.Count);

            for (int panelIndex = 0; panelIndex < panels.Count; panelIndex++)
            {
                if (!TryCreatePanelBlurSlice(
                    renderGraph,
                    panels,
                    panelIndex,
                    camera,
                    passDownsample,
                    camera.pixelWidth,
                    camera.pixelHeight,
                    cameraDescriptor.width,
                    cameraDescriptor.height,
                    ref pingTextureDesc,
                    out PanelBlurSlice panelBlurSlice))
                {
                    continue;
                }

                panelBlurSlices.Add(panelBlurSlice);
            }

            if (panelBlurSlices.Count == 0)
            {
                return;
            }

            TextureDesc atlasDesc = new TextureDesc(atlasWidth, atlasHeight)
            {
                format = GraphicsFormat.R8G8B8A8_UNorm,
                depthBufferBits = 0,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = AtlasName
            };
            TextureHandle atlas = renderGraph.CreateTexture(atlasDesc);

            using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass<BlurPassData>(PassName, out BlurPassData passData))
            {
                passData.material = material;
                passData.cameraColor = cameraColor;
                passData.atlas = atlas;
                passData.camera = camera;
                passData.panels = panels;
                passData.panelBlurSlices = panelBlurSlices;
                passData.iterations = passIterations;
                passData.blurOffset = passOffset;
                passData.downsample = passDownsample;
                passData.screenWidth = camera.pixelWidth;
                passData.screenHeight = camera.pixelHeight;
                passData.renderWidth = cameraDescriptor.width;
                passData.renderHeight = cameraDescriptor.height;

                builder.UseTexture(cameraColor, AccessFlags.Read);
                builder.UseTexture(atlas, AccessFlags.Write);

                for (int sliceIndex = 0; sliceIndex < panelBlurSlices.Count; sliceIndex++)
                {
                    PanelBlurSlice panelBlurSlice = panelBlurSlices[sliceIndex];
                    builder.UseTexture(panelBlurSlice.pingA, AccessFlags.ReadWrite);
                    builder.UseTexture(panelBlurSlice.pingB, AccessFlags.ReadWrite);
                }

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetGlobalTextureAfterPass(atlas, GlobalUiBlurTextureId);

                builder.SetRenderFunc((BlurPassData data, UnsafeGraphContext context) =>
                {
                    ExecutePerPanelBlurPass(data, context);
                });
            }
        }

        private static void ExecutePerPanelBlurPass(BlurPassData data, UnsafeGraphContext context)
        {
            CommandBuffer commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            context.cmd.SetRenderTarget(data.atlas);
            context.cmd.ClearRenderTarget(false, true, Color.clear);

            for (int sliceIndex = 0; sliceIndex < data.panelBlurSlices.Count; sliceIndex++)
            {
                PanelBlurSlice panelBlurSlice = data.panelBlurSlices[sliceIndex];
                UiBlurPanel panel = data.panels[panelBlurSlice.panelIndex];
                if (!TryGetPanelForRenderingCamera(panel, data.camera, out Camera projectionCamera))
                {
                    continue;
                }

                if (!panel.TryGetScreenRect(projectionCamera, out Rect pixelRect))
                {
                    continue;
                }

                Rect renderRect = PixelRectToRenderRect(
                    pixelRect,
                    data.screenWidth,
                    data.screenHeight,
                    data.renderWidth,
                    data.renderHeight);
                GetPanelBlurSize(renderRect, data.downsample, out int panelBlurWidth, out int panelBlurHeight);
                Vector4 cameraCropScaleBias = GetCameraCropScaleBias(renderRect, data.renderWidth, data.renderHeight);
                Vector4 blurTexelSize = GetBlurTexelSize(panelBlurWidth, panelBlurHeight);
                Rect panelBlurViewport = new Rect(0f, 0f, panelBlurWidth, panelBlurHeight);

                TextureHandle readHandle = data.cameraColor;
                TextureHandle writeHandle = panelBlurSlice.pingA;
                context.cmd.SetViewport(panelBlurViewport);

                for (int iterationIndex = 0; iterationIndex < data.iterations; iterationIndex++)
                {
                    float iterationOffset = data.blurOffset + iterationIndex * data.blurOffset;
                    data.material.SetFloat(BlurOffsetShaderId, iterationOffset);

                    Vector4 texelSize = readHandle == data.cameraColor
                        ? GetBlurTexelSize(data.renderWidth, data.renderHeight)
                        : blurTexelSize;
                    data.material.SetVector(BlurTexelSizeShaderId, texelSize);
                    context.cmd.SetRenderTarget(writeHandle);

                    Vector4 scaleBias = readHandle == data.cameraColor ? cameraCropScaleBias : FullScreenScaleBias;
                    Blitter.BlitTexture(commandBuffer, readHandle, scaleBias, data.material, 0);

                    readHandle = writeHandle;
                    writeHandle = readHandle == panelBlurSlice.pingA ? panelBlurSlice.pingB : panelBlurSlice.pingA;
                }

                float atlasX = renderRect.x / data.downsample;
                float atlasY = renderRect.y / data.downsample;
                Rect atlasViewport = new Rect(atlasX, atlasY, panelBlurWidth, panelBlurHeight);
                context.cmd.SetRenderTarget(data.atlas);
                context.cmd.SetViewport(atlasViewport);
                Blitter.BlitTexture(commandBuffer, readHandle, FullScreenScaleBias, 0, false);
            }

            Rect fullViewport = new Rect(0f, 0f, data.renderWidth, data.renderHeight);
            context.cmd.SetViewport(fullViewport);
        }

        public void Dispose()
        {
        }

        private class BlurPassData
        {
            public Material material;
            public TextureHandle cameraColor;
            public TextureHandle atlas;
            public Camera camera;
            public IReadOnlyList<UiBlurPanel> panels;
            public List<PanelBlurSlice> panelBlurSlices;
            public int iterations;
            public float blurOffset;
            public int downsample;
            public int screenWidth;
            public int screenHeight;
            public int renderWidth;
            public int renderHeight;
        }
    }

    private class UiBlurPanelDrawPass : ScriptableRenderPass
    {
        private static readonly int MainTexShaderId = Shader.PropertyToID("_MainTex");
        private static readonly int ColorShaderId = Shader.PropertyToID("_Color");
        private static readonly int PanelAtlasUvShaderId = Shader.PropertyToID("_PanelAtlasUv");
        private static readonly int AtlasTexelSizeShaderId = Shader.PropertyToID("_AtlasTexelSize");

        private static readonly MaterialPropertyBlock panelPropertyBlock = new MaterialPropertyBlock();

        private Material material;
        private Camera renderingCamera;
        private int passDownsample;

        public UiBlurPanelDrawPass()
        {
        }

        public void Setup(Material panelMaterial, Camera camera, int downsample)
        {
            material = panelMaterial;
            renderingCamera = camera;
            passDownsample = Mathf.Clamp(downsample, 1, 4);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (material == null || renderingCamera == null)
            {
                return;
            }

            IReadOnlyList<UiBlurPanel> panels = UiBlurPanelRegistry.ActivePanels;
            if (!HasPanelsForRenderingCamera(panels, renderingCamera))
            {
                return;
            }

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            TextureHandle destination = resourceData.activeColorTexture;

            if (!destination.IsValid())
            {
                return;
            }

            using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass<PanelPassData>("UI Blur Panels", out PanelPassData passData))
            {
                passData.material = material;
                passData.destination = destination;
                passData.panels = panels;
                passData.renderingCamera = renderingCamera;
                passData.screenWidth = renderingCamera.pixelWidth;
                passData.screenHeight = renderingCamera.pixelHeight;
                passData.renderWidth = cameraData.cameraTargetDescriptor.width;
                passData.renderHeight = cameraData.cameraTargetDescriptor.height;
                passData.atlasWidth = Mathf.Max(1, cameraData.cameraTargetDescriptor.width / passDownsample);
                passData.atlasHeight = Mathf.Max(1, cameraData.cameraTargetDescriptor.height / passDownsample);

                builder.UseTexture(destination, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PanelPassData data, UnsafeGraphContext context) =>
                {
                    ExecutePanelDrawPass(data, context);
                });
            }
        }

        private static void ExecutePanelDrawPass(PanelPassData data, UnsafeGraphContext context)
        {
            CommandBuffer commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            context.cmd.SetRenderTarget(data.destination);

            for (int panelIndex = 0; panelIndex < data.panels.Count; panelIndex++)
            {
                UiBlurPanel panel = data.panels[panelIndex];
                if (!TryGetPanelForRenderingCamera(panel, data.renderingCamera, out Camera projectionCamera))
                {
                    continue;
                }

                if (!panel.TryGetScreenRect(projectionCamera, out Rect pixelRect))
                {
                    continue;
                }

                Rect renderRect = PixelRectToRenderRect(
                    pixelRect,
                    data.screenWidth,
                    data.screenHeight,
                    data.renderWidth,
                    data.renderHeight);

                Texture sourceTexture = panel.Image.sprite != null
                    ? panel.Image.sprite.texture
                    : Texture2D.whiteTexture;

                panelPropertyBlock.Clear();
                panelPropertyBlock.SetTexture(MainTexShaderId, sourceTexture);
                panelPropertyBlock.SetColor(ColorShaderId, panel.Color);
                panelPropertyBlock.SetVector(
                    PanelAtlasUvShaderId,
                    GetPanelAtlasUv(renderRect, data.renderWidth, data.renderHeight));
                panelPropertyBlock.SetVector(
                    AtlasTexelSizeShaderId,
                    new Vector4(
                        1.0f / data.atlasWidth,
                        1.0f / data.atlasHeight,
                        data.atlasWidth,
                        data.atlasHeight));

                context.cmd.SetViewport(renderRect);
                commandBuffer.DrawProcedural(
                    Matrix4x4.identity,
                    data.material,
                    0,
                    MeshTopology.Triangles,
                    3,
                    1,
                    panelPropertyBlock);
            }

            Rect fullViewport = new Rect(0f, 0f, data.renderWidth, data.renderHeight);
            context.cmd.SetViewport(fullViewport);
        }

        public void Dispose()
        {
        }

        private class PanelPassData
        {
            public Material material;
            public TextureHandle destination;
            public IReadOnlyList<UiBlurPanel> panels;
            public Camera renderingCamera;
            public int screenWidth;
            public int screenHeight;
            public int renderWidth;
            public int renderHeight;
            public int atlasWidth;
            public int atlasHeight;
        }
    }
}
}
