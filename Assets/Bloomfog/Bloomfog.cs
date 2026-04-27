using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class Bloomfog : ScriptableRendererFeature
{
    //These are static fields for graphics settings to access
    public static bool Enabled = true;
    public static int Quality = 2;

    private static readonly int fogTextureToScreenRatioID = Shader.PropertyToID("_FogTextureToScreenRatio");
    private static readonly int thresholdID = Shader.PropertyToID("_Threshold");
    private static readonly int brightnessMultID = Shader.PropertyToID("_BrightnessMult");
    private static readonly int offsetID = Shader.PropertyToID("_Offset");
    private static readonly int blurAlphaID = Shader.PropertyToID("_BlurAlpha");

    [System.Serializable]
    public class BloomfogSettings
    {
        public Material prepassMaterial;

        [Space]
        public float bloomCaptureExtraFov = 0f;
        public float threshold = 1f;
        public float brightnessMult = 1f;

        [Header("Blur Settings")]
        public Material blurMaterial;

        [Header("Output Settings")]
        public string outputTextureName;

        [Space]
        public BloomfogQualityPreset[] qualityPresets;

        [System.NonSerialized] public int textureWidth;
        [System.NonSerialized] public int textureHeight;
        [System.NonSerialized] public int actualDownsamplePasses;
        [System.NonSerialized] private string cachedOutputTextureName;
        [System.NonSerialized] private int outputTextureID;

        public BloomfogQualityPreset currentQualityPreset => qualityPresets[Mathf.Clamp(Quality, 0, qualityPresets.Length - 1)];
        public bool hasOutputTexture => !string.IsNullOrEmpty(outputTextureName);
        public int outputTexturePropertyID
        {
            get
            {
                if(cachedOutputTextureName != outputTextureName)
                {
                    cachedOutputTextureName = outputTextureName;
                    outputTextureID = hasOutputTexture ? Shader.PropertyToID(outputTextureName) : 0;
                }

                return outputTextureID;
            }
        }
    }

    [System.Serializable]
    public class BloomfogQualityPreset
    {
        public int referenceScreenHeight = 1024;
        [Min(2)] public int downsamplePasses = 5;
        [Min(0f)] public float upsampleBlend = 20f;
        [Min(0)] public int ignoreUpsampleIndex = 1;
    }

    [SerializeField] private BloomfogSettings settings = new BloomfogSettings();

    private BloomFogPass bloomFogPass;
    private Camera mainCamera;


    public override void Create()
    {
        bloomFogPass = new BloomFogPass(settings);
        bloomFogPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }


    public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
    {
        Camera mainCamera = GetMainCamera();
        Camera renderCamera = cameraData.camera;

        BloomfogQualityPreset qualitySettings = settings.currentQualityPreset;

        //Update the camera field of view
        renderCamera.fieldOfView = Mathf.Clamp(mainCamera.fieldOfView + settings.bloomCaptureExtraFov, 30, 160);
        renderCamera.allowMSAA = false;

        float verticalFov = Mathf.Deg2Rad * renderCamera.fieldOfView;
        float horizontalFov = 2 * Mathf.Atan(Mathf.Tan(verticalFov / 2) * renderCamera.aspect);

        //Calculate the new texture ratio based on camera fov
        float originalVertFov = Mathf.Deg2Rad * mainCamera.fieldOfView;
        float screenPlaneDistance = qualitySettings.referenceScreenHeight / 2 / Mathf.Tan(originalVertFov / 2);

        //Set the new texture size
        float textureWidth = Mathf.Tan(horizontalFov / 2) * screenPlaneDistance * 2;
        float textureHeight = Mathf.Tan(verticalFov / 2) * screenPlaneDistance * 2;

        float referenceWidth = qualitySettings.referenceScreenHeight * mainCamera.aspect;
        float widthRatio = referenceWidth / textureWidth;
        float heightRatio = (float)qualitySettings.referenceScreenHeight / textureHeight;

        // Debug.Log($"fov: {verticalFov} horizontal: {horizontalFov} width: {settings.textureWidth} height: {settings.textureHeight} ratio: {widthRatio}, {heightRatio}");

        settings.textureHeight = qualitySettings.referenceScreenHeight;
        settings.textureWidth = (int)referenceWidth;

        Shader.SetGlobalVector(fogTextureToScreenRatioID, new Vector2(widthRatio, heightRatio));
    }


    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if(settings.blurMaterial && settings.prepassMaterial)
        {
            renderer.EnqueuePass(bloomFogPass);
        }
    }


    private Camera GetMainCamera()
    {
        if(!mainCamera || !mainCamera.isActiveAndEnabled || !mainCamera.CompareTag("MainCamera"))
        {
            mainCamera = Camera.main;
        }

        return mainCamera;
    }


    private class BloomFogPass : ScriptableRenderPass
    {
        private BloomfogSettings settings;

        private int[] tempIDs;
        private RenderTargetIdentifier[] tempRTs;


        public BloomFogPass(BloomfogSettings fogSettings)
        {
            settings = fogSettings;
        }


        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            BloomfogQualityPreset qualitySettings = settings.currentQualityPreset;

            int width = settings.textureWidth;
            int height = settings.textureHeight;

            //Clamp the blur passes so we don't downsample below a 2x2 texture
            int minDimension = Mathf.Min(width, height);
            int maxDownsample = Mathf.FloorToInt(Mathf.Log(minDimension, 2));
            settings.actualDownsamplePasses = Mathf.Clamp(qualitySettings.downsamplePasses, 2, maxDownsample);

            EnsureTempTargets(settings.actualDownsamplePasses);
        }


        private void EnsureTempTargets(int count)
        {
            if(tempIDs != null && tempIDs.Length >= count)
            {
                return;
            }

            tempIDs = new int[count];
            tempRTs = new RenderTargetIdentifier[count];

            for(int i = 0; i < count; i++)
            {
                tempIDs[i] = Shader.PropertyToID("tempBlurRT" + i);
                tempRTs[i] = new RenderTargetIdentifier(tempIDs[i]);
            }
        }


        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if(!Enabled)
            {
                if(settings.hasOutputTexture)
                {
                    //Bloomfog shouldn't be used, just output a black texture
                    Shader.SetGlobalTexture(settings.outputTexturePropertyID, Texture2D.blackTexture);
                }
                return;
            }

            BloomfogQualityPreset qualitySettings = settings.currentQualityPreset;
            CommandBuffer cmd = CommandBufferPool.Get("BloomfogBlur");

            //Create our temporary render textures for blurring
            int downsample = 2;
            for(int i = 0; i < settings.actualDownsamplePasses; i++)
            {
                cmd.GetTemporaryRT(tempIDs[i], settings.textureWidth / downsample, settings.textureHeight / downsample, 0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR);

                //Clear the texture content in case it's been carried over from the last frame
                cmd.SetRenderTarget(tempRTs[i]);
                cmd.ClearRenderTarget(true, true, Color.black);

                downsample *= 2;
            }

            //Copy the source into the first temp texture, applying brightness threshold
            cmd.SetGlobalFloat(thresholdID, settings.threshold);
            cmd.SetGlobalFloat(brightnessMultID, settings.brightnessMult);

            cmd.Blit(renderingData.cameraData.targetTexture, tempRTs[0], settings.prepassMaterial);

            //Blit the source image into smaller and smaller textures, applying some blur
            cmd.SetGlobalFloat(offsetID, 0.5f);
            cmd.SetGlobalFloat(blurAlphaID, 1f);
            for(int i = 1; i < settings.actualDownsamplePasses; i++)
            {
                cmd.Blit(tempRTs[i - 1], tempRTs[i], settings.blurMaterial);
            }

            //Blit back up the chain, bringing the blurred image to the half res RT
            cmd.SetGlobalFloat(offsetID, 1f);
            for(int i = settings.actualDownsamplePasses - 1; i > 0; i--)
            {
                //Blend the low res texture with alpha, to create a custom falloff of brightness
                //Don't blend high res images to avoid reintroducing unblurred details
                float alpha = i <= qualitySettings.ignoreUpsampleIndex ? 1f : Mathf.Pow(0.5f, i / qualitySettings.upsampleBlend);
                cmd.SetGlobalFloat(blurAlphaID, alpha);

                cmd.Blit(tempRTs[i], tempRTs[i - 1], settings.blurMaterial);
            }

            if(settings.hasOutputTexture)
            {
                cmd.SetGlobalTexture(settings.outputTexturePropertyID, tempRTs[0]);
            }

            //Release our temporary render textures
            //Don't release texture 0 because it's our output texture
            for(int i = 1; i < settings.actualDownsamplePasses; i++)
            {
                cmd.ReleaseTemporaryRT(tempIDs[i]);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            CommandBufferPool.Release(cmd);
        }
    }
}