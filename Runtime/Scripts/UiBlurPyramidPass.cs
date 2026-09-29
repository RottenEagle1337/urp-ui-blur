using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;

namespace RottenEagle
{
    /// <summary>
    /// Down ½ → Capture UI into ½ → Down ¼ … → Draw UI.
    /// Capture UI merges with Down ½ into one native render pass: pyramid levels + 1 render passes in total.
    /// </summary>
    internal sealed class UiBlurPyramidPass : ScriptableRenderPass
    {
        private const float DownsampleOffset = 0.5f;

        private static readonly int BlitTextureId = Shader.PropertyToID("_BlitTexture");
        private static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
        private static readonly int BlurSourceTexelSizeId = Shader.PropertyToID("_BlurSourceTexelSize");
        private static readonly int BlurParamsId = Shader.PropertyToID("_BlurParams");
        private static readonly int UiBlurScreenParamsId = Shader.PropertyToID("_UIBlurScreenParams");
        private static readonly int UiBlurLodParamsId = Shader.PropertyToID("_UIBlurLodParams");

        private static readonly int[] UiBlurLevelIds =
        {
            Shader.PropertyToID("_UIBlurLevel1"), Shader.PropertyToID("_UIBlurLevel2"),
            Shader.PropertyToID("_UIBlurLevel3"), Shader.PropertyToID("_UIBlurLevel4"),
            Shader.PropertyToID("_UIBlurLevel5"), Shader.PropertyToID("_UIBlurLevel6"),
            Shader.PropertyToID("_UIBlurLevel7")
        };

        private static readonly string[] LevelTextureNames =
        {
            "_UIBlurLevel1", "_UIBlurLevel2", "_UIBlurLevel3", "_UIBlurLevel4",
            "_UIBlurLevel5", "_UIBlurLevel6", "_UIBlurLevel7"
        };

        // Explicit samplers: stable names in Frame Debugger and GPU timings in the Profiler.
        private static readonly ProfilingSampler[] DownSamplers =
        {
            new ProfilingSampler("UI Blur Down 1/2"), new ProfilingSampler("UI Blur Down 1/4"),
            new ProfilingSampler("UI Blur Down 1/8"), new ProfilingSampler("UI Blur Down 1/16"),
            new ProfilingSampler("UI Blur Down 1/32"), new ProfilingSampler("UI Blur Down 1/64"),
            new ProfilingSampler("UI Blur Down 1/128")
        };

        private static readonly ProfilingSampler DrawSampler = new ProfilingSampler("UI Blur Draw UI");
        private static readonly ProfilingSampler CaptureSampler = new ProfilingSampler("UI Blur Capture UI");

        // Enabled while UI is drawn into the pyramid: blur panels write a near depth mark instead of color,
        // so UI drawn after a panel is rejected inside the panel shape.
        private static readonly GlobalKeyword CaptureKeyword = GlobalKeyword.Create("_UI_BLUR_CAPTURE");

        private static readonly ShaderTagId[] ShaderTags =
        {
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly")
        };

        private static readonly Vector4 FullScaleBias = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);

        private static readonly MaterialPropertyBlock DownPropertyBlock = new MaterialPropertyBlock();

        private static readonly BaseRenderFunc<DrawPassData, RasterGraphContext> DrawRenderFunc = ExecuteDraw;
        private static readonly BaseRenderFunc<DownPassData, RasterGraphContext> DownRenderFunc = ExecuteDown;
        private static readonly BaseRenderFunc<CapturePassData, RasterGraphContext> CaptureRenderFunc = ExecuteCapture;

        private readonly List<ShaderTagId> shaderTagList = new List<ShaderTagId>(ShaderTags);
        private readonly TextureHandle[] pyramid = new TextureHandle[UiBlurFeature.MaxLevels];

        private Material material;
        private int maxBlurLevels;
        private float referenceHeight;
        private LayerMask uiLayerMask;
        private bool supportStencilMasks;
        private bool blurRenderTextureCameras;
        private bool loggedDepthWarning;
        private bool loggedMsaaWarning;

        private sealed class DrawPassData
        {
            public RendererListHandle rendererList;
            public readonly TextureHandle[] levels = new TextureHandle[UiBlurFeature.MaxLevels];
            public Vector4 screenParams;
            public Vector4 lodParams;
        }

        private sealed class CapturePassData
        {
            public RendererListHandle rendererList;
        }

