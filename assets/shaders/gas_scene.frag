#version 450
// A cloud at the middle or far level in the scene: it stops at what is solid in front of it. That is read from the
// scene's own depth, attached read-only meanwhile, so nothing is copied for it. The camera is outside such a cloud,
// so the pilot's own hull is never inside its gas and the pass needs no mask of it.
#include "frame.glsl"
#include "gas.glsl"
layout(set = 1, binding = 1) uniform sampler2D sceneDepth;
layout(push_constant) uniform GasPush { GasData gas; } pushGas;
layout(location = 0) in vec2 ndc;
layout(location = 0) out vec4 outColor;
void main() {
    float depth=gasViewDepth(texelFetch(sceneDepth,ivec2(gl_FragCoord.xy),0).r);
    vec4 volume=gasSegment(pushGas.gas,gasRay(ndc*0.5+0.5),0.0,depth,2.0*frame.lens.x/frame.lens.w);
    outColor=vec4(volume.rgb,1.0-volume.a);
}
