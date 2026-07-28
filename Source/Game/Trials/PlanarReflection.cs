using System;
using FlaxEngine;
using Quaternion = FlaxEngine.Quaternion;
using Vector3 = FlaxEngine.Vector3;
using Vector4 = FlaxEngine.Vector4;

namespace Game.Game.Trials;

public class PlanarReflection : Script
{
    public GPUTexture ReflectionTexture;
    public GPUTexture SkyTexture;
    public GPUTexture ScreenTexture;
    public GPUTexture ScreenDepth;
    public string ReflectionParamName = "ReflectionTexture";
    public string SkyParamName = "SkyTexture";
    public string ScreenParamName = "ScreenTexture";
    public string ScreenDepthParamName = "ScreenDepth";
    public MaterialInstance Material;
    public Camera MainCamera { get; set; }
    public LayersMask ReflectionLayers { get; set; }
    public LayersMask SkyLayer { get; set; }
    public LayersMask ScreenLayer { get; set; }

    [Limit(0f, 1f)]
    public float UpdateFrequency { get; set; } = 1f;

    [Limit(0f, 1f)]
    public float ResolutionScale
    {
        get => _resolutionScale;
        set
        {
            value = Mathf.Clamp(value, 0.01f, 1f);
            if (float.Abs(_resolutionScale - value) > 0.0001f)
            {
                _resolutionScale = value;
                if (_taskReflectionScene)
                    _taskReflectionScene.RenderScale = value;
                if (_taskSkyScene)
                    _taskSkyScene.RenderScale = value;
                if (_taskScreenScene)
                    _taskScreenScene.RenderScale = value;
                if (_taskScreenDepth)
                    _taskScreenDepth.RenderScale = value;
                UpdateOutput();
            }
        }
    }

    [ShowInEditor, ReadOnly]
    private Float2 _resolution = MainRenderTask.Instance
        ? MainRenderTask.Instance.Viewport.Size
        : Float2.One * 512;

    public float ClipPlaneOffset;
    public ViewFlags ReflectionViewFlags;
    private float _resolutionScale = 1f;
    private float _updateFrequencyCounter;
    private SceneRenderTask _taskReflectionScene;
    private SceneRenderTask _taskSkyScene;
    private SceneRenderTask _taskScreenScene;
    private SceneRenderTask _taskScreenDepth;
    private Vector4 _reflectionPlane;
    private Camera _reflectionCamera;
    private Camera _screenCamera;

