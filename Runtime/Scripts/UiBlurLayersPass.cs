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
        private const int PassUpMix = 2;
        private const float MinStrength = 0.001f;

        private static readonly int BlitTextureId = Shader.PropertyToID("_BlitTexture");
        private static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
        private static readonly int BlurMixTextureId = Shader.PropertyToID("_BlurMixTexture");
        private static readonly int BlurSourceTexelSizeId = Shader.PropertyToID("_BlurSourceTexelSize");
        private static readonly int BlurSourceClampId = Shader.PropertyToID("_BlurSourceClamp");
        private static readonly int BlurParamsId = Shader.PropertyToID("_BlurParams");
        private static readonly int UiBlurTextureId = Shader.PropertyToID("_UIBlurTexture");
        private static readonly int UiBlurScreenParamsId = Shader.PropertyToID("_UIBlurScreenParams");

        private static readonly string[] DownPassNames =
        {
            "UI Blur Down 1/2", "UI Blur Down 1/4", "UI Blur Down 1/8",
            "UI Blur Down 1/16", "UI Blur Down 1/32", "UI Blur Down 1/64"
        };

        private static readonly string[] UpPassNames =
        {
            "UI Blur Up 1/2", "UI Blur Up 1/4", "UI Blur Up 1/8",
            "UI Blur Up 1/16", "UI Blur Up 1/32", "UI Blur Up 1/64"
        };

        private static readonly string[] LevelTextureNames =
        {
            "_UIBlurLevel1", "_UIBlurLevel2", "_UIBlurLevel3",
            "_UIBlurLevel4", "_UIBlurLevel5", "_UIBlurLevel6"
        };

        private const string DrawPassName = "UI Blur Draw UI";
        private const string OutputTextureName = "_UIBlurTexture";

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
            public TextureHandle mixSource;
            public bool hasMixSource;
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

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(DrawPassName, out DrawPassData passData))
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

            float strength = UiBlur.GetLayerStrength(layer.sortingLayerId);
            GetEffectiveLevels(layer, targetHeight, strength, out int levels, out float offset);

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
            TextureHandle output = CreateLevelTexture(renderGraph, targetWidth, targetHeight, 1, format, OutputTextureName);

            if (levels == 1)
            {
                AddBlurPass(renderGraph, DownPassNames[0], PassDown, colorTexture, TextureHandle.nullHandle, output,
                    targetWidth, targetHeight, 0, 1, fullRect, useScissor, offset, 1.0f);
                return output;
            }

            // Downsample chain: color -> L1 -> ... -> Ln.
            TextureHandle source = colorTexture;
            int sourceLevel = 0;
            TextureHandle firstLevel = TextureHandle.nullHandle;
            for (int level = 1; level <= levels; level++)
            {
                TextureHandle destination = CreateLevelTexture(renderGraph, targetWidth, targetHeight, level, format,
                    LevelTextureNames[level - 1]);
                AddBlurPass(renderGraph, DownPassNames[level - 1], PassDown, source, TextureHandle.nullHandle,
                    destination, targetWidth, targetHeight, sourceLevel, level, fullRect, useScissor, offset, 1.0f);

                if (level == 1)
                {
                    firstLevel = destination;
                }

                levelTextures[level] = destination;
                source = destination;
                sourceLevel = level;
            }

            // Upsample chain in place: Ln -> Ln-1 -> ... -> L2, then L2 -> output mixed with L1.
            for (int level = levels; level > 2; level--)
            {
                TextureHandle destination = levelTextures[level - 1];
                AddBlurPass(renderGraph, UpPassNames[level - 2], PassUp, levelTextures[level], TextureHandle.nullHandle,
                    destination, targetWidth, targetHeight, level, level - 1, fullRect, useScissor, offset, 1.0f);
            }

            AddBlurPass(renderGraph, UpPassNames[0], PassUpMix, levelTextures[2], firstLevel, output,
                targetWidth, targetHeight, 2, 1, fullRect, useScissor, offset, strength);

            return output;
        }

        private readonly TextureHandle[] levelTextures = new TextureHandle[UiBlurLayerSettings.MaxLevels + 1];

        private void AddBlurPass(
            RenderGraph renderGraph,
            string passName,
            int shaderPass,
            TextureHandle source,
            TextureHandle mixSource,
            TextureHandle destination,
            int targetWidth,
            int targetHeight,
            int sourceLevel,
            int destinationLevel,
            Rect fullRect,
            bool useScissor,
            float offset,
            float mix)
        {
            int sourceWidth = LevelSize(targetWidth, sourceLevel);
            int sourceHeight = LevelSize(targetHeight, sourceLevel);

            Rect sourceRect = LevelRect(fullRect, sourceLevel, sourceWidth, sourceHeight);
            Rect destinationRect = LevelRect(fullRect, destinationLevel,
                LevelSize(targetWidth, destinationLevel), LevelSize(targetHeight, destinationLevel));

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(passName, out BlurPassData passData))
            {
                passData.material = material;
                passData.shaderPass = shaderPass;
                passData.source = source;
                passData.mixSource = mixSource;
                passData.hasMixSource = mixSource.IsValid();
                passData.sourceTexelSize = new Vector4(1.0f / sourceWidth, 1.0f / sourceHeight, sourceWidth, sourceHeight);
                passData.sourceClamp = new Vector4(
                    (sourceRect.xMin + 0.5f) / sourceWidth,
                    (sourceRect.yMin + 0.5f) / sourceHeight,
                    (sourceRect.xMax - 0.5f) / sourceWidth,
                    (sourceRect.yMax - 0.5f) / sourceHeight);
                passData.blurParams = new Vector4(offset, mix, 0.0f, 0.0f);
                passData.scissor = destinationRect;
                passData.useScissor = useScissor;

                builder.UseTexture(source);
                if (passData.hasMixSource)
                {
                    builder.UseTexture(mixSource);
                }

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
            if (data.hasMixSource)
            {
                propertyBlock.SetTexture(BlurMixTextureId, (RTHandle)data.mixSource);
            }

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
        /// Converts authored levels/offset (reference height) to the current target height:
        /// whole octaves become extra or fewer levels, the remainder scales the offset.
        /// </summary>
        private void GetEffectiveLevels(in LayerEntry layer, int targetHeight, float strength, out int levels,
            out float offset)
        {
            if (strength <= MinStrength)
            {
                levels = 1;
                offset = 0.0f;
                return;
            }

            float scale = targetHeight / referenceHeight;
            int extraLevels = Mathf.RoundToInt(Mathf.Log(scale, 2.0f));
            levels = Mathf.Clamp(layer.levels + extraLevels, 1, UiBlurLayerSettings.MaxLevels);
            offset = layer.offset * scale / Mathf.Pow(2.0f, levels - layer.levels);

            if (levels == 1)
            {
                offset *= strength;
            }
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
