using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace RottenEagle.Tests
{
    public class UiBlurTests
    {
        private const string PyramidShaderName = "Hidden/RottenEagle/UiBlurPyramid";
        private const string PanelShaderName = "RottenEagle/UI/Blur Panel";
        private const string PanelMaterialPath = "Packages/com.rotteneagle.urp-ui-blur/Runtime/Materials/UiBlurPanel.mat";
        private const string CaptureKeyword = "_UI_BLUR_CAPTURE";

        [Test]
        public void PyramidShader_CompilesWithoutErrors()
        {
            Shader shader = Shader.Find(PyramidShaderName);

            Assert.That(shader, Is.Not.Null);
            AssertNoErrors(shader);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PanelShader_CompilesWithoutErrors(bool captureVariant)
        {
            Shader shader = Shader.Find(PanelShaderName);
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            try
            {
                if (captureVariant)
                {
                    material.EnableKeyword(CaptureKeyword);
                }

                ShaderUtil.CompilePass(material, 0, true);
                AssertNoErrors(shader);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void PanelMaterial_DefaultsToFullStrength()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(PanelMaterialPath);

            Assert.That(material, Is.Not.Null);
            Assert.That(material.shader.name, Is.EqualTo(PanelShaderName));
            Assert.That(material.GetFloat("_BlurStrength"), Is.EqualTo(1.0f));
        }

        [Test]
        public void Feature_CreatesWithoutErrors()
        {
            var feature = ScriptableObject.CreateInstance<UiBlurFeature>();
            try
            {
                feature.Create();
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(feature);
            }
        }

        private static void AssertNoErrors(Shader shader)
        {
            string[] errors = ShaderUtil.GetShaderMessages(shader)
                .Where(message => message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                .Select(message => message.message + " (line " + message.line + ")")
                .ToArray();

            Assert.That(errors, Is.Empty, string.Join("\n", errors));
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
        }
    }
}
