// Shared by the ordinary gas pass, HDR portal composition and shield refraction.
#ifndef GAS_GLSL
#define GAS_GLSL
// Diagnostics only for explicitly muted fixtures. No normal-world counter writes.
int gasFixtureMarches = 0;
#include "ftl.glsl"
#include "ftl_own.glsl"
float gasHash(vec3 p) {
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}
// One immutable lattice is bound in the common frame set for every gas consumer.
layout(set = 0, binding = 1) uniform sampler3D gasNoiseLattice;
vec4 gasNoise(vec3 p) {
    vec3 cell=floor(p), f=fract(p);
    // Keep the original cubic interpolation; hardware filtering combines the eight cached corners.
    f=f*f*(3.0-2.0*f);
    return textureLod(gasNoiseLattice,(cell+f+0.5)/64.0,0.0);
}
// Per-cloud frame, derived once per segment call from the cloud seed. Nothing here is uploaded or cached.
struct GasShape {
    mat3 basis;  // Seeded rotation, so no noise lattice axis lines up with the box.
    vec3 iso;    // Box-normalized to isotropic units (one x half-extent), so features are round, not stretched.
    vec3 offset; // Seeded lattice phase.
    vec3 warp;   // Largest silhouette displacement per box axis; the same distance in metres on every axis.
    vec3 lean;   // Seeded offset of the envelope, so the densest part is not centred.
    float scale; // 1/fill: box units to envelope units.
    float skip;  // Squared envelope radius beyond which no fringe can reach, whatever the warp.
};
// A fixed irrational rotation between octaves hides the lattice grid of each finer field.
const mat3 GasOctave=mat3(0.00,0.80,0.60, -0.80,0.36,-0.48, -0.60,-0.48,0.64);
vec3 gasHash3(float s, float k) { return vec3(gasHash(vec3(s,k,1.3)),gasHash(vec3(k,s,7.1)),gasHash(vec3(s+k,3.7,k))); }
GasShape gasShape(GasData gas) {
    GasShape shape;
    float seed=gas.center.w;
    vec3 a=gasHash3(seed,91.0)*6.2831853;
    vec3 c=cos(a), s=sin(a);
    shape.basis=mat3(c.x,s.x,0,-s.x,c.x,0,0,0,1)*mat3(c.y,0,-s.y,0,1,0,s.y,0,c.y)*mat3(1,0,0,0,c.z,s.z,0,-s.z,c.z);
    shape.iso=gas.halfSize.xyz/gas.halfSize.x;
    shape.offset=vec3(seed,seed*0.61,seed*1.37);
    // Artist data from content (GasCloudModule.Shape), bounded there so a swollen surface stays inside the box.
    shape.warp=gas.shape.z/shape.iso;
    shape.lean=(gasHash3(seed,47.0)-0.5)*gas.shape.w;
    shape.scale=1.0/gas.shape.x;
    // The fringe ends where |e|^2 times the least swell (0.6, GasCloudModule.MinSwell) reaches 1.8; the warp moves
    // e by at most |warp| * scale.
    float reach=sqrt(1.8/0.6)+length(shape.warp)*shape.scale;
    shape.skip=reach*reach;
    return shape;
}
// The noiseless envelope's coarse density: what the farthest light probe uses, at no lookup cost.
float gasEnvelope(GasData gas, GasShape shape, vec3 p) {
    if(gas.budget.z>0.0) return 1.0;
    vec3 box=1.0-smoothstep(vec3(0.86),vec3(1.0),abs(p));
    vec3 e=(p-shape.lean)*shape.scale;
    float cover=clamp(0.4+0.6*(1.0-dot(e,e)),0.0,1.0);
    return box.x*box.y*box.z*cover*cover*0.75;
}
// Detail 0 is the coarse field used by far light probes (one lookup), 1 adds the medium octave, 2 is full detail.
// Detail 3 is the far level's view of the cloud: full detail with the finest octave at its mean, so its outline is
// the full outline smoothed rather than another shape, for two lookups and no wisps.
float gasDensity(GasData gas, GasShape shape, vec3 p, int detail) {
    // Homogeneous fixture input isolates composition from procedural appearance; only --gas-fixture sets this.
    if(gas.budget.z>0.0) return 1.0;
    // Compact support: zero before the box boundary on every axis, with rounded corners.
    vec3 box=1.0-smoothstep(vec3(0.86),vec3(1.0),abs(p));
    float fade=box.x*box.y*box.z;
    if(fade<=0.0) return 0.0;
    // An ellipsoid envelope at the content's fill share of the box, its squared radius scaled by the lumps below
    // between 0.6 and 1.4. Beyond the reach of the fringe the lookups can be skipped without changing anything.
    vec3 e=(p-shape.lean)*shape.scale;
    if(dot(e,e)>shape.skip) return 0.0;
    vec3 u=shape.basis*(p*shape.iso);
    // Large scale: RGB bends the silhouette; alpha swells or shrinks the envelope by the content's lumpiness,
    // a few lumps across the cloud, so it is lopsided rather than an egg.
    vec4 large=gasNoise(u*2.0+shape.offset);
    e=(p+(large.rgb-0.5)*2.0*shape.warp-shape.lean)*shape.scale;
    float body=1.0-dot(e,e)*clamp(1.0-(large.a-0.5)*gas.shape.y,0.6,1.4);
    if(body<-0.8) return 0.0;
    // The envelope does not fill the cloud; it sets how much of the multi-octave field survives. Deep inside nearly
    // all of it, near the surface only its peaks, so edges break into billows and islands instead of a smooth blob.
    float cover=clamp(0.4+0.6*body,0.0,1.0);
    if(detail==0) return fade*cover*cover*(0.2+1.1*large.a);
    // Medium: domain-warped octave with a slow drift; the silhouette stays still while the interior evolves.
    vec3 drift=vec3(gas.motion.x,gas.motion.x*0.37,-gas.motion.x*0.21);
    vec4 medium=gasNoise(GasOctave*(u*3.4)+(large.rgb-0.5)*0.9+shape.offset+drift);
    // Two octaves of amount, so dense knots and thin regions have no single straight-edged scale.
    float knots=0.55*large.a+0.45*medium.g;
    // Dense knots against thin veils: the knots are optically thick, so their sunward sides light and the rest shade.
    float amount=gas.look.w+gas.look.y*knots*knots*knots;
    float edge=gas.look.x;
    if(detail==1) return fade*smoothstep(1.0-edge-cover,1.0+edge-cover,0.35*large.a+0.65*medium.a+0.15)*amount;
    // Fine: a third octave warped by the medium field; its ridges give curved strands, not straight streaks.
    // Where the coverage step is already saturated whatever the fine octave says, and there is no fringe, the result
    // is exactly the same without it, so the lookup is skipped (most of a cloud's deep interior).
    // The edge follows the medium and fine octaves more than the broad one, so it breaks into billows, not arcs.
    float partial=0.3*large.a+0.4*medium.a+0.15;
    float low=1.0-edge-cover, high=1.0+edge-cover;
    if(detail==3) {
        // What the finest octave does on average: it widens the coverage step by its spread and fills the fringe
        // with a share of its strands. Without that the far level's outline would be the smaller one.
        float haze=smoothstep(-0.8,-0.35,body)*(1.0-smoothstep(-0.1,0.25,body));
        return fade*(smoothstep(low-0.1,high+0.1,partial+0.15)*amount+haze*0.25*(0.3+0.5*medium.a)*gas.look.z);
    }
    bool needsFine=body<0.25 || (partial<high && partial+0.3>low);
    if(!needsFine) return fade*(partial>=high ? amount : 0.0);
    vec4 fine=gasNoise(GasOctave*(GasOctave*(u*8.7))+(medium.rgb-0.5)*1.5+shape.offset+drift*1.7);
    float core=smoothstep(low,high,partial+0.3*fine.r)*amount;
    // Wisps and tendrils just outside the body, where nothing hides them.
    float strands=smoothstep(0.6,0.95,1.0-abs(2.0*fine.a-1.0));
    float fringe=smoothstep(-0.8,-0.35,body)*(1.0-smoothstep(-0.1,0.25,body));
    return fade*(core+fringe*strands*(0.3+0.5*medium.a)*gas.look.z);
}

