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
// motion.w: the kind (0 normal, 1 cold, 2 lightning) plus four times one more than a slot (GasConstants.KindCode).
int gasKindOf(GasData gas) { return int(gas.motion.w+0.5)&3; }
int gasSlotOf(GasData gas) { return (int(gas.motion.w+0.5)>>2)-1; }
// What the pilot's own hyperspace tunnel leaves of the extinction of a pair's second cloud, as the passes that thin the
// pushed cloud for the pilot's own hull set it; 1 everywhere else.
float gasThin=1.0;
// False only in the half-resolution march (gas_half.frag): bolt lines are a pixel or two wide, so pixels near a bolt
// march at full resolution instead and every other pixel takes the half-resolution result without them. Their light on
// the gas around them is volume light and stays in.
bool gasBoltLines=true;
// Where in its step each sample is taken, 0 to 1, when the pass chooses it (the half-resolution march); below zero, an
// integer hash of the pixel.
float gasJitter=-1.0;
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
// Wispiness (motion.z): the world setting gasWispiness, 0 for solid clouds with a hard outline, 1 as shipped, 2 the
// most. At 0 every expression below is exactly 1 or 0 and the cloud is what it was before the setting existed.
// The shell: the outer part of the body is thin gas that grades into the dense core, so the outline is where the
// gas becomes too thin to see rather than where a wall starts. Without it even the outermost gas was optically thick
// over a few hundred metres, which made a cloud a cotton ball. `n` is noise (0 to 1, about 0.5 on average) that makes
// the shell deeper in some places than others, so the thinning is lumpy, not a smooth halo.
float gasShell(GasData gas, float body, float n) {
    float wisp=gas.motion.z;
    if(wisp<=0.0) return 1.0;
    // The gas is optically thick: a shell a tenth as dense is still opaque edge on. So the thinning is steep, a power
    // of the ramp, and reaches far into the body.
    float r=smoothstep(-0.45,0.3+0.35*wisp,body+1.6*(n-0.5)*min(wisp,1.0));
    return mix(1.0,pow(r,1.0+0.75*wisp),min(wisp,1.0));
}
// Gaps: near the surface the gas survives only where a mixed medium and fine octave is above a threshold that rises
// toward the outside, so the shell breaks into strands with clear sky between them. Zero deep inside. It depends on
// the body alone, so the levels and probes without the fine octave can take the same gaps by their average.
float gasGapThreshold(GasData gas, float body) {
    // Capped at 1: a higher threshold empties a whole band, and the core inside it shows a cut edge.
    return min(gas.motion.z,1.0)*0.5*(1.0-smoothstep(0.0,0.75+0.15*gas.motion.z,body));
}
// The noiseless envelope's coarse density: what the farthest light probe uses, at no lookup cost.
float gasEnvelope(GasData gas, GasShape shape, vec3 p) {
    if(gas.budget.z>0.0) return 1.0;
    vec3 box=1.0-smoothstep(vec3(0.86),vec3(1.0),abs(p));
    vec3 e=(p-shape.lean)*shape.scale;
    float cover=clamp(0.4+0.6*(1.0-dot(e,e)),0.0,1.0);
    return box.x*box.y*box.z*cover*cover*0.75*gasShell(gas,1.0-dot(e,e),0.5);
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
    // The far light probe has the broad octave only: it takes the shell thinned by that, and the gaps by the share of
    // the gas they leave on average.
    float shell=gasShell(gas,body,large.a);
    float open=1.0-gasGapThreshold(gas,body);
    if(detail==0) return fade*cover*cover*(0.2+1.1*large.a)*shell*open;
    // Medium: domain-warped octave with a slow drift; the silhouette stays still while the interior evolves.
    vec3 drift=vec3(gas.motion.x,gas.motion.x*0.37,-gas.motion.x*0.21);
    vec4 medium=gasNoise(GasOctave*(u*3.4)+(large.rgb-0.5)*0.9+shape.offset+drift);
    // With the medium octave the shell's depth varies at a finer scale, so the dense part's own outline is lumpy.
    shell=gasShell(gas,body,0.4*large.a+0.6*medium.a);
    // Two octaves of amount, so dense knots and thin regions have no single straight-edged scale.
    float knots=0.55*large.a+0.45*medium.g;
    // Dense knots against thin veils: the knots are optically thick, so their sunward sides light and the rest shade.
    float amount=gas.look.w+gas.look.y*knots*knots*knots;
    float edge=gas.look.x;
    // The gaps as the fine octave averages them, from the medium one alone. The near light probe takes them, so
    // the gas around a gap is lit on one side and shaded on the other, which is what shows it inside the body.
    float gap=gasGapThreshold(gas,body);
    float gaps=gap>0.0 ? smoothstep(gap-0.18,gap+0.18,0.55*medium.a+0.225) : 1.0;
    if(detail==1) return fade*smoothstep(1.0-edge-cover,1.0+edge-cover,0.35*large.a+0.65*medium.a+0.15)*amount*shell*gaps;
    // Fine: a third octave warped by the medium field; its ridges give curved strands, not straight streaks.
    // Where the coverage step is already saturated whatever the fine octave says, and there is no fringe, the result
    // is exactly the same without it, so the lookup is skipped (most of a cloud's deep interior).
    // The edge follows the medium and fine octaves more than the broad one, so it breaks into billows, not arcs.
    float partial=0.3*large.a+0.4*medium.a+0.15;
    float low=1.0-edge-cover, high=1.0+edge-cover;
    // The fringe strands are as dense as the core, so on their own they draw the whole outline as a solid rim.
    // Wispy, they thin out toward the fringe's outer end, so the outline fades rather than stops, and they are
    // sparser, so what lies behind shows between them.
    float wisp=min(gas.motion.z,1.0), fall=smoothstep(-0.8,0.25,body);
    float thread=mix(1.0,0.35*fall*fall,wisp)/(1.0+2.0*max(gas.motion.z-1.0,0.0));
    float sparse=0.2*wisp;
    if(detail==3) {
        // What the finest octave does on average: it widens the coverage step by its spread and fills the fringe
        // with a share of its strands. Without that the far level's outline would be the smaller one. The gaps
        // follow the medium octave alone over a wider step, which is what the fine one averages to.
        float haze=smoothstep(-0.8,-0.35,body)*(1.0-smoothstep(-0.1,0.25,body));
        return fade*(smoothstep(low-0.1,high+0.1,partial+0.15)*amount*shell*gaps+haze*(0.25-0.3*sparse)*(0.3+0.5*medium.a)*gas.look.z*thread);
    }
    // Gaps need the fine octave wherever their threshold is above zero.
    bool needsFine=body<0.25 || gap>0.0 || (partial<high && partial+0.3>low);
    if(!needsFine) return fade*(partial>=high ? amount*shell : 0.0);
    vec4 fine=gasNoise(GasOctave*(GasOctave*(u*8.7))+(medium.rgb-0.5)*1.5+shape.offset+drift*1.7);
    float core=smoothstep(low,high,partial+0.3*fine.r)*amount*shell;
    if(gap>0.0) core*=smoothstep(gap-0.06,gap+0.06,0.55*medium.a+0.45*fine.g);
    // Wisps and tendrils just outside the body, where nothing hides them.
    float strands=smoothstep(0.6+sparse,0.95,1.0-abs(2.0*fine.a-1.0));
    float fringe=smoothstep(-0.8,-0.35,body)*(1.0-smoothstep(-0.1,0.25,body));
    return fade*(core+fringe*strands*(0.3+0.5*medium.a)*gas.look.z*thread);
}

