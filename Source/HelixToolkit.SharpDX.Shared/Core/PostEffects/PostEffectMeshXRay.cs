﻿/*
The MIT License (MIT)
Copyright (c) 2018 Helix Toolkit contributors
*/
using SharpDX;
using SharpDX.Direct3D11;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Format = global::SharpDX.DXGI.Format;
#if !NETFX_CORE
namespace HelixToolkit.Wpf.SharpDX.Core
#else
namespace HelixToolkit.UWP.Core
#endif
{
    using Render;
    using Shaders;   
    using Model;
    using Model.Scene;
    using Utilities;

    /// <summary>
    /// 
    /// </summary>
    public interface IPostEffectMeshXRay : IPostEffect
    {
        /// <summary>
        /// Gets or sets the color.
        /// </summary>
        /// <value>
        /// The color.
        /// </value>
        Color4 Color { set; get; }
        /// <summary>
        /// Gets or sets the outline fading factor.
        /// </summary>
        /// <value>
        /// The outline fading factor.
        /// </value>
        float OutlineFadingFactor { set; get; }
        /// <summary>
        /// Gets or sets a value indicating whether [double pass]. Double pass uses stencil buffer to reduce overlapping artifacts
        /// </summary>
        /// <value>
        ///   <c>true</c> if [double pass]; otherwise, <c>false</c>.
        /// </value>
        bool DoublePass { set; get; }
    }
    /// <summary>
    /// 
    /// </summary>
    public class PostEffectMeshXRayCore : RenderCoreBase<BorderEffectStruct>, IPostEffectMeshXRay
    {
        /// <summary>
        /// Gets or sets the name of the effect.
        /// </summary>
        /// <value>
        /// The name of the effect.
        /// </value>
        public string EffectName
        {
            set; get;
        } = DefaultRenderTechniqueNames.PostEffectMeshXRay;

        private Color4 color = global::SharpDX.Color.Red;
        /// <summary>
        /// Gets or sets the color of the border.
        /// </summary>
        /// <value>
        /// The color of the border.
        /// </value>
        public Color4 Color
        {
            set
            {
                SetAffectsRender(ref color, value);
            }
            get { return color; }
        }

        /// <summary>
        /// Outline fading
        /// </summary>
        public float OutlineFadingFactor
        {
            set
            {
                SetAffectsRender(ref modelStruct.Param.M11, value);
            }
            get { return modelStruct.Param.M11; }
        }

        private bool doublePass = false;
        /// <summary>
        /// Gets or sets a value indicating whether [double pass]. Double pass uses stencil buffer to reduce overlapping artifacts
        /// </summary>
        /// <value>
        ///   <c>true</c> if [double pass]; otherwise, <c>false</c>.
        /// </value>
        public bool DoublePass
        {
            set
            {
                SetAffectsRender(ref doublePass, value);
            }
            get
            {
                return doublePass;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PostEffectMeshXRayCore"/> class.
        /// </summary>
        public PostEffectMeshXRayCore() : base(RenderType.PostProc)
        {
            Color = global::SharpDX.Color.Blue;
        }

        private readonly List<KeyValuePair<SceneNode, IEffectAttributes>> currentCores = new List<KeyValuePair<SceneNode, IEffectAttributes>>();
        private DepthPrepassCore depthPrepassCore;
        /// <summary>
        /// Gets the model constant buffer description.
        /// </summary>
        /// <returns></returns>
        protected override ConstantBufferDescription GetModelConstantBufferDescription()
        {
            return new ConstantBufferDescription(DefaultBufferNames.BorderEffectCB, BorderEffectStruct.SizeInBytes);
        }
        protected override bool OnAttach(IRenderTechnique technique)
        {
            depthPrepassCore = Collect(new DepthPrepassCore());
            depthPrepassCore.Attach(technique);
            return base.OnAttach(technique);
        }

        protected override void OnDetach()
        {
            depthPrepassCore.Detach();
            base.OnDetach();
        }

        /// <summary>
        /// Called when [render].
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="deviceContext">The device context.</param>
        protected override void OnRender(RenderContext context, DeviceContextProxy deviceContext)
        {         
            var buffer = context.RenderHost.RenderBuffer;
            bool hasMSAA = buffer.ColorBufferSampleDesc.Count > 1;
            var dPass = DoublePass;
            var depthStencilBuffer = hasMSAA ? buffer.FullResDepthStencilPool.Get(Format.D32_Float_S8X24_UInt) : buffer.DepthStencilBuffer;

            BindTarget(depthStencilBuffer, buffer.FullResPPBuffer.CurrentRTV, deviceContext, buffer.TargetWidth, buffer.TargetHeight, false);
            if (hasMSAA)
            {
                //Needs to do a depth pass for existing meshes.Because the msaa depth buffer is not resolvable.
                deviceContext.DeviceContext.ClearDepthStencilView(depthStencilBuffer, DepthStencilClearFlags.Depth, 1, 0);
                depthPrepassCore.Render(context, deviceContext);
            }

            context.IsCustomPass = true;
            if (dPass)
            {                
                deviceContext.DeviceContext.ClearDepthStencilView(depthStencilBuffer, DepthStencilClearFlags.Stencil, 1, 0);
                currentCores.Clear();
                for (int i = 0; i < context.RenderHost.PerFrameNodesWithPostEffect.Count; ++i)
                {
                    IEffectAttributes effect;
                    var mesh = context.RenderHost.PerFrameNodesWithPostEffect[i];
                    if (mesh.TryGetPostEffect(EffectName, out effect))
                    {
                        currentCores.Add(new KeyValuePair<SceneNode, IEffectAttributes>(mesh, effect));
                        context.CustomPassName = DefaultPassNames.EffectMeshXRayP1;
                        var pass = mesh.EffectTechnique[DefaultPassNames.EffectMeshXRayP1];
                        if (pass.IsNULL) { continue; }
                        pass.BindShader(deviceContext);
                        pass.BindStates(deviceContext, StateType.BlendState);
                        deviceContext.SetDepthStencilState(pass.DepthStencilState, 0);//Increment the stencil value
                        mesh.Render(context, deviceContext);
                    }
                }

                for (int i = 0; i < currentCores.Count; ++i)
                {
                    var mesh = currentCores[i];
                    IEffectAttributes effect = mesh.Value;
                    object attribute;
                    var color = Color;
                    if (effect.TryGetAttribute(EffectAttributeNames.ColorAttributeName, out attribute) && attribute is string colorStr)
                    {
                        color = colorStr.ToColor4();
                    }
                    if (modelStruct.Color != color)
                    {
                        modelStruct.Color = color;
                        OnUploadPerModelConstantBuffers(deviceContext);
                    }

                    context.CustomPassName = DefaultPassNames.EffectMeshXRayP2;
                    var pass = mesh.Key.EffectTechnique[DefaultPassNames.EffectMeshXRayP2];
                    if (pass.IsNULL) { continue; }
                    pass.BindShader(deviceContext);
                    pass.BindStates(deviceContext, StateType.BlendState);
                    deviceContext.SetDepthStencilState(pass.DepthStencilState, 1);//Do stencil test only on value = 1.
                    mesh.Key.Render(context, deviceContext);
                }
                currentCores.Clear();                
            }
            else
            {
                for (int i =0; i < context.RenderHost.PerFrameNodesWithPostEffect.Count; ++i)
                {
                    IEffectAttributes effect;
                    var mesh = context.RenderHost.PerFrameNodesWithPostEffect[i];
                    if (mesh.TryGetPostEffect(EffectName, out effect))
                    {
                        object attribute;
                        var color = Color;
                        if (effect.TryGetAttribute(EffectAttributeNames.ColorAttributeName, out attribute) && attribute is string colorStr)
                        {
                            color = colorStr.ToColor4();
                        }
                        if (modelStruct.Color != color)
                        {
                            modelStruct.Color = color;
                            OnUploadPerModelConstantBuffers(deviceContext);
                        }
                        context.CustomPassName = DefaultPassNames.EffectMeshXRayP2;
                        var pass = mesh.EffectTechnique[DefaultPassNames.EffectMeshXRayP2];
                        if (pass.IsNULL) { continue; }
                        pass.BindShader(deviceContext);
                        pass.BindStates(deviceContext, StateType.BlendState);
                        deviceContext.SetDepthStencilState(pass.DepthStencilState, 0);
                        mesh.Render(context, deviceContext);
                    }
                }
            }
            if (hasMSAA)
            {
                deviceContext.DeviceContext.OutputMerger.ResetTargets();
                buffer.FullResDepthStencilPool.Put(Format.D32_Float_S8X24_UInt, depthStencilBuffer);
            }
            context.IsCustomPass = false;
        }

        protected override void OnUpdatePerModelStruct(ref BorderEffectStruct model, RenderContext context)
        {
            modelStruct.Color = color;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BindTarget(DepthStencilView dsv, RenderTargetView targetView, DeviceContext context, int width, int height, bool clear = true)
        {
            if (clear)
            {
                context.ClearRenderTargetView(targetView, global::SharpDX.Color.Transparent);
            }
            context.OutputMerger.SetRenderTargets(dsv, targetView == null ? null : new RenderTargetView[] { targetView });
            context.Rasterizer.SetViewport(0, 0, width, height);
            context.Rasterizer.SetScissorRectangle(0, 0, width, height);
        }
    }
}
