# Interactive Water in Flax Engine 1.12
<img width="1073" height="559" alt="image" src="https://github.com/user-attachments/assets/9298ca96-4533-4a15-8706-e188017509df" />

A simple demo including the following content:
1. HLSL compute shader to create ripple texture and calculate normal
2. Planar reflection
3. A Rain Generator
4. A water material with advanced screen space refraction

## Known Issues:
  Planar reflection can't rendering shadow or sky correctly due to certain depth errors caused by the oblique projection matrix.