// A large cloud seen from inside or close by: its octaves are as many times larger as it is, so what would be
// billows and wisps on an ordinary cloud are walls kilometres across. Near the camera one further octave, about half
// the body's finest, breaks the gas into strands with clear gaps between them: there is structure at the scale the
// camera is at, and the eye passes through the gaps, so the cloud is deep rather than a surface. The strands average
// out to one, so the amount of gas, the far view and the light probes stay as they are. The octave counts only where
// a step is short enough to follow it (see the bent steps in gasSegment) and fades with distance from the camera.
// A second, finer octave was tried as well: it cost a lookup a sample and could hardly be told apart.
// x is what the gas at p is multiplied by; y, times the gas there, is how much more (or, toward a gap, less) of it
// lies between p and the star within a strand's width, so each strand has a lit and a shaded side.
vec2 gasStrands(GasData gas, GasShape shape, vec3 p, float width, float along) {
    float close=1.0-smoothstep(0.5,1.0,along/(gas.fine.z*gas.halfSize.x));
    // The gas nearest the camera is thinned as well (fine.w), so the eye reaches a few strands deep instead of
    // stopping at the first: without it the inside of a dense part is one flat colour however it is broken up.
    float clear=1.0-gas.fine.w*close;
    // The finest octave of the body has 8.7 waves to the x half-extent.
    float wave=gas.halfSize.x/18.3;
    float first=gas.fine.x*close*clamp(1.5*wave/width-1.0,0.0,1.0);
    if(first<=0.0) return vec2(clear,0.0);
    vec3 u=shape.basis*(p*shape.iso);
    vec3 drift=vec3(gas.motion.x,gas.motion.x*0.37,-gas.motion.x*0.21)*2.4;
    vec3 at=GasOctave*(u*18.3)+shape.offset*1.9+drift;
    // Six tenths of the volume is gaps and the rest holds the gas.
    float strands=2.5*smoothstep(0.44,0.64,gasNoise(at).a);
    // A third of a wave toward the star, in the octave's own coordinates; not worth a lookup where strands are faint.
    float ahead=1.0;
    if(first>0.3) ahead=2.5*smoothstep(0.44,0.64,gasNoise(at+GasOctave*(shape.basis*frame.sunDirection.xyz)*0.35).a);
    return vec2(clear*mix(1.0,strands,first),(ahead-1.0)*first*(1.0/18.3));
}

