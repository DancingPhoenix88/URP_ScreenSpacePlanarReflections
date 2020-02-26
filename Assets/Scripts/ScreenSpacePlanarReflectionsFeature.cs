﻿using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Scripting.APIUpdating;


public class ScreenSpacePlanarReflectionsFeature : ScriptableRendererFeature
{
    [System.Serializable, ReloadGroup]
    public class ScreenSpacePlanarReflectionsSettings
    {
        
        public RenderPassEvent Event = RenderPassEvent.AfterRenderingOpaques;

        [Reload("Shaders/ReflectionShader.compute")]
        public ComputeShader reflectionCS;

        [Reload("Shaders/ReflectionShader.shader")]
        public Shader reflectionShader;

        public Quaternion PlaneRotation;
        public Vector3 PlaneLocation;

        public bool ApplyEdgeStretch = true; 
        public bool ApplyBlur = true;


        public bool RenderReflectiveLayer = true;
        //[ConditionalField("RenderReflectiveLayer")]
        public LayerMask ReflectiveSurfaceLayer;

        public bool StencilOptimization = true;
        [Range(1,255)]
        public int StencilValue = 255;

        //public StencilStateData stencilSettings = new StencilStateData();
        public bool needsStencilPass
        {
            get { return StencilOptimization && ReflectiveSurfaceLayer != 0; }
        }

        public bool needsRenderReflective
        {
            get { return RenderReflectiveLayer && ReflectiveSurfaceLayer != 0; }
        }

    }

    class StencilRenderPass : ScriptableRenderPass
    {
        private const string m_ProfilerTag = "ReflectionsFeature_Stencil";
        private ScreenSpacePlanarReflectionsSettings m_Settings;
        RenderStateBlock m_RenderStateBlock;
        FilteringSettings m_FilteringSettings;

        List<ShaderTagId> m_ShaderTagIdList = new List<ShaderTagId>();


        public StencilRenderPass(ScreenSpacePlanarReflectionsSettings settings)
        {
            m_Settings = settings;

            m_RenderStateBlock = new RenderStateBlock();
            m_RenderStateBlock.mask |= RenderStateMask.Depth;
            m_RenderStateBlock.depthState = new DepthState(false, CompareFunction.Always);

            m_RenderStateBlock.mask |= RenderStateMask.Stencil;

            StencilState stencilState = StencilState.defaultValue;
            stencilState.enabled = true;
            stencilState.SetCompareFunction(CompareFunction.Always);
            stencilState.SetPassOperation(StencilOp.Replace);
            stencilState.SetFailOperation(StencilOp.Zero);
            stencilState.SetZFailOperation(StencilOp.Keep);

            m_RenderStateBlock.stencilReference = settings.StencilValue;
            m_RenderStateBlock.stencilState = stencilState;

            m_FilteringSettings = new FilteringSettings(RenderQueueRange.opaque, settings.ReflectiveSurfaceLayer);

            m_ShaderTagIdList.Add(new ShaderTagId("DepthOnly"));

        }

        // Here you can implement the rendering logic.
        // Use <c>ScriptableRenderContext</c> to issue drawing commands or execute command buffers
        // https://docs.unity3d.com/ScriptReference/Rendering.ScriptableRenderContext.html
        // You don't have to call ScriptableRenderContext.submit, the render pipeline will call it at specific points in the pipeline.
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // Update the Value
            m_RenderStateBlock.stencilReference = m_Settings.StencilValue;
            m_FilteringSettings.layerMask = m_Settings.ReflectiveSurfaceLayer;

            // Draw settings
            SortingCriteria sortingCriteria = renderingData.cameraData.defaultOpaqueSortFlags;
            DrawingSettings drawingSettings = CreateDrawingSettings(m_ShaderTagIdList, ref renderingData, sortingCriteria);