    public override void OnEnable()
    {
        if (MainCamera == null)
        {
            MainCamera = Camera.MainCamera;
            if (MainCamera == null)
            {
                Debug.LogError("PlanarReflection: Main Camera is null");
                return;
            }
        }

        if (_reflectionCamera == null)
        {
            _reflectionCamera = new Camera
            {
                Name = "ReflectionCamera",
                FarPlane = MainCamera.FarPlane,
                NearPlane = MainCamera.NearPlane,
                FieldOfView = MainCamera.FieldOfView,
                CustomAspectRatio = MainCamera.CustomAspectRatio,
                RenderFlags = ReflectionViewFlags,
                RenderMode = MainCamera.RenderMode,
                RenderLayersMask = ReflectionLayers,
                HideFlags = HideFlags.DontSave,
                Parent = Scene,
            };
        }
        else
        {
            _reflectionCamera.FarPlane = MainCamera.FarPlane;
            _reflectionCamera.NearPlane = MainCamera.NearPlane;
            _reflectionCamera.FieldOfView = MainCamera.FieldOfView;
            _reflectionCamera.CustomAspectRatio = MainCamera.CustomAspectRatio;
            _reflectionCamera.RenderFlags = ReflectionViewFlags;
            _reflectionCamera.RenderMode = MainCamera.RenderMode;
            _reflectionCamera.HideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            _reflectionCamera.Parent = Scene;
        }

        if(_screenCamera == null)
        {
            _screenCamera = new Camera
            {
                Name = "ScreenCamera",
                FarPlane = MainCamera.FarPlane,
                NearPlane = MainCamera.NearPlane,
                FieldOfView = MainCamera.FieldOfView,
                CustomAspectRatio = MainCamera.CustomAspectRatio,
                RenderFlags = ViewFlags.DirectionalLights|ViewFlags.SkyLights,
                RenderMode = MainCamera.RenderMode,
                RenderLayersMask = ScreenLayer,
                HideFlags = HideFlags.DontSave,
                Parent = Scene,
            };
        }
        else
        {
            _screenCamera.FarPlane = MainCamera.FarPlane;
            _screenCamera.NearPlane = MainCamera.NearPlane;
            _screenCamera.FieldOfView = MainCamera.FieldOfView;
            _screenCamera.CustomAspectRatio = MainCamera.CustomAspectRatio;
            _screenCamera.RenderFlags = ViewFlags.DirectionalLights|ViewFlags.SkyLights;
            _screenCamera.RenderMode = MainCamera.RenderMode;
            _screenCamera.RenderLayersMask = ScreenLayer;
            _screenCamera.HideFlags = HideFlags.DontSave;
            _screenCamera.Parent = Scene;
        }

        Camera.OverrideMainCamera = MainCamera;

        // Create backbuffers
        if (ReflectionTexture == null)
            ReflectionTexture = new GPUTexture();
        if (SkyTexture == null)
            SkyTexture = new GPUTexture();
        if (ScreenTexture == null)
            ScreenTexture = new GPUTexture();
        if (ScreenDepth == null)
            ScreenDepth = new GPUTexture();
        UpdateOutput();

        // Create rendering tasks
        // 反射 Pass 必须剔除阴影/AO 等依赖深度重建的特性:
        // 斜裁剪投影会扭曲反射深度缓冲, 而阴影贴图是为主相机渲染的,
        // 在反射的扭曲深度空间中复用会导致阴影错误与闪烁
        ViewFlags reflectionFlags = ViewFlags.Sky;

        if (_taskReflectionScene == null)
        {
            _taskReflectionScene = new SceneRenderTask
            {
                Output = ReflectionTexture,
                Order = -100,
                Camera = _reflectionCamera,
                Enabled = false,
            };
        }
        else
        {
            _taskReflectionScene.ViewFlags = reflectionFlags;
        }

        if (_taskSkyScene == null)
        {
            _taskSkyScene = new SceneRenderTask
            {
                Output = SkyTexture,
                Order = -99,
                Camera = _reflectionCamera,
                ViewFlags = ReflectionViewFlags,
                ViewLayersMask = ReflectionLayers,
                Enabled = false,
            };
        }

        if (_taskScreenScene == null)
        {
            _taskScreenScene = new SceneRenderTask
            {
                Output = ScreenTexture,
                Order = -98,
                Camera = _screenCamera,
                ViewLayersMask = ScreenLayer,
                Enabled = false,
            };
        }

        if (_taskScreenDepth == null)
        {
            _taskScreenDepth = new SceneRenderTask
            {
                Output = ScreenDepth,
                Order = -97,
                Camera = _screenCamera,
                ViewLayersMask = ScreenLayer,
                ViewMode = ViewMode.Depth,
                Enabled = false,
            };
        }

        _taskReflectionScene.PreRender += OnReflectionPreRender;
        _taskReflectionScene.Enabled = true;

        _taskSkyScene.PreRender += OnSkyPreRender;
        _taskSkyScene.Enabled = true;

        _taskScreenScene.PreRender += OnScreenPreRender;
        _taskScreenScene.Enabled = true;

        _taskScreenDepth.PreRender += OnScreenDepthPreRender;
        _taskScreenDepth.Enabled = true;

        if (Material != null)
        {
            Material.SetParameterValue(ReflectionParamName, ReflectionTexture);
            Material.SetParameterValue(SkyParamName, SkyTexture);
            Material.SetParameterValue(ScreenParamName, ScreenTexture);
            Material.SetParameterValue(ScreenDepthParamName, ScreenDepth);
        }
    }


    public override void OnDisable()
    {
        if (_taskReflectionScene != null)
        {
            _taskReflectionScene.PreRender -= OnReflectionPreRender;
            Destroy(ref _taskReflectionScene);
        }
        if (_taskSkyScene != null)
        {
            _taskSkyScene.PreRender -= OnSkyPreRender;
            Destroy(ref _taskSkyScene);
        }
        if (_taskScreenScene != null)
        {
            _taskScreenScene.PreRender -= OnScreenPreRender;
            Destroy(ref _taskScreenScene);
        }
        if (_taskScreenDepth != null)
        {
            _taskScreenDepth.PreRender -= OnScreenDepthPreRender;
            Destroy(ref _taskScreenDepth);
        }

        Destroy(ref ReflectionTexture);
        Destroy(ref SkyTexture);
        Destroy(ref ScreenTexture);
        Destroy(ref ScreenDepth);

        // 释放反射相机
        if (_reflectionCamera != null)
        {
            _reflectionCamera.Parent = null;
            Destroy(ref _reflectionCamera);
        }
        if (_screenCamera != null)
        {
            _screenCamera.Parent = null;
            Destroy(ref _screenCamera);
        }
    }

