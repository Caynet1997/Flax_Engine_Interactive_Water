# Interactive Water in Flax Engine 1.10
<img width="1866" height="1171" alt="image" src="https://github.com/user-attachments/assets/aa850295-b6be-4c69-abf6-98cb02fd5218" />

A simple demo including the following content:
1. HLSL compute shader to create ripple texture and calculate normal
2. Planar reflection
3. A Noise Generator to procedure create normal texture
4. Simple water shader

## Knowing Issues:
  Planar reflection can't rendering shadow or sky correctly due to certain depth errors caused by the oblique projection matrix.
