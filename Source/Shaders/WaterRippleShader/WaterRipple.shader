#include "./Flax/Common.hlsl"

META_CB_BEGIN(0, Data)
	float Damping;				
	float WaveSpeed;			
	float TouchStrength;		
	float DeltaTime;		
	float NormalStrength;			
	float2 TouchPosition;		

META_CB_END

RWTexture2D<float2> HeightField : register(u0);
RWTexture2D<float3> NormalField : register(u1);

float3 ComputeNormal(uint2 pos, float2 texSize)
{
	// 确保不采样边界（或使用边界处理）
	if (pos.x <= 0 || pos.x >= texSize.x - 1 || pos.y <= 0 || pos.y >= texSize.y - 1)
		return float3(0, 0, 1); // 返回默认法线

	// DX中通常Y轴向下（纹理坐标），所以上下采样要反转
	float h_left  = HeightField[pos - uint2(1, 0)].r;
	float h_right = HeightField[pos + uint2(1, 0)].r;
	float h_down  = HeightField[pos - uint2(0, 1)].r;  // 注意：在DX中，纹理坐标Y向下
	float h_up    = HeightField[pos + uint2(0, 1)].r;   // 所以"up"实际上是纹理坐标的+Y方向

	// 计算梯度（注意Y方向符号）
	float dhdx = (h_right - h_left) * 0.5f * NormalStrength;
	float dhdy = (h_down - h_up) * 0.5f * NormalStrength; // Y轴反转
	
	// 构造DX风格的法线（Z-up坐标系）
	// 注意：根据你的具体坐标系可能需要调整符号
	float3 normal = float3(-dhdx, -dhdy, 1.0f);
	
	// 归一化并确保指向正确的半球（通常是Z+）
	return normalize(normal);
}

META_CS(true, FEATURE_LEVEL_SM5)
[numthreads(8, 8, 1)]
void CS_UpdateRipples(
	uint3 groupID : SV_GroupID,
	uint3 groupThreadID : SV_GroupThreadID,
	uint3 dispatchThreadID : SV_DispatchThreadID)
{
	
	uint2 pos = dispatchThreadID.xy;

	uint2 texSize;
	HeightField.GetDimensions(texSize.x, texSize.y);

	if (pos.x >= texSize.x || pos.y >= texSize.y)
		return;

	float2 currentData = HeightField[pos];

	uint2 offset_x = uint2(1, 0);
	uint2 offset_y = uint2(0, 1);
	float h = currentData.r;      
	float h_prev = currentData.g; 
	float h_left  = HeightField[pos - offset_x].r;
	float h_right = HeightField[pos + offset_x].r;
	float h_up    = HeightField[pos + offset_y].r;
	float h_down  = HeightField[pos - offset_y].r;

	float laplacian = h_left + h_right + h_up + h_down - 4.0 * h;

	float velocity = h - h_prev;

	float acceleration = WaveSpeed * laplacian - Damping * velocity;

	float h_next = h + velocity + acceleration * DeltaTime * DeltaTime;

	uint2 touchPos = (uint2)TouchPosition;
	if (pos.x == touchPos.x && pos.y == touchPos.y)
	{
		h_next = TouchStrength;
	}

	HeightField[pos] = float2(h_next, h);
	
	if (pos.x > 0 && pos.x < texSize.x - 1 && pos.y > 0 && pos.y < texSize.y - 1)
	{
		float3 normal = ComputeNormal(pos, texSize);
		NormalField[pos] = normal;
	}
}