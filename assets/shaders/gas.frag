#version 450
#include "frame.glsl"
#include "gas.glsl"
#include "gas_half.glsl"
layout(set = 1, binding = 0) uniform sampler2D opaqueColor;
layout(set = 1, binding = 1) uniform sampler2D opaqueDepth;
// The primary cloud at half resolution, when budget.w is below zero (GasConstants.HalfMarched).
layout(set = 1, binding = 2) uniform sampler2D halfGas;
layout(push_constant) uniform GasPush { GasData gas; } pushGas;
layout(location = 0) in vec2 ndc;
layout(location = 0) out vec4 outColor;
void main() {
    GasData gas=pushGas.gas;
    ivec2 pixel=ivec2(gl_FragCoord.xy);
    float ownMask=texelFetch(opaqueColor,pixel,0).a;
    if(int(frame.ftl.x+0.5)==1 && ownMask>=0.5) {
        gasThin=1.0-clamp(frame.ftlUp.w,0.0,1.0);
        gas.halfSize.w*=gasThin;
    }
    float depth=gasViewDepth(texelFetch(opaqueDepth,pixel,0).r);
    vec3 ray=gasRay(ndc*0.5+0.5);
    float pixelAngle=2.0*frame.lens.x/frame.lens.w;
    vec4 volume;
    bool fromHalf=gas.budget.w<0.0 && gasThin>=1.0 && !gasNearBolt(gas,ray,pixelAngle);
    if(!fromHalf || !gasHalfRead(halfGas,opaqueDepth,pixel,depth,volume))
        volume=gasSegment(gas,ray,0.0,depth,pixelAngle);
    outColor=vec4(volume.rgb,1.0-volume.a);
}
