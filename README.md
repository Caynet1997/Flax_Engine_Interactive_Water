# Interactive Water in Flax Engine 1.10
<img width="1865" height="1264" alt="屏幕截图 2025-08-20 130943" src="https://github.com/user-attachments/assets/b56fceee-492d-4a20-80cc-6746b22fa6b1" />

A simple demo including the following content:
1. HLSL compute shader to create ripple texture and calculate normal
2. Planar reflection
3. A Noise Generator to procedure create normal texture
4. A Water Material with advanced Screen space refraction

## Known Issues:
  Planar reflection can't rendering shadow or sky correctly due to certain depth errors caused by the oblique projection matrix.