// Dual-lobe Henyey-Greenstein: a forward lobe silvers the edges toward the star, a weak back lobe keeps the far
// side from going flat. Scaled so isotropic scattering is 0.65.
float gasPhase(float cosine) {
    float forwardLobe=0.8775/pow(1.1225-0.7*cosine,1.5), backLobe=0.9775/pow(1.0225+0.3*cosine,1.5);
    return mix(forwardLobe,backLobe,0.4)*0.65;
}
// Ice crystals in a cold cloud scatter a faint ring 22 degrees around the star, reddish inside, bluish outside: the
// halo the eye knows from cirrus. It is in the phase, so it costs nothing per sample.
float gasKindPhase(GasData gas, float cosine) {
    float phase=gasPhase(cosine);
    if(gasKindOf(gas)!=1 || frame.gasKindTime.z<=0.0) return phase;
    float angle=acos(clamp(cosine,-1.0,1.0));
    float ring=exp(-pow((angle-0.3840)/0.022,2.0));
    return phase+frame.gasKindTime.z*ring;
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
// Where step `index` starts. Unbent, the steps are equal. Bent (a large cloud near the camera), step `index` is
// drawn toward the start of the interval by a share that falls from `bend` at the first step to none at the last
// but one, so the first steps are a fraction of the even length and the last nearly twice it.
float gasStepStart(float enter, float stepLength, float bend, int budget, int index) {
    float even=float(index)*stepLength;
    if(bend<=0.0) return enter+even;
    return enter+even*(1.0-bend+bend*float(index)/float(budget));
}
// How long step `index` is before it is cut at the interval's end: exactly the even length when unbent.
float gasStepLength(float stepLength, float bend, int budget, int index) {
    if(bend<=0.0) return stepLength;
    return gasStepStart(0.0,stepLength,bend,budget,index+1)-gasStepStart(0.0,stepLength,bend,budget,index);
}
vec3 gasStepPoint(vec3 origin, vec3 direction, float enter, float stepLength, float bend, int budget, float leave, float jitter, int index) {
    float from=gasStepStart(enter,stepLength,bend,budget,index);
    return origin+direction*(from+jitter*clamp(leave-from,0.0,gasStepLength(stepLength,bend,budget,index)));
}
// The light a lightning stroke scatters toward the camera from a sample, per unit of light the sample takes away from
// what is behind it: the flash is deep in the cloud and lights the gas around it, falling off with distance in x
// half-extents, so dense knots near it glow and gas between it and the camera hides it.
vec3 gasFlashAt(GasData gas, GasShape shape, vec3 p, vec4 stroke) {
    // The reach is a share of the cloud, but never more than gasFlashReach.x metres: in a huge cloud a stroke lights its
    // own part of it, not all of it.
    float x=length((p-stroke.xyz)*gas.halfSize.xyz)/min(frame.gasFlashLight.w*gas.halfSize.x,frame.gasFlashReach.x);
    return gas.tint.rgb*frame.gasFlashLight.rgb*stroke.w*exp(-1.6*x)*(1.0+0.6*exp(-6.0*x*x));
}
// The closest approach of a ray from the camera (unit `ray`) to a segment a..b, both camera-relative in metres:
// x the distance along the ray, y how far from it the segment passes.
vec2 gasRaySegment(vec3 ray, vec3 a, vec3 b) {
    vec3 ab=b-a;
    float abab=dot(ab,ab), rab=dot(ray,ab);
    float denom=abab-rab*rab;
    float s=denom>1e-6*abab ? clamp((dot(ray,a)*rab-dot(a,ab))/denom,0.0,1.0) : 0.0;
    vec3 q=a+ab*s;
    float t=max(dot(q,ray),0.0);
    return vec2(t,length(q-ray*t));
}
// One segment of a bolt: its core's light toward this pixel, never wider than the bolt but fainter once the bolt is
// narrower than a pixel, and the light-weighted distance along the ray. `halo.x` sums the squares of the segments'
// glows, which gasBolts takes the root of: a bolt glows around its nearest parts.
void gasBoltSegment(vec3 ray, vec3 a, vec3 b, float scale, float width, float pixelAngle, inout float sum, inout float at, inout vec2 halo) {
    if(scale<=0.0) return;
    vec2 r=gasRaySegment(ray,a,b);
    // A channel is a fine line: never under about a pixel (fainter instead), never over about two however near.
    float pixel=pixelAngle*r.x;
    float w=clamp(width,pixel*0.7,pixel*2.0);
    float e=min(width/w,1.0)*exp(-(r.y*r.y)/(w*w))*scale;
    sum+=e; at+=r.x*e;
    // The glow fades to nothing well inside the bounds gasBolts tests, so no edge shows where it stops looking.
    // Distance alone, not each segment's own light, and summed in squares: a smooth union of the segments' glows,
    // with no crease where the nearest segment changes, and close to the nearest one's glow alone.
    float glow=exp(-r.y/frame.gasBoltShape.x)*(1.0-smoothstep(4.0,7.0,r.y/frame.gasBoltShape.x));
    halo.x+=glow*glow;
}
// How much of a bolt shows at camera-relative point q: all of it inside the cloud's body, none past its outer gas, so a
// bolt never hangs in clear space beside the cloud.
float gasBoltInside(GasData gas, GasShape shape, vec3 q) {
    vec3 e=((q-gas.center.xyz)/gas.halfSize.xyz-shape.lean)*shape.scale;
    return smoothstep(-0.35,0.25,1.0-dot(e,e));
}
// This cloud's bolts (LightningBolts), as segments worked out on the CPU: each grown as far along its path as its
// front, its growing tip brighter, its main channel brighter and wider than its forks and theirs than their forks.
// x is where along the ray their light is added, yzw that light. The two brightest bolts also light the gas around
// them: `lampA` and `lampB` are their centres in the cloud's metres and how much.
vec4 gasBolts(GasData gas, GasShape shape, int slot, bool lines, vec3 ray, float pixelAngle, out vec4 lampA, out vec4 lampB) {
    lampA=vec4(0); lampB=vec4(0);
    float sum=0.0, at=0.0;
    int bolts=int(frame.gasKindTime.w+0.5);
    for(int b=0;b<6;b++) {
        if(b>=bolts) break;
        vec4 bounds=frame.gasBoltHeads[3*b], path=frame.gasBoltHeads[3*b+1], strength=frame.gasBoltHeads[3*b+2];
        if(int(path.w+0.5)!=slot) continue;
        vec4 lamp=vec4(bounds.xyz-gas.center.xyz,strength.z);
        if(lamp.w>lampA.w) { lampB=lampA; lampA=lamp; }
        else if(lamp.w>lampB.w) lampB=lamp;
        float toward=max(dot(bounds.xyz,ray),0.0);
        if(!lines || length(bounds.xyz-ray*toward)>bounds.w+8.0*frame.gasBoltShape.x) continue;
        int first=int(path.x+0.5), count=int(path.y+0.5);
        float front=path.z;
        vec2 halo=vec2(0);
        for(int k=0;k<128;k++) {
            if(k>=count) break;
            vec4 from=frame.gasBoltSegments[2*(first+k)], to=frame.gasBoltSegments[2*(first+k)+1];
            if(from.w>=front) continue;
            float span=max(length(to.xyz-from.xyz),1e-3);
            // Most segments are far from this pixel's ray: skip them before the exact test and the kinks.
            vec3 middle0=0.5*(from.xyz+to.xyz);
            if(length(middle0-ray*max(dot(middle0,ray),0.0))>0.75*span+7.0*frame.gasBoltShape.x) continue;
            float shown=min((front-from.w)/span,1.0);
            float fork=to.w;
            float light=(fork<0.5 ? strength.x : strength.y*pow(0.75,fork-1.0))*(shown<1.0 ? 1.0+frame.gasBoltShape.w : 1.0)
                *gasBoltInside(gas,shape,0.5*(from.xyz+to.xyz));
            float width=frame.gasBoltLight.w*pow(0.7,fork);
            // A segment that covers many pixels is kinked further, in four pieces bent across the view, so a bolt
            // close by is as finely jagged as one far off. The kinks are seeded by the segment, so they hold still.
            // Judged by the segment's view depth, the same for every pixel, so all pixels see one shape.
            float pixels=span/(pixelAngle*max(dot(0.5*(from.xyz+to.xyz),frame.cameraForward.xyz),1.0));
            if(pixels<24.0) {
                gasBoltSegment(ray,from.xyz,mix(from.xyz,to.xyz,shown),light,width,pixelAngle,sum,at,halo);
                continue;
            }
            vec3 along=(to.xyz-from.xyz)/span;
            vec3 across=normalize(cross(along,frame.cameraForward.xyz)+vec3(1e-4,0,0));
            vec3 kink=gasHash3(float(first+k)*1.37+path.z*0.0,fork+3.1)-0.5;
            vec3 middle=0.5*(from.xyz+to.xyz)+span*(across*kink.x*0.36+cross(across,along)*kink.y*0.12);
            vec3 quarter=0.5*(from.xyz+middle)+span*across*kink.z*0.2;
            vec3 three=0.5*(middle+to.xyz)-span*across*kink.x*0.16;
            vec3 points[5]=vec3[5](from.xyz,quarter,middle,three,to.xyz);
            for(int piece=0;piece<4;piece++) {
                float start=0.25*float(piece);
                if(start>=shown) break;
                vec3 next=mix(points[piece],points[piece+1],min((shown-start)*4.0,1.0));
                gasBoltSegment(ray,points[piece],next,light,width,pixelAngle,sum,at,halo);
            }
        }
        // The glow is added at one depth per bolt, its centre's: at the nearest segment's depth, gas in front of it
        // would change abruptly where the nearest segment does.
        float glow=frame.gasBoltShape.y*strength.x*sqrt(halo.x);
        sum+=glow;
        at+=glow*toward;
    }
    return vec4(at/max(sum,1e-12),frame.gasBoltLight.rgb*sum);
}
// The light a bolt sheds on the gas at p (box units), per unit the sample takes away.
vec3 gasBoltLamp(GasData gas, vec3 p, vec4 lamp) {
    if(lamp.w<=0.0) return vec3(0);
    return gas.tint.rgb*frame.gasFlashLight.rgb*lamp.w*exp(-length(p*gas.halfSize.xyz-lamp.xyz)/frame.gasBoltShape.z);
}
// Ice glints in a cold cloud: a few cells of a fixed grid in the cloud's metres hold a glint, a point drawn a pixel or
// two across where the ray passes it, lit by as much starlight as reaches it and twinkling slowly. Added once per cell
// a ray samples, so a glint is the same dot from neighbouring pixels.
vec3 gasGlint(GasData gas, vec3 ray, vec3 p, float pixelAngle, float sunlight, inout vec3 lastCell) {
    float cell=frame.gasCold.y;
    vec3 m=p*gas.halfSize.xyz;
    vec3 c=floor(m/cell);
    if(c==lastCell) return vec3(0);
    lastCell=c;
    float h=gasHash(c+vec3(gas.center.w*0.37,11.1,3.3));
    if(h>=frame.gasCold.z) return vec3(0);
    vec3 g=gas.center.xyz+(c+0.2+0.6*gasHash3(h*311.0,c.x+c.y*7.0+c.z*13.0))*cell;
    float t=dot(g,ray);
    if(t<=0.0) return vec3(0);
    float miss=length(g-ray*t);
    // A glint is a point about a pixel across wherever it is; one whose crystal is smaller than a pixel fades.
    float w=pixelAngle*t*1.2;
    float size=min(1.0,frame.gasCold.w/w);
    float twinkle=0.35+0.65*pow(0.5+0.5*sin(frame.gasKindTime.x*(1.3+2.0*h/frame.gasCold.z)+h*731.0),3.0);
    return vec3(0.85,0.95,1.1)*frame.sunColor.rgb*frame.gasCold.x*sunlight*twinkle*size*size*exp(-(miss*miss)/(w*w));
}
// The ray-distance interval a cloud's box takes of [fromDepth, toDepth] (view depths), clipped as gasMarch clips
// it; empty when y <= x.
vec2 gasClip(GasData gas, vec3 ray, float fromDepth, float toDepth) {
    vec3 origin=-gas.center.xyz/gas.halfSize.xyz, direction=ray/gas.halfSize.xyz;
    float forward=dot(ray,frame.cameraForward.xyz);
    float enter=max(frame.viewProjection[3][2],fromDepth)/forward, leave=toDepth/forward;
    for(int axis=0;axis<3;axis++) {
        if(abs(direction[axis])<1e-12) { if(abs(origin[axis])>=1.0) return vec2(1,0); }
        else {
            float a=(-1.0-origin[axis])/direction[axis], b=(1.0-origin[axis])/direction[axis];
            enter=max(enter,min(a,b)); leave=min(leave,max(a,b));
        }
    }
    return vec2(enter,leave);
}
// The march of one cloud over [fromDepth, toDepth], or, with `joint`, of the stretch where `gas` and `other` overlap:
// there both clouds are sampled at each step, their extinctions add, each scatters its own light in proportion to its
// share of the extinction, and one transmission update follows, so the two mix in depth instead of one lying over the
// other. A joint stretch takes even steps sized by the smaller cloud. One function for both, so every consumer that
// integrates gas inlines one march, not one per case.
vec4 gasMarch(GasData gas, GasData other, bool joint, vec3 ray, float fromDepth, float toDepth, float pixelAngle, float span) {
    // One exit at the end, so the march inlines into gasSegment's stretch loop as one block.
    vec3 light=vec3(0); float transmission=1.0;
    vec3 origin=-gas.center.xyz/gas.halfSize.xyz, direction=ray/gas.halfSize.xyz;
    float forward=dot(ray,frame.cameraForward.xyz);
    float enter=max(frame.viewProjection[3][2],fromDepth)/forward, leave=toDepth/forward;
    bool empty=gas.budget.x<1.0 || toDepth<=fromDepth;
    for(int axis=0;axis<3;axis++) {
        if(abs(direction[axis])<1e-12) { if(abs(origin[axis])>=1.0) empty=true; }
        else {
            float a=(-1.0-origin[axis])/direction[axis], b=(1.0-origin[axis])/direction[axis];
            enter=max(enter,min(a,b)); leave=min(leave,max(a,b));
        }
    }
    if(leave<=enter) empty=true;
    if(!empty) {
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
    if(joint) smallest=min(smallest,min(other.halfSize.x,min(other.halfSize.y,other.halfSize.z)));
    // A stretch of a pair takes its steps at the length the whole pair's span would, so the stretches together spend
    // about the content's budget; a single cloud (span 0) is marched exactly as it always was.
    float stepLength=max(span>0.0 ? span : leave-enter,smallest)/float(budget);
    int count=min(int(ceil((leave-enter)/stepLength)),budget);
    // A large cloud whose gas begins close to the camera spends its steps where the eye can tell them apart: short
    // ones first, growing with distance. Its even steps are hundreds of metres long, far too coarse for the gas a
    // few hundred metres ahead. An interval that begins far off keeps even steps.
    float bend=level==0 && gas.fine.y>0.0 && !joint ? gas.fine.y*clamp(1.0-2.0*enter/(gas.fine.z*gas.halfSize.x),0.0,1.0) : 0.0;
    if(bend>0.0) count=budget;
    // An integer hash of the pixel: neighbours get unrelated offsets, so banding becomes fine grain, without the
    // radial streaks a direction hash gave where ray.xy barely changes or the hatching of a gradient pattern.
    uvec2 pixel=uvec2(gl_FragCoord.xy);
    uint mixed=pixel.x*1664525u+pixel.y*1013904223u;
    mixed^=mixed>>16; mixed*=0x7feb352du; mixed^=mixed>>15; mixed*=0x846ca68bu; mixed^=mixed>>16;
    float jitter=gasJitter>=0.0 ? gasJitter : float(mixed>>8)*(1.0/16777216.0);
    int kind=int(frame.ftl.x+0.5);
    bool passage=frame.ftl.y>0.0 && (kind==2 || kind==3);
    FtlOwnRay ownRay=ftlOwnRay(ray,pixelAngle);
    GasShape shape=gasShape(gas);
    // The weight thins the cloud to the view only. Its own light still crosses the whole cloud, so a level that is
    // fading in or out keeps the shading of the full cloud, and two levels with weights adding to one take the same
    // light away from what is behind them as either would alone.
    float extinction=gas.halfSize.w*gas.motion.y;
    float phase=gasKindPhase(gas,dot(ray,frame.sunDirection.xyz));
    // Kinds. A lightning cloud in a burst has a stroke lighting it from inside and maybe a bolt; a cold one glints
    // close up. Both are uniform per draw, so a normal cloud takes neither branch.
    int cloudKind=gasKindOf(gas), slot=gasSlotOf(gas);
    vec4 stroke=vec4(0), boltLight=vec4(0);
    bool flash=cloudKind==2 && slot>=0 && gas.budget.z<=0.0;
    bool boltPending=false;
    vec4 lampA=vec4(0), lampB=vec4(0);
    if(flash) {
        stroke=frame.gasFlashes[slot];
        if(level>0) stroke.w*=frame.gasKindTime.y;
        // Bolts are lines a few metres wide: the far level is too small on screen for them, but not for their light.
        boltLight=gasBolts(gas,shape,slot,level<2 && gasBoltLines,ray,pixelAngle,lampA,lampB);
        boltPending=boltLight.x>=enter && boltLight.x<=leave && boltLight.yzw!=vec3(0);
    }
    if(!joint) {
    // One cloud, as every cloud was always marched: the paired case below costs an unpaired cloud nothing but this
    // branch, which is the same for every pixel of a draw.
    vec3 lastCell=vec3(1e9);
    bool glints=cloudKind==1 && level==0 && frame.gasCold.x>0.0;
    // Light probes march toward the star: a near one for local self-shadow, three over the chord for the bulk.
    vec3 sunStep=frame.sunDirection.xyz*gas.halfSize.x/gas.halfSize.xyz;
    float near=0.0, far=0.0;
    int nearAt=-1, farAt=-1;
    // The sun direction in envelope space, for the chord to where the fringe ends (|e|^2 = 3.6).
    vec3 sunEnvelope=sunStep*shape.scale;
    float sunReach=dot(sunEnvelope,sunEnvelope);
    for(int i=0;i<64;i++) {
        if(i>=count || transmission<0.01) break;
        float from=gasStepStart(enter,stepLength,bend,budget,i);
        if(bend>0.0 && from>=leave) break;
        float width=clamp(leave-from,0.0,gasStepLength(stepLength,bend,budget,i));
        float t=from+jitter*width;
        // A bolt is light at its own distance: whatever gas lies in front of it there dims it.
        if(boltPending && boltLight.x<t) { light+=transmission*boltLight.yzw; boltPending=false; }
        // Test the actual sample, including own-hull pixels that the surface compositor protects.
        float real=passage ? ftlOwnReal(ownRay,t*forward) : 1.0;
        if(real<=0.0) continue;
        vec3 p=origin+direction*t;
        float d=gasDensity(gas,shape,p,detail)*real;
        if(d<0.001) continue;
        float relief=0.0;
        if(level==0 && gas.fine.x>0.0) {
            vec2 strands=gasStrands(gas,shape,p,width,t);
            relief=d*strands.y;
            d*=strands.x;
            if(d<0.001) continue;
        }
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
            vec3 lit=gasSampleLight(gas,phase,near+far);
            if(flash) lit+=gasFlashAt(gas,shape,p,stroke)+gasBoltLamp(gas,p,lampA)+gasBoltLamp(gas,p,lampB);
            light+=transmission*seen*lit;
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
            near=gasDensity(gas,shape,gasStepPoint(origin,direction,enter,stepLength,bend,budget,leave,jitter,pair)+sunStep*0.035,1)*0.07;
        }
        if(quad!=farAt) {
            farAt=quad;
            vec3 q=gasStepPoint(origin,direction,enter,stepLength,bend,budget,leave,jitter,quad);
            vec3 e=(q-shape.lean)*shape.scale;
            float b=dot(e,sunEnvelope), c=dot(e,e)-3.6;
            // Homogeneous fixture gas has no envelope; a unit chord keeps its light the same everywhere.
            float chord=gas.budget.z>0.0 ? 1.0 : clamp((-b+sqrt(max(b*b-sunReach*c,0.0)))/sunReach,0.1,2.0);
            far=(gasDensity(gas,shape,q+sunStep*(chord*0.2),0)
                +gasDensity(gas,shape,q+sunStep*(chord*0.5),0)
                +gasEnvelope(gas,shape,q+sunStep*(chord*0.8)))*(chord*0.33);
        }
        float depth=max(near+far+relief,0.0);
        float absorbed=1.0-exp(-d*extinction*width);
        vec3 lit=gasSampleLight(gas,phase,depth);
        if(flash) lit+=gasFlashAt(gas,shape,p,stroke)+gasBoltLamp(gas,p,lampA)+gasBoltLamp(gas,p,lampB);
        light+=transmission*absorbed*lit;
        if(glints) light+=transmission*gasGlint(gas,ray,p,pixelAngle,exp(-depth*gas.halfSize.x*gas.halfSize.w),lastCell);
        transmission*=1.0-absorbed;
    }
    }
    else {
    // Per cloud of the sample (the pushed one, then in a joint stretch the other): the last glint cell, and the light
    // probes, near and far, with the pair and group of four steps they were taken for. Both clouds go through the one
    // body below, so its density and light code is compiled once for the two.
    vec3 lastCells[2]=vec3[2](vec3(1e9),vec3(1e9));
    vec2 nearProbe=vec2(0), farProbe=vec2(0);
    ivec2 nearProbeAt=ivec2(-1), farProbeAt=ivec2(-1);
    int clouds=2;
    // Everything about the clouds that does not change along the ray, worked out once: their shapes, frames, phases,
    // extinctions and the star's direction in their units.
    GasShape shapes[2]=GasShape[2](shape,gasShape(other));
    vec3 starts[2]=vec3[2](origin,-other.center.xyz/other.halfSize.xyz);
    vec3 towards[2]=vec3[2](direction,ray/other.halfSize.xyz);
    vec3 sunSteps[2]=vec3[2](frame.sunDirection.xyz*gas.halfSize.x/gas.halfSize.xyz,frame.sunDirection.xyz*other.halfSize.x/other.halfSize.xyz);
    vec3 sunEnvelopes[2]=vec3[2](sunSteps[0]*shapes[0].scale,sunSteps[1]*shapes[1].scale);
    vec2 sunReaches=vec2(dot(sunEnvelopes[0],sunEnvelopes[0]),dot(sunEnvelopes[1],sunEnvelopes[1]));
    vec2 phases=vec2(phase,gasKindPhase(other,dot(ray,frame.sunDirection.xyz)));
    vec2 extinctions=vec2(extinction,other.halfSize.w*other.motion.y);
    for(int i=0;i<64;i++) {
        if(i>=count || transmission<0.01) break;
        float from=gasStepStart(enter,stepLength,bend,budget,i);
        if(bend>0.0 && from>=leave) break;
        float width=clamp(leave-from,0.0,gasStepLength(stepLength,bend,budget,i));
        float t=from+jitter*width;
        // A bolt is light at its own distance: whatever gas lies in front of it there dims it.
        if(boltPending && boltLight.x<t) { light+=transmission*boltLight.yzw; boltPending=false; }
        // Test the actual sample, including own-hull pixels that the surface compositor protects.
        float real=passage ? ftlOwnReal(ownRay,t*forward) : 1.0;
        if(real<=0.0) continue;
        // Extinction per metre and the light scattered per unit taken away, summed over the clouds weighted by their
        // extinction.
        float sigma=0.0;
        vec3 lit=vec3(0), sparkle=vec3(0);
        for(int k=0;k<2;k++) {
            if(k>=clouds) break;
            GasData cloud=k==0 ? gas : other;
            GasShape cut=shapes[k];
            vec3 start=starts[k], toward=towards[k];
            // Light probes march toward the star: a near one for local self-shadow, three over the chord for the bulk.
            vec3 sunStep=sunSteps[k];
            // The sun direction in envelope space, for the chord to where the fringe ends (|e|^2 = 3.6).
            vec3 sunEnvelope=sunEnvelopes[k];
            float sunReach=sunReaches[k];
            vec3 p=start+toward*t;
            float d=gasDensity(cloud,cut,p,detail)*real;
            if(d<0.001) continue;
            float relief=0.0;
            if(level==0 && cloud.fine.x>0.0) {
                vec2 strands=gasStrands(cloud,cut,p,width,t);
                relief=d*strands.y;
                d*=strands.x;
                if(d<0.001) continue;
            }
            float depth;
            if(level>0) {
                // Local self-shadow follows the sample's own density, which is what the near probe finds on average.
                nearProbe[k]=d*0.07;
                vec3 e=(p-cut.lean)*cut.scale;
                float b=dot(e,sunEnvelope), c=dot(e,e)-3.6;
                float chord=cloud.budget.z>0.0 ? 1.0 : clamp((-b+sqrt(max(b*b-sunReach*c,0.0)))/sunReach,0.1,2.0);
                int pair=i&~1;
                if(pair!=farProbeAt[k]) {
                    farProbeAt[k]=pair;
                    farProbe[k]=(gasDensity(cloud,cut,p+sunStep*(chord*0.2),0)+gasDensity(cloud,cut,p+sunStep*(chord*0.5),0)
                        +gasEnvelope(cloud,cut,p+sunStep*(chord*0.8)))*(chord*0.33);
                }
                depth=nearProbe[k]+farProbe[k];
            }
            else {
                // One near probe resolves local self-shadow. The others are spread over the sun's chord through the
                // outer envelope, so a thick core really shades the far side wherever the sample is. Light changes
                // slowly along the view ray, so the near probe belongs to each pair of steps and the chord to each
                // group of four, both taken at the group's first step whether or not that step held gas. Taking them
                // where the last lit step happened to be instead made a sample's light jump when a neighbour crossed the
                // density cut by a rounding error, which is how the ordinary pass and composition disagreed at single
                // pixels. The group index is the same for every pixel in a wave, so this removes lookups for whole waves.
                int pair=i&~1, quad=i&~3;
                if(pair!=nearProbeAt[k]) {
                    nearProbeAt[k]=pair;
                    nearProbe[k]=gasDensity(cloud,cut,gasStepPoint(start,toward,enter,stepLength,bend,budget,leave,jitter,pair)+sunStep*0.035,1)*0.07;
                }
                if(quad!=farProbeAt[k]) {
                    farProbeAt[k]=quad;
                    vec3 q=gasStepPoint(start,toward,enter,stepLength,bend,budget,leave,jitter,quad);
                    vec3 e=(q-cut.lean)*cut.scale;
                    float b=dot(e,sunEnvelope), c=dot(e,e)-3.6;
                    // Homogeneous fixture gas has no envelope; a unit chord keeps its light the same everywhere.
                    float chord=cloud.budget.z>0.0 ? 1.0 : clamp((-b+sqrt(max(b*b-sunReach*c,0.0)))/sunReach,0.1,2.0);
                    farProbe[k]=(gasDensity(cloud,cut,q+sunStep*(chord*0.2),0)
                        +gasDensity(cloud,cut,q+sunStep*(chord*0.5),0)
                        +gasEnvelope(cloud,cut,q+sunStep*(chord*0.8)))*(chord*0.33);
                }
                depth=max(nearProbe[k]+farProbe[k]+relief,0.0);
            }
            // The weight thins the cloud to the view only (see extinction above); a partner takes the pushed weight.
            float own=d*extinctions[k];
            vec3 scattered=gasSampleLight(cloud,phases[k],depth);
            if(flash) scattered+=gasFlashAt(cloud,cut,p,stroke);
            lit+=scattered*own;
            sigma+=own;
            if(level==0 && gasKindOf(cloud)==1 && frame.gasCold.x>0.0)
                sparkle+=gasGlint(cloud,ray,p,pixelAngle,exp(-depth*cloud.halfSize.x*cloud.halfSize.w),lastCells[k]);
        }
        if(sigma<=0.0) continue;
        lit/=sigma;
        float absorbed=1.0-exp(-sigma*width);
        light+=transmission*absorbed*lit;
        if(sparkle!=vec3(0)) light+=transmission*sparkle;
        transmission*=1.0-absorbed;
    }
    }
    if(boltPending) light+=transmission*boltLight.yzw;
    }
    return vec4(light,transmission);
}

