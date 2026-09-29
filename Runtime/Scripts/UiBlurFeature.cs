using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace RottenEagle
{
    /// <summary>
    /// Draws Screen Space Camera UI after post processing, split by sorting layers into blur layers.
    /// Before each blur layer the current camera color is blurred (Dual Kawase) and published as
    /// the global texture <c>_UIBlurTexture</c>, which the "RottenEagle/UI/Blur Panel" material samples.
    /// Exclude <see cref="UiLayerMask"/> from the renderer Transparent Layer Mask, otherwise UI is drawn twice.
    /// </summary>
    public class UiBlurFeature : ScriptableRendererFeature
    {
        private const string BlurShaderName = "Hidden/RottenEagle/UiBlurDualKawase";

        [Tooltip("Unity layers of the UI rendered by this feature. Remove them from the renderer Transparent Layer Mask.")]
        [SerializeField] private LayerMask uiLayerMask = 1 << 5;

        [SerializeField] private RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingPostProcessing;

        [Tooltip("Blur layers, any order. Sorted by sorting layer order every frame.")]
        [SerializeField] private List<UiBlurLayerSettings> blurLayers = new List<UiBlurLayerSettings>();

        [Tooltip("Screen height the blur settings are authored for. Blur radius scales with the camera target height.")]
        [Min(1.0f)]
        [SerializeField] private float referenceHeight = 1080.0f;

        [Tooltip("Skip a blur layer when no visible UiBlurPanel is on it.")]
        [SerializeField] private bool skipLayersWithoutPanels = true;

        [Tooltip("Blur only the bounds of the visible UiBlurPanels of the layer (scissor).")]
        [SerializeField] private bool limitToPanelBounds = true;

        [Tooltip("Bind the camera depth-stencil while drawing UI so stencil Mask components work.")]
        [SerializeField] private bool supportStencilMasks = true;

        [SerializeField] [HideInInspector] private Shader blurShader;

        private Material blurMaterial;
        private UiBlurLayersPass layersPass;

        public LayerMask UiLayerMask => uiLayerMask;

        public IReadOnlyList<UiBlurLayerSettings> BlurLayers => blurLayers;

        public override void Create()
        {
            layersPass = new UiBlurLayersPass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
            {
                return;
            }

            if (!TryCreateMaterial())
            {
                return;
            }

            layersPass.renderPassEvent = injectionPoint;
            layersPass.Setup(
                blurMaterial,
                blurLayers,
                uiLayerMask,
                referenceHeight,
                skipLayersWithoutPanels,
                limitToPanelBounds,
                supportStencilMasks);
            renderer.EnqueuePass(layersPass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(blurMaterial);
            blurMaterial = null;
        }

        private bool TryCreateMaterial()
        {
            if (blurShader == null)
            {
                blurShader = Shader.Find(BlurShaderName);
            }

            if (blurShader == null)
            {
                return false;
            }

            if (blurMaterial == null || blurMaterial.shader != blurShader)
            {
                CoreUtils.Destroy(blurMaterial);
                blurMaterial = CoreUtils.CreateEngineMaterial(blurShader);
            }

            return true;
        }
    }
}