        private sealed class DownPassData
        {
            public Material material;
            public TextureHandle source;
            public Vector4 sourceTexelSize;
        }

        public UiBlurPyramidPass()
        {
            requiresIntermediateTexture = true;
        }

        public void Setup(
            Material pyramidMaterial,
            int levels,
            float blurReferenceHeight,
            LayerMask layerMask,
            bool useStencil,
            bool includeRenderTextureCameras)
        {
            material = pyramidMaterial;
            maxBlurLevels = Mathf.Clamp(levels, 1, UiBlurFeature.MaxLevels);
            referenceHeight = Mathf.Max(1.0f, blurReferenceHeight);
            uiLayerMask = layerMask;
            supportStencilMasks = useStencil;
            blurRenderTextureCameras = includeRenderTextureCameras;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData.isActiveTargetBackBuffer)
            {
                return;
            }

            TextureHandle colorTexture = resourceData.activeColorTexture;
            if (!colorTexture.IsValid())
            {
                return;
            }

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();

            TextureDesc colorDesc = renderGraph.GetTextureDesc(colorTexture);
            int targetWidth = colorDesc.width;
            int targetHeight = colorDesc.height;

            TextureHandle depthTexture = GetStencilAttachment(renderGraph, resourceData, colorDesc);
            DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(
                shaderTagList, renderingData, cameraData, lightData, SortingCriteria.CommonTransparent);

            var screenParams = new Vector4(targetWidth, targetHeight, 1.0f / targetWidth, 1.0f / targetHeight);

            // Radius is authored at the reference height: whole octaves of the resolution change add pyramid levels.
            float resolutionLod = Mathf.Log(targetHeight / referenceHeight, 2.0f);
            int levelCount = Mathf.Clamp(maxBlurLevels + Mathf.RoundToInt(resolutionLod), 1, UiBlurFeature.MaxLevels);
            var lodParams = new Vector4(resolutionLod, maxBlurLevels, levelCount, 0.0f);

            bool blurAllowed = IsBlurAllowed(cameraData, colorDesc);

            // One pyramid for all UI: UI drawn before a panel (in hierarchy order) is captured into the
            // pyramid, UI drawn after it is rejected inside the panel shape by the depth mark.
            int blurredLevels = blurAllowed
                ? AddPyramid(renderGraph, colorTexture, targetWidth, targetHeight, levelCount, renderingData,
                    drawingSettings)
                : 0;

