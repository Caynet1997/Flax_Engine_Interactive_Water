using System;
using FlaxEngine;
using Quaternion = FlaxEngine.Quaternion;
using Vector3 = FlaxEngine.Vector3;
using Vector4 = FlaxEngine.Vector4;

namespace Game.Game.Trials;

public class PlanarReflection : Script
{
	public string ReflectionParamName = "MainTex";
	public string FlectionParamName = "FlectionTex";
	public MaterialInstance Material;
	public Camera MainCamera { get; set; }
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
				if (_taskFlectionScene) _taskFlectionScene.RenderingPercentage = value;
				UpdateOutput();
			}
		}
	}
	[ShowInEditor, ReadOnly]
	private Float2 _resolution =
		MainRenderTask.Instance ? MainRenderTask.Instance.Viewport.Size : Float2.One * 512;

	public float ClipPlaneOffset;
	public ViewFlags ReflectionViewFlags;
	public ViewFlags FlectionViewFlags;
	private float _resolutionScale = 1f;
	private float _updateFrequencyCounter;
	private GPUTexture _output0;
	private GPUTexture _output1;
	private SceneRenderTask _taskReflectionScene;
	private SceneRenderTask _taskFlectionScene;
	private Vector4 _reflectionPlane;
	private Vector4 _flectionPlane;
	private Camera ReflectionCamera;


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
				RenderFlags = ReflectionViewFlags,
				RenderMode = MainCamera.RenderMode,
				RenderLayersMask = ReflectionLayers,
				HideFlags = HideFlags.DontSave,
				Parent = Scene
			};
		}
		else
		{
			ReflectionCamera.FarPlane = MainCamera.FarPlane;
			ReflectionCamera.NearPlane = MainCamera.NearPlane;
			ReflectionCamera.FieldOfView = MainCamera.FieldOfView;
			ReflectionCamera.CustomAspectRatio = MainCamera.CustomAspectRatio;
			ReflectionCamera.RenderFlags = ReflectionViewFlags;
			ReflectionCamera.RenderMode = MainCamera.RenderMode;
			ReflectionCamera.HideFlags = HideFlags.HideInHierarchy|HideFlags.DontSave;
			ReflectionCamera.Parent = Scene;
		}
		Camera.OverrideMainCamera = MainCamera;

		// Create backbuffer
		if (_output0 == null) _output0 = new GPUTexture();
		if (_output1 == null) _output1 = new GPUTexture();
		UpdateOutput();
		

		// Create rendering task
		if (_taskReflectionScene == null)
		{
			_taskReflectionScene = new SceneRenderTask
			{
				Output = _output0,
				Order = -100,
				Camera = ReflectionCamera,
				ViewFlags = ReflectionViewFlags,
				Enabled = false
			};
		}

		if (_taskFlectionScene == null)
		{
			_taskFlectionScene = new SceneRenderTask
			{
				Output = _output1,
				Order = -99,
				Camera = MainCamera,
				ViewFlags = FlectionViewFlags,
				Enabled = false
			};
		}
		_taskReflectionScene.PreRender += OnRelectionPreRender;
		_taskReflectionScene.Enabled = true;

		_taskFlectionScene.PreRender += OnFlectionPreRender;
		_taskFlectionScene.Enabled = true;

		if (Material != null)
		{
			Material.SetParameterValue(ReflectionParamName, _output0);
			Material.SetParameterValue(FlectionParamName, _output1);
		}
	}


	public override void OnDisable()
	{
		_taskReflectionScene.PreRender -= OnRelectionPreRender;
		Destroy(ref _taskReflectionScene);
		Destroy(ref _output0);

		_taskFlectionScene.PreRender -= OnFlectionPreRender;
		Destroy(ref _taskFlectionScene);
		Destroy(ref _output1);
	}

	public override void OnUpdate()
	{
		_updateFrequencyCounter += UpdateFrequency;
		if (_updateFrequencyCounter >= 1f)
		{
			_updateFrequencyCounter = 0f;
			_taskReflectionScene.Enabled = true;
			_taskFlectionScene.Enabled = true;
		}
		else
		{
			_taskReflectionScene.Enabled = false;
			_taskFlectionScene.Enabled = false;
		}
	}

    public override void OnLateUpdate()
    {
    }

	private void UpdateReflectionCamera()
	{
		// Calculate Reflection Camera Position
		Transform transform = MainCamera.Transform;

		// Calculate dot-normal method plane.
		Vector3 position = Actor.Position;
		Vector3 normal = Actor.Transform.Up;
		float d = -Vector3.Dot(normal, position) - ClipPlaneOffset;
		_reflectionPlane = new Vector4(normal.X, normal.Y, normal.Z, d);
		_flectionPlane = new Vector4(normal.X, normal.Y, normal.Z, d + 1);

		// Calculate Reflection Camera Position and Rotation
		Vector3 camPos = transform.Translation;
		Vector3 reflectionCamPos = camPos - 2 * (Vector3.Dot(camPos, normal) + d) * normal;
		Vector3 originalForward = transform.Forward;
		Vector3 originalUp = transform.Up;
		Vector3 reflectedForward = originalForward - 2 * Vector3.Dot(originalForward, normal) * normal;
		Vector3 reflectedUp = originalUp - 2 * Vector3.Dot(originalUp, normal) * normal;
		Transform newTransform = new()
		{
			Orientation = Quaternion.LookRotation(reflectedForward, reflectedUp),
			Translation = reflectionCamPos,
		};

		ReflectionCamera.Transform = newTransform;
	}

	private void OnRelectionPreRender(GPUContext context, ref RenderContext renderContext)
	{
		Matrix reflectionViewMatrix = renderContext.View.View;
		Matrix projectionMatrix = renderContext.View.Projection;
		Matrix inverseTransposeViewMatrix = Matrix.Transpose(Matrix.Invert(reflectionViewMatrix));
		Vector4 viewSpaceReflectionPlane = Vector4.Transform(_reflectionPlane, inverseTransposeViewMatrix);
		Matrix targetProjectionMatrix = GetObliqueProjectionMatrixForDirectX(projectionMatrix, viewSpaceReflectionPlane);
		renderContext.View.SetUp(ref reflectionViewMatrix, ref targetProjectionMatrix);
		UpdateReflectionCamera();
	}

	private void OnFlectionPreRender(GPUContext context, ref RenderContext renderContext)
	{
		Matrix flectionViewMatrix = renderContext.View.View;
		Matrix projectionMatrix = renderContext.View.Projection;
		Matrix inverseTransposeViewMatrix = Matrix.Transpose(Matrix.Invert(flectionViewMatrix));
		Vector4 viewSpaceReflectionPlane = Vector4.Transform(_flectionPlane, inverseTransposeViewMatrix);
		Matrix targetProjectionMatrix = GetObliqueProjectionMatrixNew(projectionMatrix, viewSpaceReflectionPlane);

		renderContext.View.SetUp(ref flectionViewMatrix, ref targetProjectionMatrix);
	}

	private void UpdateOutput()
	{
		if (_output0)
		{
			GPUTextureDescription desc0 = GPUTextureDescription.New2D(
				(int)_resolution.X,
				(int)_resolution.Y,
				PixelFormat.R8G8B8A8_UNorm);
			_output0.Init(ref desc0);
		}
		if (_output1)
		{
			GPUTextureDescription desc1 = GPUTextureDescription.New2D(
				(int)_resolution.X,
				(int)_resolution.Y,
				PixelFormat.R8G8B8A8_UNorm);
			
			_output1.Init(ref desc1);
		}
	}
	private Matrix GetObliqueProjectionMatrixNew(Matrix projectionMatrix, Vector4 viewSpaceClipPlane)
	{
		// 计算裁剪空间中的点 q，对应 NDC 的角落 (-1,-1) 或 (1,1) 取决于符号
		Vector4 q = new(
			Math.Sign(viewSpaceClipPlane.X),
			Math.Sign(viewSpaceClipPlane.Y),
			-1.0f,  // NDC 的近裁面是 z=0，但这里用 -1 表示从后往前
			1.0f
		);
		// 构造修正向量 c
		Vector4 c = viewSpaceClipPlane * -2.0f * Vector4.Dot(projectionMatrix.Column4, q) / Vector4.Dot(viewSpaceClipPlane, q) + projectionMatrix.Column4;
		Matrix obliqueProj = projectionMatrix;
		obliqueProj.Column3 = c;

		return obliqueProj;
	}
	public float dd = 1;
	
	private Matrix GetObliqueProjectionMatrixNew1(Matrix projectionMatrix, Vector4 viewSpacePlane)
	{
		var M = projectionMatrix;
		var viewC = Vector4.Normalize(viewSpacePlane);
		var clipC = Vector4.Transform(viewC, Matrix.Transpose(Matrix.Invert(M)));
		var clipQ = new Vector4(Math.Sign(clipC.X), Math.Sign(clipC.Y), 1, 1);
		var viewQ = Vector4.Transform(clipQ, Matrix.Invert(M));
		var newM3 = dd / Vector4.Dot(viewC, viewQ) * viewC;
		M.Column3 = newM3;
		return M;
	}

	private Matrix GetObliqueProjectionMatrixForDirectX(Matrix projectionMatrix, Vector4 viewSpacePlane)
	{
		Matrix M = projectionMatrix;
		Vector4 viewC = Vector4.Normalize(viewSpacePlane); // 平面 (a,b,c,d) in view space
		//var clipC = Vector4.Transform(viewC, Matrix.Transpose(Matrix.Invert(M)));

		// Step 1: 构造 NDC 中的角落点 q
		// 在 DirectX 中，NDC 的 Z 范围是 [0, 1]，近裁面对应 z = 0
		Vector4 clipQ = new Vector4(
			Math.Sign(viewC.X),  // x: ±1
			Math.Sign(viewC.Y),  // y: ±1
			0.0f,                // ✅ z = 0 表示近裁面（DirectX）
			1.0f                 // w = 1
		);

		// Step 2: 将 NDC 的 q 点反变换到视图空间
		Matrix invM = Matrix.Invert(M);
		Vector4 viewQ = Vector4.Transform(clipQ, invM);

		// Step 3: 计算 dot(plane, viewQ)
		float dotProduct = Vector4.Dot(viewC, viewQ);

		// ⚠️ 安全检查：防止除以零或极小值（这是“压缩成缝”的根本原因）
		if (Math.Abs(dotProduct) < 1e-6f)
		{
			// 平面几乎与视线平行，避免数值爆炸
			if(Engine.FrameCount %120 ==0) Debug.Log(0);
			return M; // 返回原始投影矩阵
		}

		// Step 4: 计算修正向量 c = -2 * plane / dot(plane, viewQ)
		float scale = -2.0f / dotProduct;
		Vector4 c = scale * viewC;

		// Step 5: 修改投影矩阵的第3列（Column3）——这是 DirectX 的关键！
		M.Column3 = c;
		if(Engine.FrameCount %120 ==0) Debug.Log(1);
		return M;
	}
	private Matrix GetObliqueProjectionMatrix(Matrix projectionMatrix, Vector4 viewSpaceClipPlane)
	{
		Vector4 q = new(Math.Sign(viewSpaceClipPlane.X), Math.Sign(viewSpaceClipPlane.Y), -1f, 1f);
		Vector4 c = viewSpaceClipPlane * (-2.0f / Vector4.Dot(viewSpaceClipPlane, q));
		Matrix obliqueProj = projectionMatrix;
		obliqueProj.Column3 = c;
		return obliqueProj;
	}
	
}