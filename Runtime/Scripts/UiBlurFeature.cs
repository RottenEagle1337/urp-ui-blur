using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace RottenEagle
{
    /// <summary>
    /// Builds a blur pyramid of the camera color and draws Screen Space Camera UI after post processing.
    /// Graphics with the "RottenEagle/UI/Blur Panel" material sample the pyramid by their _BlurStrength.
    /// UI is captured into the pyramid in hierarchy order: a panel blurs the scene and the UI drawn before it,
    /// UI drawn after it stays on top.
    /// Exclude <see cref="UiLayerMask"/> from the renderer Transparent Layer Mask, otherwise UI is drawn twice.
    /// </summary>
    public class UiBlurFeature : ScriptableRendererFeature
    {
        public const int MaxLevels = 7;

        private const string PyramidShaderName = "Hidden/RottenEagle/UiBlurPyramid";

        [Tooltip("Pyramid levels at the reference height. _BlurStrength = 1 has a radius of about " +
                 "2^levels pixels. One raster pass per level.")]
        [Range(1, MaxLevels)]
        [SerializeField] private int maxBlurLevels = 5;

        [Tooltip("Screen height the blur strength is authored for. The radius scales with the target height.")]
        [Min(1.0f)]
        [SerializeField] private float referenceHeight = 1080.0f;

        [Tooltip("Unity layers of the UI drawn by this feature. Remove them from the renderer Transparent Layer Mask.")]
        [SerializeField] private LayerMask uiLayerMask = 1 << 5;

        [Tooltip("Bind the camera depth-stencil while drawing UI so stencil Mask components work.")]
        [SerializeField] private bool supportStencilMasks = true;

        [Tooltip("Also build the blur pyramid for cameras that render into a RenderTexture (minimaps, portals).")]
        [SerializeField] private bool blurRenderTextureCameras;

        [SerializeField] private RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingPostProcessing;

        [SerializeField] [HideInInspector] private Shader pyramidShader;

        private Material pyramidMaterial;
        private UiBlurPyramidPass pyramidPass;

        public LayerMask UiLayerMask => uiLayerMask;

        public override void Create()
        {
            pyramidPass = new UiBlurPyramidPass();
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

            pyramidPass.renderPassEvent = injectionPoint;
            pyramidPass.Setup(pyramidMaterial, maxBlurLevels, referenceHeight, uiLayerMask, supportStencilMasks,
                blurRenderTextureCameras);
            renderer.EnqueuePass(pyramidPass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(pyramidMaterial);
            pyramidMaterial = null;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Serialize the hidden shader reference so it is included in player builds.
            if (pyramidShader == null)
            {
                pyramidShader = Shader.Find(PyramidShaderName);
            }
        }
#endif

        private bool TryCreateMaterial()
        {
            if (pyramidShader == null)
            {
                pyramidShader = Shader.Find(PyramidShaderName);
            }

            if (pyramidShader == null)
            {
                return false;
            }

            if (pyramidMaterial == null || pyramidMaterial.shader != pyramidShader)
            {
                CoreUtils.Destroy(pyramidMaterial);
                pyramidMaterial = CoreUtils.CreateEngineMaterial(pyramidShader);
            }

            return true;
        }
    }
}