            // Render any 'reflective surfaces' with the stencil value,
            // will use this to generate the texture later
            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
            using (new ProfilingSample(cmd, m_ProfilerTag))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref m_FilteringSettings,
                    ref m_RenderStateBlock);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }


    class ReflectionsRenderPass : ScriptableRenderPass
    {
        private const string m_ProfilerTag = "ReflectionsFeature_Render";
        private ScreenSpacePlanarReflectionsSettings m_Settings;

        RenderTargetHandle m_ScreenSpacePlanarReflection;
        RenderTargetHandle m_ScreenSpacePlanarReflectionBuffer;
        RenderTexture m_ScreenSpacePlanarReflectionTexture;
        RenderTexture m_ScreenSpacePlanarReflectionTextureBuffer;
        RenderTextureDescriptor m_RenderTextureDescriptor;
        RenderTextureDescriptor m_RenderTextureBufferDescriptor;

        RenderTargetIdentifier m_CameraColorTarget;
        RenderTargetIdentifier m_CameraDepthTarget;

        private const string _NO_MSAA = "_NO_MSAA";
        private const string _MSAA_2 = "_MSAA_2";
        private const string _MSAA_4 = "_MSAA_4";
        private const string COLOR_ATTACHMENT = "COLOR_ATTACHMENT";


        RenderTargetHandle[] m_Temp = new RenderTargetHandle[2];

        Vector2Int m_Size;
        Vector2Int m_ThreadSize;

        ComputeShader m_ReflectionShaderCS;
        Shader m_ReflectionShader;
        Material m_ReflectionMaterial;

        int m_ClearKernal;
        int m_RenderKernal;
        int m_PropertyResult;
        int m_PropertyResultSize;
        int m_PropertyDepth;
        int m_PropertyInvVP;
        int m_PropertyVP;
        int m_PropertyReflectionData;
        int m_PropertySSPRBufferRange;
        int m_PropertyMainTex;

        Matrix4x4 m_InvVP;
        Matrix4x4 m_VP;
        Vector4[] m_ReflectionData;

        RenderStateBlock m_RenderStateBlock;

        bool bStencilValid = true;

        public ReflectionsRenderPass(ScreenSpacePlanarReflectionsSettings settings)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            m_Settings = settings;
            m_ScreenSpacePlanarReflection.Init("_ScreenSpacePlanarReflectionTexture");
            m_ScreenSpacePlanarReflectionBuffer.Init("_ScreenSpacePlanarReflectionBuffer");

            m_Temp[0].Init("_SSPRTempA");
            m_Temp[1].Init("_SSPRTempB");

            m_RenderTextureBufferDescriptor = new RenderTextureDescriptor(512,512,RenderTextureFormat.RInt, 0);
            m_RenderTextureBufferDescriptor.enableRandomWrite = true;

            m_RenderTextureDescriptor = new RenderTextureDescriptor(512, 512, RenderTextureFormat.RGB111110Float, 0);
            m_Size = new Vector2Int(512,512);
            m_ThreadSize = new Vector2Int(1, 1);
            m_ReflectionShaderCS = settings.reflectionCS;
            m_ReflectionShader = settings.reflectionShader;

            if(m_ReflectionShaderCS!=null)
            {
                m_ClearKernal = m_ReflectionShaderCS.FindKernel("CSClear");
                m_RenderKernal = m_ReflectionShaderCS.FindKernel("CSMain");
            }

            if(m_ReflectionShader != null)
            {
                m_ReflectionMaterial = new Material(m_ReflectionShader);
            }

            m_PropertyResult = Shader.PropertyToID("Result");
            m_PropertyResultSize = Shader.PropertyToID("ResultSize");
            m_PropertyDepth = Shader.PropertyToID("_CameraDepthTexture");
            m_PropertyInvVP = Shader.PropertyToID("InverseViewProjection");
            m_PropertyVP = Shader.PropertyToID("ViewProjection");
            m_PropertyReflectionData = Shader.PropertyToID("ReflectionData");
            m_PropertySSPRBufferRange = Shader.PropertyToID("_SSPRBufferRange");
            m_PropertyMainTex = Shader.PropertyToID("_MainTex");

            m_InvVP = new Matrix4x4();
            m_VP = new Matrix4x4();
            m_ReflectionData = new Vector4[2] { new Vector4(), new Vector4() };
            bStencilValid = true;
        }

        public void SetTargets(ScriptableRenderer renderer)
        {
            m_CameraColorTarget = renderer.cameraColorTarget;
            m_CameraDepthTarget = renderer.cameraDepth;

            // if it matches with Camera Target then it should be equal to whatever CameraColorTarget is
            if(m_CameraDepthTarget.Equals(RenderTargetHandle.CameraTarget.Identifier()))
            {
                m_CameraDepthTarget = m_CameraColorTarget;
            }
        }

        // This method is called before executing the render pass.
        // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
        // When empty this render pass will render to the active camera render target.
        // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
        // The render pipeline will ensure target setup and clearing happens in an performance manner.
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            m_Size.x = cameraTextureDescriptor.width;
            m_Size.y = cameraTextureDescriptor.height;
            m_RenderTextureBufferDescriptor.width = m_Size.x;
            m_RenderTextureBufferDescriptor.height = m_Size.y;
            m_RenderTextureDescriptor.width = m_Size.x;
            m_RenderTextureDescriptor.height = m_Size.y;
            m_RenderTextureDescriptor.colorFormat = cameraTextureDescriptor.colorFormat;

            cmd.GetTemporaryRT(m_ScreenSpacePlanarReflection.id, m_RenderTextureDescriptor);
            cmd.GetTemporaryRT(m_ScreenSpacePlanarReflectionBuffer.id, m_RenderTextureBufferDescriptor);

            bStencilValid = !cameraTextureDescriptor.bindMS;

            m_ThreadSize.x = m_Size.x / 32 + (m_Size.x % 32 > 0 ? 1 : 0);
            m_ThreadSize.y = m_Size.y / 32 + (m_Size.y % 32 > 0 ? 1 : 0);
        }

        // Here you can implement the rendering logic.
        // Use <c>ScriptableRenderContext</c> to issue drawing commands or execute command buffers
        // https://docs.unity3d.com/ScriptReference/Rendering.ScriptableRenderContext.html
        // You don't have to call ScriptableRenderContext.submit, the render pipeline will call it at specific points in the pipeline.
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (m_ReflectionShaderCS == null || m_ReflectionShader == null)
            {
                return;
            }

            Camera camera = renderingData.cameraData.camera;

            // Calculate Our Plane and Matrices
            Vector3 temp;
            temp = m_Settings.PlaneRotation * Vector3.up;
            m_ReflectionData[0].x = temp.x;
            m_ReflectionData[0].y = temp.y;
            m_ReflectionData[0].z = temp.z;
            m_ReflectionData[0].w = -Vector3.Dot(temp, m_Settings.PlaneLocation);
            m_ReflectionData[1].x = 1.0f / m_Size.x;
            m_ReflectionData[1].y = 1.0f / m_Size.y;
            m_ReflectionData[1].z = m_ReflectionData[1].x * 0.5f;
            m_ReflectionData[1].w = m_ReflectionData[1].y * 0.5f;

            m_VP = renderingData.cameraData.camera.projectionMatrix * renderingData.cameraData.camera.worldToCameraMatrix;
            m_InvVP = m_VP.inverse;


            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
            using (new ProfilingSample(cmd, m_ProfilerTag))
            {

                
                

                // need to run compute shader to clear
                cmd.SetComputeTextureParam(m_ReflectionShaderCS, m_ClearKernal, m_PropertyResult, m_ScreenSpacePlanarReflectionBuffer.Identifier());
                cmd.SetComputeIntParams(m_ReflectionShaderCS, m_PropertyResultSize, m_Size.x, m_Size.y);
                cmd.DispatchCompute(m_ReflectionShaderCS, m_ClearKernal, m_ThreadSize.x, m_ThreadSize.y, 1);

                
                // need to run the reflection compute to find image coords
                cmd.SetComputeTextureParam(m_ReflectionShaderCS, m_RenderKernal, m_PropertyResult, m_ScreenSpacePlanarReflectionBuffer.Identifier());
                //cmd.SetComputeTextureParam(m_ReflectionShaderCS, m_RenderKernal, m_PropertyDepth, BuiltinRenderTextureType.Depth);
                cmd.SetComputeIntParams(m_ReflectionShaderCS, m_PropertyResultSize, m_Size.x, m_Size.y);
                cmd.SetComputeMatrixParam(m_ReflectionShaderCS, m_PropertyInvVP, m_InvVP);
                cmd.SetComputeMatrixParam(m_ReflectionShaderCS, m_PropertyVP, m_VP);
                cmd.SetComputeVectorArrayParam(m_ReflectionShaderCS, m_PropertyReflectionData, m_ReflectionData);

                cmd.DispatchCompute(m_ReflectionShaderCS, m_RenderKernal, m_ThreadSize.x, m_ThreadSize.y, 1);
                GraphicsFence fence = cmd.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.ComputeProcessing);
                cmd.WaitOnAsyncGraphicsFence(fence);


                if (m_Settings.ApplyEdgeStretch)
                {
                    // now we can render into the temporary texture where the stencil is set or full screen depending if the optimisation is on
                    cmd.GetTemporaryRT(m_Temp[0].id, m_RenderTextureDescriptor);

                    // render
                    RenderReflection(cmd, m_Temp[0].Identifier(), camera);


                    if (m_Settings.ApplyBlur)
                    {
                        // will need one more temporary texture
                        cmd.GetTemporaryRT(m_Temp[1].id, m_RenderTextureDescriptor);

                        // we blit and squish the edges to make a full image
                        RenderSquishEdges(cmd, m_Temp[1].Identifier(), m_Temp[0].Identifier());
                        // we apply a simple box filter blur
                        RenderBlur(cmd, m_ScreenSpacePlanarReflection.Identifier(), m_Temp[1].Identifier());

                        cmd.ReleaseTemporaryRT(m_Temp[1].id);

                    }
                    else
                    {
                        // we blit and squish the edges to make a full image
                        RenderSquishEdges(cmd, m_ScreenSpacePlanarReflection.Identifier(), m_Temp[0].Identifier());
                    }
                    cmd.ReleaseTemporaryRT(m_Temp[0].id);
                }
                else
                {

                    if (m_Settings.ApplyBlur)
                    {
                        cmd.GetTemporaryRT(m_Temp[0].id, m_RenderTextureDescriptor);
                        // now we can render into the temporary texture where the stencil is set or full screen depending if the optimisation is on
                        RenderReflection(cmd, m_Temp[0].Identifier(), camera);
                        // render blur
                        RenderBlur(cmd, m_ScreenSpacePlanarReflection.Identifier(), m_Temp[0].Identifier());

                        cmd.ReleaseTemporaryRT(m_Temp[0].id);
                    }
                    else
                    {
                        // now we can render into the temporary texture where the stencil is set or full screen depending if the optimisation is on
                        RenderReflection(cmd, m_ScreenSpacePlanarReflection.Identifier(), camera);
                    }
                }


                // restore target state
                cmd.SetRenderTarget(m_CameraColorTarget, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, m_CameraDepthTarget, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                cmd.SetGlobalTexture(m_ScreenSpacePlanarReflection.id, m_ScreenSpacePlanarReflection.Identifier());

            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        /// Cleanup any allocated resources that were created during the execution of this render pass.
        public override void FrameCleanup(CommandBuffer cmd)
        {
            if (cmd == null)
                throw new System.ArgumentNullException("cmd");

            cmd.ReleaseTemporaryRT(m_ScreenSpacePlanarReflection.id);
            cmd.ReleaseTemporaryRT(m_ScreenSpacePlanarReflectionBuffer.id);
        }

        void RenderReflection(CommandBuffer cmd, RenderTargetIdentifier target, Camera camera)
        {
            if (m_ReflectionShader == null)
            {
                m_ReflectionMaterial = new Material(m_ReflectionShader);
            }

            cmd.SetRenderTarget(m_ScreenSpacePlanarReflection.Identifier(), RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, bStencilValid ? m_CameraDepthTarget : RenderTargetHandle.CameraTarget.Identifier(), RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            cmd.SetGlobalVector(m_PropertySSPRBufferRange, new Vector4(m_Size.x, m_Size.y, 0, 0));
            cmd.SetGlobalTexture(m_ScreenSpacePlanarReflectionBuffer.id, m_ScreenSpacePlanarReflectionBuffer.Identifier());
            cmd.SetGlobalTexture(m_PropertyMainTex, m_CameraColorTarget);
            
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetViewport(camera.pixelRect);
            cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_ReflectionMaterial, 0, 0);
            cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);


            //cmd.Blit(m_CameraColorTarget, target, m_ReflectionMaterial, 0);
            //cmd.SetGlobalTexture(COLOR_ATTACHMENT, m_CameraColorTarget);
            //cmd.Blit(m_CameraColorTarget, target);//, m_ReflectionMaterial, 0);
        }

        void RenderSquishEdges(CommandBuffer cmd, RenderTargetIdentifier target, RenderTargetIdentifier source)
        {

        }

        void RenderBlur(CommandBuffer cmd, RenderTargetIdentifier target, RenderTargetIdentifier source)
        {

        }
    }


    class DrawReflectiveLayerRenderPass : ScriptableRenderPass
    {
        private const string m_ProfilerTag = "ReflectionsFeature_DrawPass";
        private ScreenSpacePlanarReflectionsSettings m_Settings;

        public DrawReflectiveLayerRenderPass(ScreenSpacePlanarReflectionsSettings settings)
        {
            m_Settings = settings;
        }

        // Here you can implement the rendering logic.
        // Use <c>ScriptableRenderContext</c> to issue drawing commands or execute command buffers
        // https://docs.unity3d.com/ScriptReference/Rendering.ScriptableRenderContext.html
        // You don't have to call ScriptableRenderContext.submit, the render pipeline will call it at specific points in the pipeline.
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
        }
    }

    public ScreenSpacePlanarReflectionsSettings settings = new ScreenSpacePlanarReflectionsSettings();
    StencilRenderPass m_StencilPass;
    ReflectionsRenderPass m_ReflectionsPass;
    DrawReflectiveLayerRenderPass m_DrawReflectivePass;

    public override void Create()
    {
#if UNITY_EDITOR
        ResourceReloader.TryReloadAllNullIn(settings, "Assets/");
#endif

        m_StencilPass = new StencilRenderPass(settings);
        m_ReflectionsPass = new ReflectionsRenderPass(settings);
        m_DrawReflectivePass = new DrawReflectiveLayerRenderPass(settings);

        // Configures where the render pass should be injected.
        m_StencilPass.renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
        m_ReflectionsPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
        m_DrawReflectivePass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    // Here you can inject one or multiple render passes in the renderer.
    // This method is called when setting up the renderer once per-camera.
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if(settings.needsStencilPass)
        {
            renderer.EnqueuePass(m_StencilPass);
        }

        m_ReflectionsPass.SetTargets(renderer);
        renderer.EnqueuePass(m_ReflectionsPass);

        if(settings.needsRenderReflective)
        {
            renderer.EnqueuePass(m_DrawReflectivePass);
        }
        
    }

}


