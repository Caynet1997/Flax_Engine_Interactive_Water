using System;
using FlaxEngine;
using Quaternion = FlaxEngine.Quaternion;
using Vector3 = FlaxEngine.Vector3;
using Vector4 = FlaxEngine.Vector4;

namespace Game.Game.Trials;

public class PlanarReflection : Script
{
    public string ReflectionParamName = "MainTex";
    public MaterialInstance Material;
    public Camera MainCamera { get; set; }

    public Camera ReflectionCamera { get; set; }

    public LayersMask ReflectionLayers { get; set; }

    public LayersMask SkyLayer { get; set; }

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
                if (_taskReflectionScene) _taskReflectionScene.RenderingPercentage = value;
                //if(_taskReflectionScene1) _taskReflectionScene1.RenderingPercentage = value;
                //UpdateOutput();
            }
        }
    }
    [ShowInEditor, ReadOnly]
    private Float2 _resolution =
        MainRenderTask.Instance ? MainRenderTask.Instance.Viewport.Size : Float2.One * 512;

    public float ClipPlaneOffset;
    public ViewFlags ViewFlags;
    public ViewMode ViewMode;

    private float _resolutionScale = 1f;
    private float _updateFrequencyCounter;
    private GPUTexture _outputScene;
    private SceneRenderTask _taskReflectionScene;
    private Vector4 _reflectionPlane;


    public override void OnEnable()
    {
        if (MainCamera == null)
        {
            MainCamera = Camera.MainCamera;
            if (MainCamera == null)
            {
                Debug.LogError("Main Camera is null");
                return;
            }
        }

        if (ReflectionCamera == null)
        {
            ReflectionCamera = new Camera
            {
                Name = "ReflectionCamera",
                FarPlane = MainCamera.FarPlane,
                NearPlane = MainCamera.NearPlane,
                FieldOfView = MainCamera.FieldOfView,
                CustomAspectRatio = MainCamera.CustomAspectRatio,
                RenderFlags = ViewFlags,
                RenderMode = MainCamera.RenderMode,
                RenderLayersMask = ReflectionLayers,
                HideFlags = HideFlags.HideInHierarchy,
                Parent = Scene
            };
        }
        else
        {
            ReflectionCamera.FarPlane = MainCamera.FarPlane;
            ReflectionCamera.NearPlane = MainCamera.NearPlane;
            ReflectionCamera.FieldOfView = MainCamera.FieldOfView;
            ReflectionCamera.CustomAspectRatio = MainCamera.CustomAspectRatio;
            ReflectionCamera.RenderFlags = ViewFlags;
            ReflectionCamera.RenderMode = MainCamera.RenderMode;
            ReflectionCamera.HideFlags = HideFlags.HideInHierarchy;
            ReflectionCamera.Parent = Scene;
        }
        Camera.OverrideMainCamera = MainCamera;

        // Create backbuffer
        if (_outputScene == null) _outputScene = new GPUTexture();
        UpdateOutput();

        // Create rendering task
        if (_taskReflectionScene == null)
        {
            _taskReflectionScene = new SceneRenderTask
            {
                Output = _outputScene,
                Order = -100,
                Camera = ReflectionCamera,
                ViewFlags = ViewFlags,
                Enabled = false

            };
        }
        _taskReflectionScene.PreRender += OnPreRender;
        _taskReflectionScene.Enabled = true;
    }


    public override void OnDisable()
    {
        _taskReflectionScene.PreRender -= OnPreRender;
        Destroy(ref _taskReflectionScene);
        Destroy(ref _outputScene);
    }

    public override void OnLateUpdate()
    {
        _updateFrequencyCounter += UpdateFrequency;
        if (_updateFrequencyCounter >= 1f)
        {
            _updateFrequencyCounter = 0f;
            _taskReflectionScene.Enabled = true;
        }
        else
        {
            _taskReflectionScene.Enabled = false;
        }

        if (MainCamera == null) return;

        // Calculate Reflection Camera Position
        Transform transform = MainCamera.Transform;

        // Calculate dot-normal method plane.
        Vector3 position = Actor.Position;
        Vector3 normal = Actor.Transform.Up;
        float d = -Vector3.Dot(normal, position) - ClipPlaneOffset;
        _reflectionPlane = new Vector4(normal.X, normal.Y, normal.Z, d);

        // Calculate Reflection Camera Position
        Vector3 camPos = transform.Translation;
        Vector3 reflectionCamPos = camPos - 2 * (Vector3.Dot(camPos, normal) + d) * normal;

        // Calculate Reflection Camera Rotation
        Vector3 originalForward = transform.Forward;
        Vector3 originalUp = transform.Up;
        Vector3 reflectedForward = originalForward - 2 * Vector3.Dot(originalForward, normal) * normal;
        Vector3 reflectedUp = originalUp - 2 * Vector3.Dot(originalUp, normal) * normal;

        // Apply Result
        Transform newTransform = new()
        {
            Orientation = Quaternion.LookRotation(reflectedForward, reflectedUp),
            Translation = reflectionCamPos,
        };


        ReflectionCamera.Transform = newTransform;
        if (Material != null)
        {
            Material.SetParameterValue(ReflectionParamName, _outputScene);
        }
    }

    private void OnPreRender(GPUContext context, ref RenderContext renderContext)
    {
        Matrix reflectionViewMatrix = renderContext.View.View;
        Matrix projectionMatrix = renderContext.View.Projection;
        Matrix inverseTransposeViewMatrix = Matrix.Transpose(Matrix.Invert(reflectionViewMatrix));
        Vector4 viewSpaceReflectionPlane = Vector4.Transform(_reflectionPlane, inverseTransposeViewMatrix);
        Matrix targetProjectionMatrix = GetObliqueProjectionMatrixGl(projectionMatrix, viewSpaceReflectionPlane);

        renderContext.View.SetUp(ref reflectionViewMatrix, ref targetProjectionMatrix);
    }

    private void UpdateOutput()
    {
        GPUTextureDescription desc = GPUTextureDescription.New2D(
            (int)_resolution.X,
            (int)_resolution.Y,
            PixelFormat.R8G8B8A8_UNorm);
        _outputScene.Init(ref desc);
    }

    private Matrix GetObliqueProjectionMatrixGl(Matrix projectionMatrix, Vector4 viewSpaceClipPlane)
    {
        Vector4 q = new(Math.Sign(viewSpaceClipPlane.X), Math.Sign(viewSpaceClipPlane.Y), -1f, 1f);
        Vector4 c = viewSpaceClipPlane * (-2.0f / Vector4.Dot(viewSpaceClipPlane, q));
        Matrix obliqueProj = projectionMatrix;
        obliqueProj.Column3 = c;
        return obliqueProj;
    }

    private Matrix apply_oblique_plane(Matrix projection,Vector4 oblique_plane)
    {
        Vector4 q;
        q.X = (Mathf.Sign(oblique_plane.X) + projection.Row3.X) / projection.Row1.X;
        q.Y = (Mathf.Sign(oblique_plane.Y) + projection.Row3.Y) / projection.Row2.Y;

        q.Z = -1f;
        q.W = (1f + projection.Row3.Z) / projection.Row4.Z;

        var c = oblique_plane * (-2f/ Vector4.Dot(oblique_plane,q));
        projection.Column3 = c; // projection.Column4;

        return projection;
    }
}