            AddDrawPass(renderGraph, renderingData, drawingSettings, colorTexture, depthTexture, blurredLevels,
                screenParams, lodParams);
        }

        private bool IsBlurAllowed(UniversalCameraData cameraData, in TextureDesc colorDesc)
        {
            // Scene View and other editor cameras draw UI without blur.
            if (material == null ||
                (cameraData.cameraType != CameraType.Game && cameraData.cameraType != CameraType.VR))
            {
                return false;
            }

            // Minimap, portal and similar cameras rarely show blurred UI, skip their pyramid unless asked.
            if (!blurRenderTextureCameras && cameraData.camera.targetTexture != null)
            {
                return false;
            }

            // Without post processing URP may still hold the multisampled color here; it cannot be sampled.
            if (colorDesc.msaaSamples != MSAASamples.None)
            {
                if (!loggedMsaaWarning)
                {
                    loggedMsaaWarning = true;
                    Debug.LogWarning("UiBlurFeature: the camera color is multisampled after post processing " +
                                     "(MSAA with Post Processing off). UI is drawn without blur; enable Post " +
                                     "Processing on the camera or disable MSAA.");
                }

                return false;
            }

            return true;
        }

        private TextureHandle GetStencilAttachment(
            RenderGraph renderGraph,
            UniversalResourceData resourceData,
            in TextureDesc colorDesc)
        {
            if (!supportStencilMasks)
            {
                return TextureHandle.nullHandle;
            }

            TextureHandle depthTexture = resourceData.activeDepthTexture;
            if (!depthTexture.IsValid())
            {
                return TextureHandle.nullHandle;
            }

            TextureDesc depthDesc = renderGraph.GetTextureDesc(depthTexture);
            if (depthDesc.msaaSamples != colorDesc.msaaSamples ||
                depthDesc.width != colorDesc.width ||
                depthDesc.height != colorDesc.height)
            {
                if (!loggedDepthWarning)
                {
                    loggedDepthWarning = true;
                    Debug.LogWarning("UiBlurFeature: camera depth does not match the color target after post processing " +
                                     "(MSAA or size). Stencil masks are disabled for UI drawn by the feature.");
                }

                return TextureHandle.nullHandle;
            }

            return depthTexture;
        }

        /// <summary>Downsample chain color → L1 → … → Ln into <see cref="pyramid"/>. Returns n.</summary>
        private int AddPyramid(RenderGraph renderGraph, TextureHandle colorTexture, int targetWidth, int targetHeight,
            int levelCount, UniversalRenderingData renderingData, DrawingSettings drawingSettings)
        {
            GraphicsFormat format = GetBlurFormat();
            TextureHandle source = colorTexture;

            for (int level = 1; level <= levelCount; level++)
            {
                TextureHandle destination = CreateLevelTexture(renderGraph, targetWidth, targetHeight, level, format,
                    LevelTextureNames[level - 1]);

                int sourceWidth = LevelSize(targetWidth, level - 1);
                int sourceHeight = LevelSize(targetHeight, level - 1);

                ProfilingSampler sampler = DownSamplers[level - 1];
                using (IRasterRenderGraphBuilder builder =
                       renderGraph.AddRasterRenderPass(sampler.name, out DownPassData passData, sampler))
                {
                    passData.material = material;
                    passData.source = source;
                    passData.sourceTexelSize = new Vector4(1.0f / sourceWidth, 1.0f / sourceHeight, sourceWidth,
                        sourceHeight);

                    builder.UseTexture(source);
                    builder.SetRenderAttachment(destination, 0, AccessFlags.WriteAll);
                    builder.SetRenderFunc(DownRenderFunc);
                }

                if (level == 1)
                {
                    AddCapturePass(renderGraph, renderingData, drawingSettings, destination,
                        LevelSize(targetWidth, 1), LevelSize(targetHeight, 1));
                }

                pyramid[level - 1] = destination;
                source = destination;
            }

            return levelCount;
        }

        /// <summary>Draws all UI into the first pyramid level with its own depth-stencil buffer.</summary>
        private void AddCapturePass(
            RenderGraph renderGraph,
            UniversalRenderingData renderingData,
            DrawingSettings drawingSettings,
            TextureHandle levelTexture,
            int width,
            int height)
        {
            var filteringSettings = new FilteringSettings(RenderQueueRange.all, uiLayerMask)
            {
                sortingLayerRange = SortingLayerRange.all
            };

            // No state override: UI materials keep ZWrite Off / ZTest LEqual, blur panels write the depth mark.
            var rendererListParams = new RendererListParams(renderingData.cullResults, drawingSettings, filteringSettings);

            var depthDesc = new TextureDesc(width, height)
            {
                format = SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil),
                clearBuffer = false,
                name = "_UIBlurCaptureDepth"
            };
            TextureHandle depthTexture = renderGraph.CreateTexture(depthDesc);

            using (IRasterRenderGraphBuilder builder =
                   renderGraph.AddRasterRenderPass(CaptureSampler.name, out CapturePassData passData, CaptureSampler))
            {
                passData.rendererList = renderGraph.CreateRendererList(rendererListParams);

                builder.UseRendererList(passData.rendererList);
                builder.SetRenderAttachment(levelTexture, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depthTexture, AccessFlags.WriteAll);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(CaptureRenderFunc);
            }
        }

        private void AddDrawPass(
            RenderGraph renderGraph,
            UniversalRenderingData renderingData,
            DrawingSettings drawingSettings,
            TextureHandle colorTexture,
            TextureHandle depthTexture,
            int blurredLevels,
            Vector4 screenParams,
            Vector4 lodParams)
        {
            var filteringSettings = new FilteringSettings(RenderQueueRange.all, uiLayerMask)
            {
                sortingLayerRange = SortingLayerRange.all
            };

            // UI always draws on top of the scene: depth test and write are disabled, stencil stays with the material.
            var stateBlock = new RenderStateBlock(RenderStateMask.Depth)
            {
                depthState = new DepthState(false, CompareFunction.Always)
            };

            var tagValues = new NativeArray<ShaderTagId>(1, Allocator.Temp);
            var stateBlocks = new NativeArray<RenderStateBlock>(1, Allocator.Temp);
            tagValues[0] = ShaderTagId.none;
            stateBlocks[0] = stateBlock;

            var rendererListParams = new RendererListParams(renderingData.cullResults, drawingSettings, filteringSettings)
            {
                tagValues = tagValues,
                stateBlocks = stateBlocks,
                isPassTagName = false
            };

            using (IRasterRenderGraphBuilder builder =
                   renderGraph.AddRasterRenderPass(DrawSampler.name, out DrawPassData passData, DrawSampler))
            {
                passData.rendererList = renderGraph.CreateRendererList(rendererListParams);
                passData.screenParams = screenParams;
                passData.lodParams = lodParams;

                // Without a pyramid (editor cameras), panels sample black; missing top levels repeat the last level.
                TextureHandle fallback = renderGraph.defaultResources.blackTexture;
                if (blurredLevels == 0)
                {
                    passData.lodParams.z = 1.0f;
                    builder.UseTexture(fallback);
                }

                for (int level = 0; level < UiBlurFeature.MaxLevels; level++)
                {
                    TextureHandle levelTexture = blurredLevels == 0
                        ? fallback
                        : pyramid[Mathf.Min(level, blurredLevels - 1)];
                    passData.levels[level] = levelTexture;
                    if (level < blurredLevels)
                    {
                        builder.UseTexture(levelTexture);
                    }
                }

                builder.UseRendererList(passData.rendererList);
                builder.SetRenderAttachment(colorTexture, 0);
                if (depthTexture.IsValid())
                {
                    builder.SetRenderAttachmentDepth(depthTexture);
                }

                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(DrawRenderFunc);
            }
        }

        private static void ExecuteDraw(DrawPassData data, RasterGraphContext context)
        {
            RasterCommandBuffer cmd = context.cmd;
            cmd.SetGlobalVector(UiBlurScreenParamsId, data.screenParams);
            cmd.SetGlobalVector(UiBlurLodParamsId, data.lodParams);
            for (int level = 0; level < UiBlurLevelIds.Length; level++)
            {
                cmd.SetGlobalTexture(UiBlurLevelIds[level], data.levels[level]);
            }

            cmd.DrawRendererList(data.rendererList);
        }

        private static void ExecuteCapture(CapturePassData data, RasterGraphContext context)
        {
            RasterCommandBuffer cmd = context.cmd;
            cmd.ClearRenderTarget(RTClearFlags.DepthStencil, Color.clear, 1.0f, 0);
            cmd.EnableKeyword(CaptureKeyword);
            cmd.DrawRendererList(data.rendererList);
            cmd.DisableKeyword(CaptureKeyword);
        }

        private static void ExecuteDown(DownPassData data, RasterGraphContext context)
        {
            MaterialPropertyBlock propertyBlock = DownPropertyBlock;

            // Property block values are copied into the command buffer by DrawProcedural,
            // so one shared block is safe for every pass.
            propertyBlock.Clear();
            propertyBlock.SetTexture(BlitTextureId, (RTHandle)data.source);
            propertyBlock.SetVector(BlitScaleBiasId, FullScaleBias);
            propertyBlock.SetVector(BlurSourceTexelSizeId, data.sourceTexelSize);
            propertyBlock.SetVector(BlurParamsId, new Vector4(DownsampleOffset, 0.0f, 0.0f, 0.0f));

            context.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1,
                propertyBlock);
        }

        private static GraphicsFormat blurFormat = GraphicsFormat.None;

        private static GraphicsFormat GetBlurFormat()
        {
            if (blurFormat != GraphicsFormat.None)
            {
                return blurFormat;
            }

            // Same check URP uses for its own B10G11R11 targets.
            if (SystemInfo.IsFormatSupported(GraphicsFormat.B10G11R11_UFloatPack32, GraphicsFormatUsage.Blend))
            {
                blurFormat = GraphicsFormat.B10G11R11_UFloatPack32;
            }
            else
            {
                // sRGB storage keeps precision in dark tones, but is only valid in linear color space.
                blurFormat = QualitySettings.activeColorSpace == ColorSpace.Linear
                    ? GraphicsFormat.R8G8B8A8_SRGB
                    : GraphicsFormat.R8G8B8A8_UNorm;
            }

            return blurFormat;
        }

        private static TextureHandle CreateLevelTexture(
            RenderGraph renderGraph,
            int targetWidth,
            int targetHeight,
            int level,
            GraphicsFormat format,
            string name)
        {
            var desc = new TextureDesc(LevelSize(targetWidth, level), LevelSize(targetHeight, level))
            {
                format = format,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                clearBuffer = false,
                name = name
            };
            return renderGraph.CreateTexture(desc);
        }

        private static int LevelSize(int size, int level)
        {
            return Mathf.Max(1, size >> level);
        }
    }
}
