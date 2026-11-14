#version 450
// The primary cloud at half resolution (see gas_half.glsl): RGB the light it scatters toward the camera, alpha the
// transmission, from the camera to the farthest opaque depth of the texel's block. Overwrites the target.
#include "frame.glsl"
#include "gas.glsl"
#include "gas_half.glsl"
layout(set = 1, binding = 1) uniform sampler2D opaqueDepth;
layout(push_constant) uniform GasPush { GasData gas; } pushGas;
layout(location = 0) out vec4 outColor;
void main() {
    ivec2 texel=ivec2(gl_FragCoord.xy);
    gasBoltLines=false;
    // The four texels of each two-by-two block take one quarter of the step each, in an order and at a shift drawn
    // from the block: a pixel reads several neighbouring texels, so their offsets average out instead of leaving
    // blotches twice the size of a pixel's grain.
    uvec2 block=uvec2(texel)>>1;
    uint mixed=block.x*1664525u+block.y*1013904223u;
    mixed^=mixed>>16; mixed*=0x7feb352du; mixed^=mixed>>15; mixed*=0x846ca68bu; mixed^=mixed>>16;
    uint stratum=(uint(texel.x&1)+2u*uint(texel.y&1))^(mixed&3u);
    gasJitter=(float(stratum)+float(mixed>>8)*(1.0/16777216.0))*0.25;
    outColor=gasSegment(pushGas.gas,gasHalfRay(opaqueDepth,texel),0.0,gasHalfDepth(opaqueDepth,texel),2.0*frame.lens.x/frame.lens.w);
}
