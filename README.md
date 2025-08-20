# Interactive Water in Flax Engine 1.10
<img width="1873" height="1260" alt="image" src="https://github.com/user-attachments/assets/c4490e54-79b2-4ca6-9c78-d8a41311cf74" />

A simple demo including the following content:
1. HLSL compute shader to create ripple texture and calculate normal
2. Planar reflection
3. A Noise Generator to procedure create normal texture
4. A Water Material with advanced Screen space flection

## Knowing Issues:
  Planar reflection can't rendering shadow or sky correctly due to certain depth errors caused by the oblique projection matrix.