// Dual-lobe Henyey-Greenstein: a forward lobe silvers the edges toward the star, a weak back lobe keeps the far
// side from going flat. Scaled so isotropic scattering is 0.65.
float gasPhase(float cosine) {
    float forwardLobe=0.8775/pow(1.1225-0.7*cosine,1.5), backLobe=0.9775/pow(1.0225+0.3*cosine,1.5);
    return mix(forwardLobe,backLobe,0.4)*0.65;
}
// Light one sample scatters toward the camera, given the optical depth its light probes found toward the star.
vec3 gasSampleLight(GasData gas, float phase, float depth) {
    float optical=depth*gas.halfSize.x*gas.halfSize.w;
    // Direct light plus one softened octave standing in for multiple scattering, so thick gas is not black.
    float direct=exp(-optical)*phase+0.2*exp(-optical*0.22);
    // Ambient close to the fill rocks get, so the unlit side of a cloud is dark like theirs; it reaches the
    // outer gas more easily than the core.
    float open=exp(-optical*0.5)*0.6+0.4;
    return gas.tint.rgb*(vec3(0.035,0.042,0.055)*open+frame.sunColor.rgb*direct);
}
vec3 gasRay(vec2 uv) {
    vec2 at=uv*2.0-1.0;
    return normalize(frame.cameraForward.xyz + frame.cameraRight.xyz*at.x*frame.lens.x*frame.lens.y - frame.cameraUp.xyz*at.y*frame.lens.x);
}
float gasViewDepth(float z) { return z>0.0 ? frame.viewProjection[3][2]/z : Far; }
// RGB is integrated scattering; alpha is remaining transmission. Bounds are view depths, not ray distances.
// The sample point of step `index`: jittered within the step, the last step cut at the interval's end.
vec3 gasStepPoint(vec3 origin, vec3 direction, float enter, float stepLength, float leave, float jitter, int index) {
    float from=enter+float(index)*stepLength;
    return origin+direction*(from+jitter*clamp(leave-from,0.0,stepLength));
}
vec4 gasSegment(GasData gas, vec3 ray, float fromDepth, float toDepth, float pixelAngle) {
    if(gas.budget.x<1.0 || toDepth<=fromDepth) return vec4(0,0,0,1);
    vec3 origin=-gas.center.xyz/gas.halfSize.xyz, direction=ray/gas.halfSize.xyz;
    float forward=dot(ray,frame.cameraForward.xyz);
    float enter=max(frame.viewProjection[3][2],fromDepth)/forward, leave=toDepth/forward;
    for(int axis=0;axis<3;axis++) {
        if(abs(direction[axis])<1e-12) { if(abs(origin[axis])>=1.0) return vec4(0,0,0,1); }
        else {
            float a=(-1.0-origin[axis])/direction[axis], b=(1.0-origin[axis])/direction[axis];
            enter=max(enter,min(a,b)); leave=min(leave,max(a,b));
        }
    }
    if(leave<=enter) return vec4(0,0,0,1);
    if(gas.budget.z>0.0) gasFixtureMarches++;
    // The content budget is the sample count for an interval as long as the cloud's smallest half-extent or longer.
    // A shorter interval (camera to a near rock, a shield to its hull) keeps that step length and takes fewer
    // samples instead of spending the whole budget on a few metres. Its last step is cut at the interval's end
    // rather than the count rounding the step: a rounded count re-placed every sample when the length moved by a
    // rounding error, so the ordinary pass and composition, which derive the same ray slightly differently, could
    // disagree by a whole sample at a pixel. Cut this way, the result follows the length continuously.
    // Level of detail (budget.y): 0 is the full march and the only one with a camera in or near the cloud. 1, the
    // middle level, keeps the full density but takes fewer steps, replaces the near light probe by the sample's own
    // density and takes the probes over the star's chord once per pair of steps. 2, the far level, is lit the same way
    // over a handful of steps and can leave out the finest octave (tint.w). A light worked out from the smooth
    // envelope alone was tried for it and shaded the cloud visibly more than the probes do, so the hand-over showed.
    // The renderer crossfades two levels of one cloud by their weights (motion.y).
    int level=int(gas.budget.y+0.5);
    int detail=level==2 && gas.tint.w>0.5 ? 3 : 2;
    int budget=clamp(int(gas.budget.x),level==0 ? 16 : 4,64);
    float smallest=min(gas.halfSize.x,min(gas.halfSize.y,gas.halfSize.z));
    float stepLength=max(leave-enter,smallest)/float(budget);
    int count=min(int(ceil((leave-enter)/stepLength)),budget);
    // An integer hash of the pixel: neighbours get unrelated offsets, so banding becomes fine grain, without the
    // radial streaks a direction hash gave where ray.xy barely changes or the hatching of a gradient pattern.
    uvec2 pixel=uvec2(gl_FragCoord.xy);
    uint mixed=pixel.x*1664525u+pixel.y*1013904223u;
    mixed^=mixed>>16; mixed*=0x7feb352du; mixed^=mixed>>15; mixed*=0x846ca68bu; mixed^=mixed>>16;
    float jitter=float(mixed>>8)*(1.0/16777216.0);
    int kind=int(frame.ftl.x+0.5);
    bool passage=frame.ftl.y>0.0 && (kind==2 || kind==3);
    FtlOwnRay ownRay=ftlOwnRay(ray,pixelAngle);
    GasShape shape=gasShape(gas);
    vec3 light=vec3(0); float transmission=1.0;
    // The weight thins the cloud to the view only. Its own light still crosses the whole cloud, so a level that is
    // fading in or out keeps the shading of the full cloud, and two levels with weights adding to one take the same
    // light away from what is behind them as either would alone.
    float extinction=gas.halfSize.w*gas.motion.y;
    float phase=gasPhase(dot(ray,frame.sunDirection.xyz));
    // Light probes march toward the star: a near one for local self-shadow, three over the chord for the bulk.
    vec3 sunStep=frame.sunDirection.xyz*gas.halfSize.x/gas.halfSize.xyz;
    float near=0.0, far=0.0;
    int nearAt=-1, farAt=-1;
    // The sun direction in envelope space, for the chord to where the fringe ends (|e|^2 = 3.6).
    vec3 sunEnvelope=sunStep*shape.scale;
    float sunReach=dot(sunEnvelope,sunEnvelope);
    for(int i=0;i<64;i++) {
        if(i>=count || transmission<0.01) break;
        float from=enter+float(i)*stepLength, width=clamp(leave-from,0.0,stepLength);
        float t=from+jitter*width;
        // Test the actual sample, including own-hull pixels that the surface compositor protects.
        float real=passage ? ftlOwnReal(ownRay,t*forward) : 1.0;
        if(real<=0.0) continue;
        vec3 p=origin+direction*t;
        float d=gasDensity(gas,shape,p,detail)*real;
        if(d<0.001) continue;
        if(level>0) {
            // Local self-shadow follows the sample's own density, which is what the near probe finds on average.
            near=d*0.07;
            vec3 e=(p-shape.lean)*shape.scale;
            float b=dot(e,sunEnvelope), c=dot(e,e)-3.6;
            float chord=gas.budget.z>0.0 ? 1.0 : clamp((-b+sqrt(max(b*b-sunReach*c,0.0)))/sunReach,0.1,2.0);
            int pair=i&~1;
            if(pair!=farAt) {
                farAt=pair;
                far=(gasDensity(gas,shape,p+sunStep*(chord*0.2),0)+gasDensity(gas,shape,p+sunStep*(chord*0.5),0)
                    +gasEnvelope(gas,shape,p+sunStep*(chord*0.8)))*(chord*0.33);
            }
            float seen=1.0-exp(-d*extinction*width);
            light+=transmission*seen*gasSampleLight(gas,phase,near+far);
            transmission*=1.0-seen;
            continue;
        }
        // One near probe resolves local self-shadow. The others are spread over the sun's chord through the outer
        // envelope, so a thick core really shades the far side wherever the sample is. Light changes slowly along
        // the view ray, so the near probe belongs to each pair of steps and the chord to each group of four, both
        // taken at the group's first step whether or not that step held gas. Taking them where the last lit step
        // happened to be instead made a sample's light jump when a neighbour crossed the density cut by a rounding
        // error, which is how the ordinary pass and composition disagreed at single pixels. The group index is the
        // same for every pixel in a wave, so this removes lookups for whole waves, not just for some of their pixels.
        int pair=i&~1, quad=i&~3;
        if(pair!=nearAt) {
            nearAt=pair;
            near=gasDensity(gas,shape,gasStepPoint(origin,direction,enter,stepLength,leave,jitter,pair)+sunStep*0.035,1)*0.07;
        }
        if(quad!=farAt) {
            farAt=quad;
            vec3 q=gasStepPoint(origin,direction,enter,stepLength,leave,jitter,quad);
            vec3 e=(q-shape.lean)*shape.scale;
            float b=dot(e,sunEnvelope), c=dot(e,e)-3.6;
            // Homogeneous fixture gas has no envelope; a unit chord keeps its light the same everywhere.
            float chord=gas.budget.z>0.0 ? 1.0 : clamp((-b+sqrt(max(b*b-sunReach*c,0.0)))/sunReach,0.1,2.0);
            far=(gasDensity(gas,shape,q+sunStep*(chord*0.2),0)
                +gasDensity(gas,shape,q+sunStep*(chord*0.5),0)
                +gasEnvelope(gas,shape,q+sunStep*(chord*0.8)))*(chord*0.33);
        }
        float depth=near+far;
        float absorbed=1.0-exp(-d*extinction*width);
        light+=transmission*absorbed*gasSampleLight(gas,phase,depth);
        transmission*=1.0-absorbed;
    }
    return vec4(light,transmission);
}
vec3 gasOver(vec4 volume, vec3 background) { return volume.rgb+volume.a*background; }
#endif
