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
    /// Records UI draw ranges and Dual Kawase blur chains:
    /// Draw UI [..L0) → Blur → Draw UI [L0..L1) → Blur → Draw UI [L1..].
    /// Pass count per active blur layer: 2 * levels - 1 blur passes and one UI draw pass.
    /// </summary>
    internal sealed class UiBlurLayersPass : ScriptableRenderPass
    {
        private const int MaxBlurLayers = 16;
        private const int PassDown = 0;
        private const int PassUp = 1;
        private const float MaxOffset = 3.0f;

        private static readonly int BlitTextureId = Shader.PropertyToID("_BlitTexture");
        private static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
        private static readonly int BlurSourceTexelSizeId = Shader.PropertyToID("_BlurSourceTexelSize");
        private static readonly int BlurSourceClampId = Shader.PropertyToID("_BlurSourceClamp");
        private static readonly int BlurParamsId = Shader.PropertyToID("_BlurParams");
        private static readonly int UiBlurTextureId = Shader.PropertyToID("_UIBlurTexture");
        private static readonly int UiBlurScreenParamsId = Shader.PropertyToID("_UIBlurScreenParams");

        // Explicit samplers: stable names in Frame Debugger and GPU timings in the Profiler.
        private static readonly ProfilingSampler[] DownSamplers =
        {
            new ProfilingSampler("UI Blur Down 1/2"), new ProfilingSampler("UI Blur Down 1/4"),
            new ProfilingSampler("UI Blur Down 1/8"), new ProfilingSampler("UI Blur Down 1/16"),
            new ProfilingSampler("UI Blur Down 1/32"), new ProfilingSampler("UI Blur Down 1/64")
        };

        private static readonly ProfilingSampler[] UpSamplers =
        {
            new ProfilingSampler("UI Blur Up 1/2"), new ProfilingSampler("UI Blur Up 1/4"),
            new ProfilingSampler("UI Blur Up 1/8"), new ProfilingSampler("UI Blur Up 1/16"),
            new ProfilingSampler("UI Blur Up 1/32"), new ProfilingSampler("UI Blur Up 1/64")
        };

        private static readonly ProfilingSampler DrawSampler = new ProfilingSampler("UI Blur Draw UI");

        private static readonly string[] LevelTextureNames =
        {
            "_UIBlurLevel1", "_UIBlurLevel2", "_UIBlurLevel3",
            "_UIBlurLevel4", "_UIBlurLevel5", "_UIBlurLevel6"
        };


        private static readonly ShaderTagId[] ShaderTags =
        {
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly")
        };

        private static readonly Vector4 FullScaleBias = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);

        private static readonly MaterialPropertyBlock BlurPropertyBlock = new MaterialPropertyBlock();

        private static readonly BaseRenderFunc<DrawPassData, RasterGraphContext> DrawRenderFunc = ExecuteDraw;
        private static readonly BaseRenderFunc<BlurPassData, RasterGraphContext> BlurRenderFunc = ExecuteBlur;

        private readonly List<ShaderTagId> shaderTagList = new List<ShaderTagId>(ShaderTags);
        private readonly LayerEntry[] layerEntries = new LayerEntry[MaxBlurLayers];

        private Material material;
        private IReadOnlyList<UiBlurLayerSettings> layerSettings;
        private LayerMask uiLayerMask;
        private float referenceHeight;
        private bool skipLayersWithoutPanels;
        private bool limitToPanelBounds;
        private bool supportStencilMasks;
        private bool loggedMsaaWarning;

        private struct LayerEntry
        {
            public int sortingLayerId;
            public int sortingLayerValue;
            public int levels;
            public float offset;
            public bool hasPanels;
            public Rect bounds;
        }

        private sealed class DrawPassData
        {
            public RendererListHandle rendererList;
            public TextureHandle blurTexture;
            public Vector4 screenParams;
        }

        private sealed class BlurPassData
        {
            public Material material;
            public int shaderPass;
            public TextureHandle source;
            public Vector4 sourceTexelSize;
            public Vector4 sourceClamp;
            public Vector4 blurParams;
            public Rect scissor;
            public bool useScissor;
        }

        public UiBlurLayersPass()
        {
            requiresIntermediateTexture = true;
        }

        public void Setup(
            Material blurMaterial,
            IReadOnlyList<UiBlurLayerSettings> blurLayers,
            LayerMask layerMask,
            float blurReferenceHeight,
            bool skipEmptyLayers,
            bool useScissor,
            bool useStencil)
        {
            material = blurMaterial;
            layerSettings = blurLayers;
            uiLayerMask = layerMask;
            referenceHeight = Mathf.Max(1.0f, blurReferenceHeight);
            skipLayersWithoutPanels = skipEmptyLayers;
            limitToPanelBounds = useScissor;
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

            Camera camera = cameraData.camera;
            bool blurAllowed = material != null &&
                               (cameraData.cameraType == CameraType.Game || cameraData.cameraType == CameraType.VR);

            int layerCount = blurAllowed ? CollectLayers() : 0;
            if (layerCount > 0)
            {
                CollectPanels(camera, layerCount, targetWidth, targetHeight);
            }

            var screenParams = new Vector4(targetWidth, targetHeight, 1.0f / targetWidth, 1.0f / targetHeight);
            DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(
                shaderTagList, renderingData, cameraData, lightData, SortingCriteria.CommonTransparent);

            TextureHandle blurTexture = renderGraph.defaultResources.blackTexture;
            int rangeStart = short.MinValue;

            for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
            {
                ref LayerEntry layer = ref layerEntries[layerIndex];
                if (skipLayersWithoutPanels && !layer.hasPanels)
                {
                    continue;
                }

                if (layer.sortingLayerValue > rangeStart)
                {
                    AddDrawPass(renderGraph, renderingData, drawingSettings, colorTexture, depthTexture, blurTexture,
                        screenParams, rangeStart, layer.sortingLayerValue - 1);
                }

                blurTexture = AddBlurChain(renderGraph, colorTexture, colorDesc, in layer);
                rangeStart = layer.sortingLayerValue;
            }

            AddDrawPass(renderGraph, renderingData, drawingSettings, colorTexture, depthTexture, blurTexture,
                screenParams, rangeStart, short.MaxValue);
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
                if (!loggedMsaaWarning)
                {
                    loggedMsaaWarning = true;
                    Debug.LogWarning("UiBlurFeature: camera depth does not match the color target after post processing " +
                                     "(MSAA or size). Stencil masks are disabled for UI drawn by the feature.");
                }

                return TextureHandle.nullHandle;
            }

            return depthTexture;
        }

        /// <summary>Fills layerEntries with valid layers sorted by sorting layer order.</summary>
        private int CollectLayers()
        {
            if (layerSettings == null)
            {
                return 0;
            }

            int count = 0;
            for (int settingsIndex = 0; settingsIndex < layerSettings.Count && count < MaxBlurLayers; settingsIndex++)
            {
                UiBlurLayerSettings settings = layerSettings[settingsIndex];
                if (settings == null || !SortingLayer.IsValid(settings.sortingLayerId))
                {
                    continue;
                }

                var entry = new LayerEntry
                {
                    sortingLayerId = settings.sortingLayerId,
                    sortingLayerValue = SortingLayer.GetLayerValueFromID(settings.sortingLayerId),
                    levels = Mathf.Clamp(settings.levels, 1, UiBlurLayerSettings.MaxLevels),
                    offset = Mathf.Max(0.0f, settings.offset),
                    hasPanels = false,
                    bounds = default
                };

                // Insertion sort by sorting layer value, duplicates keep the first entry.
                int insertIndex = count;
                bool duplicate = false;
                while (insertIndex > 0 && layerEntries[insertIndex - 1].sortingLayerValue >= entry.sortingLayerValue)
                {
                    if (layerEntries[insertIndex - 1].sortingLayerValue == entry.sortingLayerValue)
                    {
                        duplicate = true;
                        break;
                    }

                    layerEntries[insertIndex] = layerEntries[insertIndex - 1];
                    insertIndex--;
                }

                if (duplicate)
                {
                    // Undo the shift performed so far.
                    for (int shiftIndex = insertIndex; shiftIndex < count; shiftIndex++)
                    {
                        layerEntries[shiftIndex] = layerEntries[shiftIndex + 1];
                    }

                    continue;
                }

                layerEntries[insertIndex] = entry;
                count++;
            }

            return count;
        }

        private void CollectPanels(Camera camera, int layerCount, int targetWidth, int targetHeight)
        {
            IReadOnlyList<UiBlurPanel> panels = UiBlurPanelRegistry.ActivePanels;
            Rect pixelRect = camera.pixelRect;
            float scaleX = targetWidth / Mathf.Max(1.0f, pixelRect.width);
            float scaleY = targetHeight / Mathf.Max(1.0f, pixelRect.height);
            var targetRect = new Rect(0.0f, 0.0f, targetWidth, targetHeight);

            for (int panelIndex = 0; panelIndex < panels.Count; panelIndex++)
            {
                UiBlurPanel panel = panels[panelIndex];
                if (panel.CanvasCamera != camera || !panel.IsVisible)
                {
                    continue;
                }

                int layerIndex = FindLayerIndex(SortingLayer.GetLayerValueFromID(panel.SortingLayerId), layerCount);
                if (layerIndex < 0)
                {
                    continue;
                }

                if (!panel.TryGetScreenRect(camera, out Rect screenRect))
                {
                    continue;
                }

                Rect rect = Rect.MinMaxRect(
                    (screenRect.xMin - pixelRect.x) * scaleX,
                    (screenRect.yMin - pixelRect.y) * scaleY,
                    (screenRect.xMax - pixelRect.x) * scaleX,
                    (screenRect.yMax - pixelRect.y) * scaleY);

                if (!Overlaps(rect, targetRect))
                {
                    continue;
                }

                ref LayerEntry layer = ref layerEntries[layerIndex];
                layer.bounds = layer.hasPanels ? Union(layer.bounds, rect) : rect;
                layer.hasPanels = true;
            }
        }

        private int FindLayerIndex(int sortingLayerValue, int layerCount)
        {
            int result = -1;
            for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
            {
                if (layerEntries[layerIndex].sortingLayerValue > sortingLayerValue)
                {
                    break;
                }

                result = layerIndex;
            }

            return result;
        }

        private void AddDrawPass(
            RenderGraph renderGraph,
            UniversalRenderingData renderingData,
            DrawingSettings drawingSettings,
            TextureHandle colorTexture,
            TextureHandle depthTexture,
            TextureHandle blurTexture,
            Vector4 screenParams,
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

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(DrawSampler.name, out DrawPassData passData, DrawSampler))
            {
                passData.rendererList = renderGraph.CreateRendererList(rendererListParams);
                passData.blurTexture = blurTexture;
                passData.screenParams = screenParams;

                builder.UseRendererList(passData.rendererList);
                builder.UseTexture(blurTexture);
                builder.SetRenderAttachment(colorTexture, 0);
                if (depthTexture.IsValid())
                {
                    builder.SetRenderAttachmentDepth(depthTexture);
                }

                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(DrawRenderFunc);
            }
        }

        private TextureHandle AddBlurChain(
            RenderGraph renderGraph,
            TextureHandle colorTexture,
            in TextureDesc colorDesc,
            in LayerEntry layer)
        {
            int targetWidth = colorDesc.width;
            int targetHeight = colorDesc.height;

            GetEffectiveLevels(layer, targetHeight, UiBlur.GetLayerStrength(layer.sortingLayerId),
                out int levels, out float offset);

            Rect fullRect = new Rect(0.0f, 0.0f, targetWidth, targetHeight);
            bool useScissor = false;
            if (limitToPanelBounds && layer.hasPanels)
            {
                float padding = GetKernelReach(levels, offset);
                Rect paddedRect = Rect.MinMaxRect(
                    Mathf.Max(0.0f, layer.bounds.xMin - padding),
                    Mathf.Max(0.0f, layer.bounds.yMin - padding),
                    Mathf.Min(targetWidth, layer.bounds.xMax + padding),
                    Mathf.Min(targetHeight, layer.bounds.yMax + padding));

                useScissor = paddedRect.width < targetWidth || paddedRect.height < targetHeight;
                if (useScissor)
                {
                    fullRect = paddedRect;
                }
            }

            GraphicsFormat format = GetBlurFormat();

            // Downsample chain: color -> L1 -> ... -> Ln.
            TextureHandle source = colorTexture;
            for (int level = 1; level <= levels; level++)
            {
                TextureHandle destination = CreateLevelTexture(renderGraph, targetWidth, targetHeight, level, format,
                    LevelTextureNames[level - 1]);
                AddBlurPass(renderGraph, DownSamplers[level - 1], PassDown, source, destination,
                    targetWidth, targetHeight, level - 1, level, fullRect, useScissor, offset);

                levelTextures[level] = destination;
                source = destination;
            }

            // Upsample chain in place: Ln -> Ln-1 -> ... -> L1. L1 (half resolution) is the result.
            for (int level = levels; level > 1; level--)
            {
                AddBlurPass(renderGraph, UpSamplers[level - 2], PassUp, levelTextures[level], levelTextures[level - 1],
                    targetWidth, targetHeight, level, level - 1, fullRect, useScissor, offset);
            }

            return levelTextures[1];
        }

        private readonly TextureHandle[] levelTextures = new TextureHandle[UiBlurLayerSettings.MaxLevels + 1];

        private void AddBlurPass(
            RenderGraph renderGraph,
            ProfilingSampler sampler,
            int shaderPass,
            TextureHandle source,
            TextureHandle destination,
            int targetWidth,
            int targetHeight,
            int sourceLevel,
            int destinationLevel,
            Rect fullRect,
            bool useScissor,
            float offset)
        {
            int sourceWidth = LevelSize(targetWidth, sourceLevel);
            int sourceHeight = LevelSize(targetHeight, sourceLevel);

            Rect sourceRect = LevelRect(fullRect, sourceLevel, sourceWidth, sourceHeight);
            Rect destinationRect = LevelRect(fullRect, destinationLevel,
                LevelSize(targetWidth, destinationLevel), LevelSize(targetHeight, destinationLevel));

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(sampler.name, out BlurPassData passData, sampler))
            {
                passData.material = material;
                passData.shaderPass = shaderPass;
                passData.source = source;
                passData.sourceTexelSize = new Vector4(1.0f / sourceWidth, 1.0f / sourceHeight, sourceWidth, sourceHeight);
                passData.sourceClamp = new Vector4(
                    (sourceRect.xMin + 0.5f) / sourceWidth,
                    (sourceRect.yMin + 0.5f) / sourceHeight,
                    (sourceRect.xMax - 0.5f) / sourceWidth,
                    (sourceRect.yMax - 0.5f) / sourceHeight);
                passData.blurParams = new Vector4(offset, 0.0f, 0.0f, 0.0f);
                passData.scissor = destinationRect;
                passData.useScissor = useScissor;

                builder.UseTexture(source);

                builder.SetRenderAttachment(destination, 0, AccessFlags.WriteAll);
                builder.SetRenderFunc(BlurRenderFunc);
            }
        }

        private static void ExecuteDraw(DrawPassData data, RasterGraphContext context)
        {
            RasterCommandBuffer cmd = context.cmd;
            cmd.SetGlobalVector(UiBlurScreenParamsId, data.screenParams);
            cmd.SetGlobalTexture(UiBlurTextureId, data.blurTexture);
            cmd.DrawRendererList(data.rendererList);
        }

        private static void ExecuteBlur(BlurPassData data, RasterGraphContext context)
        {
            RasterCommandBuffer cmd = context.cmd;
            MaterialPropertyBlock propertyBlock = BlurPropertyBlock;

            // Property block values are copied into the command buffer by DrawProcedural,
            // so one shared block is safe for every blur pass.
            propertyBlock.Clear();
            propertyBlock.SetTexture(BlitTextureId, (RTHandle)data.source);
            propertyBlock.SetVector(BlitScaleBiasId, FullScaleBias);
            propertyBlock.SetVector(BlurSourceTexelSizeId, data.sourceTexelSize);
            propertyBlock.SetVector(BlurSourceClampId, data.sourceClamp);
            propertyBlock.SetVector(BlurParamsId, data.blurParams);

            if (data.useScissor)
            {
                cmd.EnableScissorRect(data.scissor);
            }

            cmd.DrawProcedural(Matrix4x4.identity, data.material, data.shaderPass, MeshTopology.Triangles, 3, 1,
                propertyBlock);

            if (data.useScissor)
            {
                cmd.DisableScissorRect();
            }
        }

        /// <summary>
        /// Dual Kawase radius is proportional to 2^levels * (offset + 0.5). The authored radius is scaled by the
        /// target height and the layer strength, then split back into the fewest levels that keep the offset
        /// at or below the authored one. The radius changes continuously with strength and resolution.
        /// </summary>
        private void GetEffectiveLevels(in LayerEntry layer, int targetHeight, float strength, out int levels,
            out float offset)
        {
            float spread = layer.offset + 0.5f;
            float radius = Mathf.Clamp01(strength) * (targetHeight / referenceHeight) * (1 << layer.levels) * spread;

            levels = radius > spread
                ? Mathf.Clamp(Mathf.CeilToInt(Mathf.Log(radius / spread, 2.0f) - 0.001f), 1, UiBlurLayerSettings.MaxLevels)
                : 1;
            offset = Mathf.Clamp(radius / (1 << levels) - 0.5f, 0.0f, MaxOffset);
        }

        /// <summary>Distance in full resolution pixels the blur chain reads around a pixel.</summary>
        private static float GetKernelReach(int levels, float offset)
        {
            float reach = 0.0f;
            for (int level = 1; level <= levels; level++)
            {
                // Down pass into level reads the previous level at offset + 0.5 texels plus the bilinear footprint.
                reach += (offset + 1.5f) * (1 << (level - 1));
            }

            for (int level = 2; level <= levels; level++)
            {
                // Up pass from level reads it at 2 * (offset + 0.5) texels plus the bilinear footprint.
                reach += (2.0f * offset + 2.0f) * (1 << level);
            }

            return Mathf.Ceil(reach);
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

        private static Rect LevelRect(Rect fullRect, int level, int levelWidth, int levelHeight)
        {
            float scale = 1.0f / (1 << level);
            float xMin = Mathf.Clamp(Mathf.Floor(fullRect.xMin * scale), 0.0f, levelWidth);
            float yMin = Mathf.Clamp(Mathf.Floor(fullRect.yMin * scale), 0.0f, levelHeight);
            float xMax = Mathf.Clamp(Mathf.Ceil(fullRect.xMax * scale), 0.0f, levelWidth);
            float yMax = Mathf.Clamp(Mathf.Ceil(fullRect.yMax * scale), 0.0f, levelHeight);
            return Rect.MinMaxRect(xMin, yMin, Mathf.Max(xMax, xMin + 1.0f), Mathf.Max(yMax, yMin + 1.0f));
        }

        private static bool Overlaps(Rect a, Rect b)
        {
            return a.xMax > b.xMin && a.xMin < b.xMax && a.yMax > b.yMin && a.yMin < b.yMax;
        }

        private static Rect Union(Rect a, Rect b)
        {
            return Rect.MinMaxRect(
                Mathf.Min(a.xMin, b.xMin),
                Mathf.Min(a.yMin, b.yMin),
                Mathf.Max(a.xMax, b.xMax),
                Mathf.Max(a.yMax, b.yMax));
        }
    }
}
