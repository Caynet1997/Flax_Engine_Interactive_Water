using System;
using System.Runtime.InteropServices;
using FlaxEngine;

namespace Game.Game.Trials;

public class InteractiveWater : PostProcessEffect
{
	[StructLayout(LayoutKind.Sequential)]
	private struct Data
	{
		public float Damping;
		public float WaveSpeed;
		public float TouchStrength;
		public float DeltaTime;
		public float NormalStrength;
		public Float2 TouchPosition;
	}

	[Tooltip("Wave damping factor (0-1)"), Limit(0, 10.0f, 0.01f)]
	public float Damping = 0.98f;

	[Tooltip("Wave propagation speed"), Limit(0.1f, 10.0f, 0.01f)]
	public float WaveSpeed = 1.0f;

	[Tooltip("Strength of new ripples"), Limit(0, 1.0f, 0.01f)]
	public float TouchStrength = 0.5f;

	[Tooltip("ElapseTime of new ripples"), Limit(0.1f, 60.0f, 0.1f)]
	public float ElapseTime = 2f;

	[Tooltip("NormalStrength of new ripples"), Limit(0.1f, 10.0f, 0.1f)]
	public float NormalStrength = 1f;

	public float MeshSize = 500f;
	public int TextureSize = 512;

	private bool _isComputeSupported;
	private GPUTexture _heightField;
	private GPUTexture _normalField;

	public Shader RippleShader;
	public MaterialInstance WaterMaterial;
	public string RippleTextureParam = "Ripple Texture";
	public string NormalTextureParam = "Ripple Texture";

	// 输入处理
	public Float2 TouchPosition { get; set; }

	public override void OnEnable()
	{
		// 使用计算着色器初始化纹理
		_heightField = new GPUTexture();
		GPUTextureDescription desc = GPUTextureDescription.New2D(TextureSize, TextureSize, PixelFormat.R32G32_Float, GPUTextureFlags.UnorderedAccess | GPUTextureFlags.ShaderResource | GPUTextureFlags.RenderTarget);
		_heightField.Init(ref desc);

		_normalField = new GPUTexture();
		GPUTextureDescription desc1 = GPUTextureDescription.New2D(TextureSize, TextureSize, PixelFormat.R32G32_Float, GPUTextureFlags.UnorderedAccess | GPUTextureFlags.ShaderResource | GPUTextureFlags.RenderTarget);
		_normalField.Init(ref desc1);

		WaterMaterial.SetParameterValue(RippleTextureParam, _heightField);
		WaterMaterial.SetParameterValue(NormalTextureParam, _normalField);

		_isComputeSupported = GPUDevice.Instance.Limits.HasCompute;
		MainRenderTask.Instance.AddCustomPostFx(this);
	}

	public override void OnDisable()
	{
		WaterMaterial?.SetParameterValue(RippleTextureParam, null);
		MainRenderTask.Instance?.RemoveCustomPostFx(this);
		ReleaseBuffers();
	}

	public override void OnUpdate()
	{

	}

	private void ReleaseBuffers()
	{
		if (_heightField)
		{
			_heightField.ReleaseGPU();
			Destroy(ref _heightField);
		}
		if (_normalField)
		{
			_normalField.ReleaseGPU();
			Destroy(ref _normalField);
		}
	}
	public override bool CanRender()
	{
		bool canRender = base.CanRender() && _isComputeSupported && RippleShader && RippleShader.IsLoaded;
		return canRender;
	}

	public override unsafe void Render(GPUContext context, ref RenderContext renderContext, GPUTexture input, GPUTexture output)
	{
		// Update constant buffer
		var cb = RippleShader.GPU.GetCB(0);
		if (cb != IntPtr.Zero)
		{
			Data data= new()
			{
				Damping = Damping,
				WaveSpeed = WaveSpeed,
				TouchStrength = 0f,
				DeltaTime = Time.DeltaTime / ElapseTime,
				NormalStrength = NormalStrength,
			};

			if (Input.GetMouseButton(MouseButton.Left))
			{
				var ray = Camera.MainCamera.ConvertMouseToRay(Input.MousePosition);
				if (Physics.RayCast(ray.Position, ray.Direction, out var hitInfo))
				{
					var pos = new Float2(hitInfo.Point.X + MeshSize * 0.5f - Actor.Position.X, -hitInfo.Point.Z + MeshSize * 0.5f + Actor.Position.Z);
					TouchPosition = pos /MeshSize * TextureSize;
					data.TouchPosition = TouchPosition;
					data.TouchStrength = TouchStrength;
				}
			}
			context.UpdateCB(cb, new IntPtr(&data));
		}

		// Dispatch ripple update
		context.BindCB(0, cb);
		context.BindUA(0, _heightField.View());
		context.BindUA(1, _normalField.View());
		var csUpdate = RippleShader.GPU.GetCS("CS_UpdateRipples");
		// var groupCountX = input.Width / 8;
		// var groupCountY = input.Height / 8;
		var groupCountX = TextureSize / 8;
		var groupCountY = TextureSize / 8;
		context.Dispatch(csUpdate, (uint)groupCountX, (uint)groupCountY, 1);
		context.ResetUA();
		context.ResetCB();
	}
}