An Interior room shader for Unity.

1) Place camera looking through window, adjust clipping min and max to size of room
2) Open the capture window and drop your camera into the slot
3) apply the screen ratio by pressing match button
4) click capture
5) drag drop the created plane in slot
6) change lighting and build "other lighting"
7) hide the 3d leaving only the plane and look around

How it works:

Basically it creates a few images, normal room, room with out objects and depth.
We then basically ray march from new eye position on the depth map and find the Texel on the image, 
if it lands "behind" the foreground items, we pick texel from bg map.
For the lighting blending, we basically lerp between the two pictures. 

Note:

It currently suffers from some artifacts at glancing angles.
