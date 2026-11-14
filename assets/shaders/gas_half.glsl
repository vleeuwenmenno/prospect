// The primary cloud marched at half resolution (gas_half.frag) and read back at full resolution (gas.frag, tone
// mapping). Inside a cloud the march is most of the frame, and the gas changes slowly from pixel to pixel, so a quarter
// of the rays carry it. A half-resolution texel covers a block of two by two pixels and is marched along the ray through
// the block's centre to the farthest of their depths. A pixel takes the texels around it, each only where it was marched
// to this pixel's own depth, weighted as a bilinear lookup would weight them; where none was (an edge of something
// solid, a thin rock), the pixel marches itself. Needs frame.glsl and gas.glsl.
#ifndef GAS_HALF_GLSL
#define GAS_HALF_GLSL
// How far apart two depths may be, as a share of the pixel's, for a texel marched to one to stand for the other.
const float GasHalfTolerance=0.01;
const float GasHalfRadius=2.0;
// The view depth half-resolution texel `texel` is marched to: the farthest of the four full-resolution depths it covers.
// Reverse depth, so the farthest is the smallest value; sky anywhere in the block makes it the sky.
float gasHalfDepth(sampler2D depths, ivec2 texel) {
    vec4 four=textureGather(depths,(vec2(texel)*2.0+1.0)/vec2(textureSize(depths,0)),0);
    return gasViewDepth(min(min(four.x,four.y),min(four.z,four.w)));
}
// The ray through the centre of half-resolution texel `texel`.
vec3 gasHalfRay(sampler2D depths, ivec2 texel) {
    return gasRay((vec2(texel)*2.0+1.0)/vec2(textureSize(depths,0)));
}
// Whether a lightning cloud's bolt lines can reach this pixel or a half-resolution texel next to it: those pixels march
// themselves, as the half-resolution march leaves the lines out. The test is gasBolts' own, widened by two pixels.
bool gasNearBolt(GasData gas, vec3 ray, float pixelAngle) {
    if(gasKindOf(gas)!=2) return false;
    int slot=gasSlotOf(gas), bolts=int(frame.gasKindTime.w+0.5);
    for(int b=0;b<6;b++) {
        if(b>=bolts) break;
        vec4 bounds=frame.gasBoltHeads[3*b];
        if(int(frame.gasBoltHeads[3*b+1].w+0.5)!=slot) continue;
        float toward=max(dot(bounds.xyz,ray),0.0);
        if(length(bounds.xyz-ray*toward)<=bounds.w+8.0*frame.gasBoltShape.x+2.0*pixelAngle*toward) return true;
    }
    return false;
}
// The primary cloud from the camera to view depth `depth` at full-resolution pixel `pixel`, from the half-resolution
// march; false where no texel around the pixel was marched to its depth.
bool gasHalfRead(sampler2D marched, sampler2D depths, ivec2 pixel, float depth, out vec4 volume) {
    ivec2 size=textureSize(marched,0);
    // The pixel's centre in half-resolution texels: (pixel + 0.5) / 2 - 0.5.
    vec2 at=vec2(pixel)*0.5-0.25;
    ivec2 base=ivec2(floor(at));
    vec4 sum=vec4(0);
    float weight=0.0;
    for(int k=0;k<16;k++) {
        ivec2 corner=ivec2(k&3,k>>2)-1;
        ivec2 texel=clamp(base+corner,ivec2(0),size-1);
        vec2 off=abs(vec2(base+corner)-at);
        vec2 share=max(1.0-off/GasHalfRadius,0.0);
        float w=share.x*share.y;
        if(w<=0.0) continue;
        float marchedTo=gasHalfDepth(depths,texel);
        bool same=marchedTo>=Far ? depth>=Far : abs(marchedTo-depth)<=GasHalfTolerance*depth;
        if(!same) continue;
        sum+=w*texelFetch(marched,texel,0);
        weight+=w;
    }
    volume=sum/max(weight,1e-6);
    return weight>0.0;
}
#endif