    public override void OnUpdate()
    {
        _updateFrequencyCounter += UpdateFrequency;
        if (_updateFrequencyCounter >= 1f)
        {
            _updateFrequencyCounter = 0f;
            _taskReflectionScene?.Enabled = true;
            _taskSkyScene?.Enabled = true;
            _taskScreenScene?.Enabled = true;
            _taskScreenDepth?.Enabled = true;
        }
        else
        {
            _taskReflectionScene?.Enabled = false;
            _taskSkyScene?.Enabled = false;
            _taskScreenScene?.Enabled = false;
            _taskScreenDepth?.Enabled = false;
        }
    }

    private void UpdateReflectionCamera()
    {
        Transform transform = MainCamera.Transform;

        // 计算反射平面 (点法式)
        Vector3 position = Actor.Position;
        Vector3 normal = Actor.Transform.Up;
        float d = -Vector3.Dot(normal, position) - ClipPlaneOffset;
        _reflectionPlane = new Vector4(normal.X, normal.Y, normal.Z, d);

        // 计算反射相机位姿
        _reflectionCamera.Transform = ReflectionUtils.CalculateReflectionTransform(
            transform, position, normal);
        _screenCamera.Transform = transform;
    }

    private void OnReflectionPreRender(GPUContext context, ref RenderContext renderContext)
    {
        UpdateReflectionCamera();
        Matrix reflectionViewMatrix = renderContext.View.View;
        Matrix projectionMatrix = renderContext.View.Projection;
        Matrix inverseTransposeViewMatrix = Matrix.Transpose(Matrix.Invert(reflectionViewMatrix));
        Vector4 viewSpaceReflectionPlane = Vector4.Transform(
            _reflectionPlane,
            inverseTransposeViewMatrix
        );
        Matrix targetProjectionMatrix = ReflectionUtils.GetObliqueProjectionMatrix(
            projectionMatrix,
            viewSpaceReflectionPlane
        );
        renderContext.View.SetUp(ref reflectionViewMatrix, ref targetProjectionMatrix);

        _reflectionCamera.RenderLayersMask = SkyLayer;
        _reflectionCamera.RenderFlags = ViewFlags.DefaultGame;
    }

    private void OnSkyPreRender(GPUContext context, ref RenderContext renderContext)
    {
        Matrix refractionViewMatrix = renderContext.View.View;
        Matrix projectionMatrix = renderContext.View.Projection;
        renderContext.View.SetUp(ref refractionViewMatrix, ref projectionMatrix);

        _screenCamera.RenderLayersMask = ScreenLayer;
    }

    private void OnScreenPreRender(GPUContext context, ref RenderContext renderContext)
    {
        Matrix refractionViewMatrix = renderContext.View.View;
        Matrix projectionMatrix = renderContext.View.Projection;
        renderContext.View.SetUp(ref refractionViewMatrix, ref projectionMatrix);

        _screenCamera.RenderMode = ViewMode.Depth;
        _reflectionCamera.RenderLayersMask = ReflectionLayers;
        _reflectionCamera.RenderFlags = ReflectionViewFlags;
    }

    private void OnScreenDepthPreRender(GPUContext context, ref RenderContext renderContext)
    {
        Matrix refractionViewMatrix = renderContext.View.View;
        Matrix projectionMatrix = renderContext.View.Projection;
        renderContext.View.SetUp(ref refractionViewMatrix, ref projectionMatrix);
        
        _screenCamera.RenderMode = ViewMode.Default;
        _reflectionCamera.RenderLayersMask = ReflectionLayers;
    }

    private void UpdateOutput()
    {
        if (ReflectionTexture)
        {
            GPUTextureDescription desc0 = GPUTextureDescription.New2D(
                (int)_resolution.X,
                (int)_resolution.Y,
                PixelFormat.R8G8B8A8_UNorm
            );
            ReflectionTexture.Init(ref desc0);
        }
        if (SkyTexture)
        {
            GPUTextureDescription desc1 = GPUTextureDescription.New2D(
                (int)_resolution.X,
                (int)_resolution.Y,
                PixelFormat.R8G8B8A8_UNorm
            );
            SkyTexture.Init(ref desc1);
        }
        if (ScreenTexture)
        {
            GPUTextureDescription desc2 = GPUTextureDescription.New2D(
                (int)_resolution.X,
                (int)_resolution.Y,
                PixelFormat.R8G8B8A8_UNorm
            );
            ScreenTexture.Init(ref desc2);
        }
        if (ScreenDepth)
        {
            GPUTextureDescription desc3 = GPUTextureDescription.New2D(
                (int)_resolution.X,
                (int)_resolution.Y,
                PixelFormat.R16_Float
            );
            ScreenDepth.Init(ref desc3);
        }
    }
}
