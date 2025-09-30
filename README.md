# Interactive Water in Flax Engine 1.10
<img width="1809" height="1124" alt="image" src="https://github.com/user-attachments/assets/da6c2c58-7be2-42fe-badf-cd8287da68c1" />

A simple demo including the following content:
1. HLSL compute shader to create ripple texture and calculate normal
2. Planar reflection
3. A Noise Texture Generator to procedurally create normal texture
4. A Water Material with advanced Screen Space Refraction

## Known Issues:
  Planar reflection can't rendering shadow or sky correctly due to certain depth errors caused by the oblique projection matrix.
