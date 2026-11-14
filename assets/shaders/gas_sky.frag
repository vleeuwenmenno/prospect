#version 450
// A far cloud drawn into the sky, before anything solid: it has no depth to stop at, and whatever is drawn later
// covers it. The sky kept for the jump lenses and the scene kept behind the shields both hold it.
#include "frame.glsl"
#include "gas.glsl"
layout(push_constant) uniform GasPush { GasData gas; } pushGas;
layout(location = 0) in vec2 ndc;
layout(location = 0) out vec4 outColor;
void main() {
    vec4 volume=gasSegment(pushGas.gas,gasRay(ndc*0.5+0.5),0.0,Far,2.0*frame.lens.x/frame.lens.w);
    outColor=vec4(volume.rgb,1.0-volume.a);
}