// Every consumer integrates gas through this: one cloud, or a cold/normal pair whose second cloud is in gasPartners.
// The partner takes the pushed draw's level, sample budget and weight, so both clouds change level together. A pair's
// ray is cut at both boxes' ends into at most three stretches: one cloud alone, both jointly, one cloud alone. Volume
// light composes front to back, so the stretches add up to the whole. All of them go through one call of gasMarch.
vec4 gasSegment(GasData gas, vec3 ray, float fromDepth, float toDepth, float pixelAngle) {
    int slot=gasSlotOf(gas);
    bool paired=slot>=0 && gasKindOf(gas)!=2 && gas.budget.z<=0.0;
    GasData partner=gas;
    // Stretches: which cloud (0 the pushed one, 1 its partner, 2 both) over which view depths.
    int which0=0, which1=0, which2=0;
    vec2 range0=vec2(fromDepth,toDepth), range1=vec2(0), range2=vec2(0);
    int count=1;
    float span=0.0;
    if(paired) {
        partner=frame.gasPartners[slot];
        partner.budget=gas.budget;
        partner.motion.y=gas.motion.y;
        partner.tint.w=gas.tint.w;
        partner.halfSize.w*=gasThin;
        vec2 ia=gasClip(gas,ray,fromDepth,toDepth), ib=gasClip(partner,ray,fromDepth,toDepth);
        bool hasA=ia.y>ia.x, hasB=ib.y>ib.x;
        float forward=dot(ray,frame.cameraForward.xyz);
        float o0=max(ia.x,ib.x), o1=min(ia.y,ib.y);
        span=max(hasA ? ia.y : ib.y,hasB ? ib.y : ia.y)-min(hasA ? ia.x : ib.x,hasB ? ib.x : ia.x);
        if(!hasA || !hasB) which0=hasA ? 0 : 1;
        else if(o1<=o0) {
            // Both on the ray, one after the other.
            which0=ia.x<=ib.x ? 0 : 1;
            which1=1-which0;
            range1=range0;
            count=2;
        }
        else {
            which0=ia.x<=ib.x ? 0 : 1;
            range0=vec2(fromDepth,o0*forward);
            which1=2;
            range1=vec2(o0*forward,o1*forward);
            which2=ia.y>=ib.y ? 0 : 1;
            range2=vec2(o1*forward,toDepth);
            count=3;
        }
    }
    vec4 total=vec4(0,0,0,1);
    for(int k=0;k<3;k++) {
        if(k<count && total.a>=0.01) {
            int which=k==0 ? which0 : k==1 ? which1 : which2;
            vec2 range=k==0 ? range0 : k==1 ? range1 : range2;
            bool second=which==1;
            vec4 part=gasMarch(second ? partner : gas,second ? gas : partner,which==2,ray,range.x,range.y,pixelAngle,span);
            total=vec4(total.rgb+total.a*part.rgb,total.a*part.a);
        }
    }
    return total;
}
vec3 gasOver(vec4 volume, vec3 background) { return volume.rgb+volume.a*background; }
#endif
