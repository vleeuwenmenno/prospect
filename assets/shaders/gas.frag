#version 450
#include "frame.glsl"
#include "gas.glsl"
layout(set = 1, binding = 0) uniform sampler2D opaqueColor;
layout(set = 1, binding = 1) uniform sampler2D opaqueDepth;
layout(push_constant) uniform GasPush { GasData gas; } pushGas;
layout(location = 0) in vec2 ndc;
layout(location = 0) out vec4 outColor;
void main() {
    GasData gas=pushGas.gas;
    float ownMask=texelFetch(opaqueColor,ivec2(gl_FragCoord.xy),0).a;
    if(int(frame.ftl.x+0.5)==1 && ownMask>=0.5) {
        gasThin=1.0-clamp(frame.ftlUp.w,0.0,1.0);
        gas.halfSize.w*=gasThin;
    }
    vec4 volume=gasSegment(gas,gasRay(ndc*0.5+0.5),0.0,gasViewDepth(texelFetch(opaqueDepth,ivec2(gl_FragCoord.xy),0).r),2.0*frame.lens.x/frame.lens.w);
    outColor=vec4(volume.rgb,1.0-volume.a);
}
