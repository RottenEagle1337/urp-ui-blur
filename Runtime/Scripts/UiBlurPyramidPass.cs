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
    /// Records UI draw ranges split by blur sorting layers, with a downsample pyramid before each range:
    /// Draw UI [..B0) → Pyramid → Draw UI [B0..B1) → Pyramid → Draw UI [B1..].
    /// Pass count: one raster pass per pyramid level and one UI draw pass per range.
    /// </summary>
    internal sealed class UiBlurPyramidPass : ScriptableRenderPass
    {
        private const int MaxBlurLayers = 16;
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

        private readonly List<ShaderTagId> shaderTagList = new List<ShaderTagId>(ShaderTags);
        private readonly int[] layerValues = new int[MaxBlurLayers];
        private readonly TextureHandle[] pyramid = new TextureHandle[UiBlurFeature.MaxLevels];

        private Material material;
        private IReadOnlyList<int> blurSortingLayers;
        private int maxBlurLevels;
        private float referenceHeight;
        private LayerMask uiLayerMask;
        private bool supportStencilMasks;
        private bool loggedDepthWarning;

        private sealed class DrawPassData
        {
            public RendererListHandle rendererList;
            public readonly TextureHandle[] levels = new TextureHandle[UiBlurFeature.MaxLevels];
            public Vector4 screenParams;
            public Vector4 lodParams;
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
            IReadOnlyList<int> sortingLayers,
            int levels,
            float blurReferenceHeight,
            LayerMask layerMask,
            bool useStencil)
        {
            material = pyramidMaterial;
            blurSortingLayers = sortingLayers;
            maxBlurLevels = Mathf.Clamp(levels, 1, UiBlurFeature.MaxLevels);
            referenceHeight = Mathf.Max(1.0f, blurReferenceHeight);
            uiLayerMask = layerMask;
            supportStencilMasks = useStencil;
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

            // Scene View and other editor cameras draw UI without blur.
            bool blurAllowed = material != null &&
                               (cameraData.cameraType == CameraType.Game || cameraData.cameraType == CameraType.VR);

            int layerCount = blurAllowed ? CollectLayers() : 0;
            bool pyramidBeforeAllUi = blurAllowed && layerCount == 0;

            int blurredLevels = 0;
            if (pyramidBeforeAllUi)
            {
                blurredLevels = AddPyramid(renderGraph, colorTexture, targetWidth, targetHeight, levelCount);
            }

            int rangeStart = short.MinValue;
            for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
            {
                int layerValue = layerValues[layerIndex];
                if (layerValue > rangeStart)
                {
                    AddDrawPass(renderGraph, renderingData, drawingSettings, colorTexture, depthTexture, blurredLevels,
                        screenParams, lodParams, rangeStart, layerValue - 1);
                }

                blurredLevels = AddPyramid(renderGraph, colorTexture, targetWidth, targetHeight, levelCount);
                rangeStart = layerValue;
            }

            AddDrawPass(renderGraph, renderingData, drawingSettings, colorTexture, depthTexture, blurredLevels,
                screenParams, lodParams, rangeStart, short.MaxValue);
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

        /// <summary>Fills layerValues with the sorting layer values of valid blur layers, sorted and unique.</summary>
        private int CollectLayers()
        {
            if (blurSortingLayers == null)
            {
                return 0;
            }

            int count = 0;
            for (int index = 0; index < blurSortingLayers.Count && count < MaxBlurLayers; index++)
            {
                int sortingLayerId = blurSortingLayers[index];
                if (!SortingLayer.IsValid(sortingLayerId))
                {
                    continue;
                }

                int value = SortingLayer.GetLayerValueFromID(sortingLayerId);

                int insertIndex = count;
                while (insertIndex > 0 && layerValues[insertIndex - 1] > value)
                {
                    insertIndex--;
                }

                if (insertIndex > 0 && layerValues[insertIndex - 1] == value)
                {
                    continue;
                }

                for (int shiftIndex = count; shiftIndex > insertIndex; shiftIndex--)
                {
                    layerValues[shiftIndex] = layerValues[shiftIndex - 1];
                }

                layerValues[insertIndex] = value;
                count++;
            }

            return count;
        }

        /// <summary>Downsample chain color → L1 → … → Ln into <see cref="pyramid"/>. Returns n.</summary>
        private int AddPyramid(RenderGraph renderGraph, TextureHandle colorTexture, int targetWidth, int targetHeight,
            int levelCount)
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

                pyramid[level - 1] = destination;
                source = destination;
            }

            return levelCount;
        }

        private void AddDrawPass(
            RenderGraph renderGraph,
            UniversalRenderingData renderingData,
            DrawingSettings drawingSettings,
            TextureHandle colorTexture,
            TextureHandle depthTexture,
            int blurredLevels,
            Vector4 screenParams,
            Vector4 lodParams,
            int lowerSortingValue,
            int upperSortingValue)
        {
            var filteringSettings = new FilteringSettings(RenderQueueRange.all, uiLayerMask)
            {
                sortingLayerRange = new SortingLayerRange((short)lowerSortingValue, (short)upperSortingValue)
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

                // Without a pyramid yet, panels sample black; missing top levels repeat the last level.
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
