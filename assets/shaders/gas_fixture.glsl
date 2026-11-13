// Muted regression fixtures run the actual production gasComposite, sorting, resets and segment loops.
// Only source texture inputs and density are controlled. These are not gameplay or performance scenarios.
const vec3 GasFixtureRock=vec3(0.24,0.32,0.4);
const vec3 GasFixtureLight=vec3(0.2,0.3,0.4);
bool gasFixtureSource(out vec3 base, out float cutoff, out vec3 delta, out float shieldAt, out bool ownSource)
{
    int id=int(frame.gas.budget.z+0.5);
    base=GasFixtureRock; cutoff=2.0; delta=vec3(0); shieldAt=cutoff; ownSource=false;
    if(id==0) return false;
    ownSource=id==3 || id==4 || id==11 || id==12;
    if(id==5 || id==6) { cutoff=100.0; shieldAt=8.0; delta=GasFixtureLight; }
    if(id==7) { cutoff=100.0; shieldAt=cutoff; }
    return true;
}
vec4 gasFixtureResult()
{
    int id=int(frame.gas.budget.z+0.5);
    // A wrong global mode selection must fail, even though this fixture supplies controlled source textures.
    if(frame.gas.budget.x<1.0 || ((id==5 || id==6) && frame.gas.budget.w<1.0)) return vec4(1,0,0,1);
    vec3 ray=normalize(vec3(0.01,0,-1));
    vec2 pos; project(ray,pos);
    float angle=2.0*frame.lens.x/frame.lens.w;
    GasEvent events[5]; int count=0;
    vec3 expected;
    if(id<=4 || id>=9) {
        bool own=id==3 || id==4 || id==11 || id==12;
        vec2 bend=vec2(0); int mask=0; vec3 glow=vec3(0);
        int kind=int(frame.ftl.x+0.5);
        float real=ownPassage(kind,ray,pos,angle,2.0,own,bend,mask,glow);
        vec3 throat=kind==3 ? riftThroat(ray,0.0,frame.ftl.w,0.0,angle) : wormholeThroat(ray,0.0,frame.ftl.w,0.0,angle);
        events[count++]=GasEvent(gasMouth(4,ray),bend,vec4(throat,1.0-real),glow,mask,4,true);
        // Exact homogeneous oracle: sample visibility is 0.5 on the camera side, even for a protected own hull.
        float distance=(2.0-frame.viewProjection[3][2])/dot(ray,frame.cameraForward.xyz);
        float transmission=exp(-frame.gas.halfSize.w*0.5*distance);
        // Homogeneous gas lights every sample alike. This restates the shipped light model independently on purpose:
        // it does not call gasPhase or gasSampleLight, so any change there turns these fixtures red until this
        // expectation is updated deliberately. Probe depth: near probe 0.07 plus three chord probes of 0.33 at unit
        // chord, all at unit density (fixture mode fixes the chord and the envelope probe).
        float cosine=dot(ray,frame.sunDirection.xyz);
        float phase=0.65*(0.6*0.8775/pow(1.1225-0.7*cosine,1.5)+0.4*0.9775/pow(1.0225+0.3*cosine,1.5));
        float optical=(0.07+3.0*0.33)*frame.gas.halfSize.x*frame.gas.halfSize.w;
        float direct=exp(-optical)*phase+0.2*exp(-0.22*optical);
        float open=0.4+0.6*exp(-0.5*optical);
        vec3 scattering=frame.gas.tint.rgb*(vec3(0.035,0.042,0.055)*open+frame.sunColor.rgb*direct)*(1.0-transmission);
        expected=(own ? transmission*GasFixtureRock : mix(throat,transmission*GasFixtureRock,0.5))+scattering;
    } else {
        int openings=id==6 ? 1 : id==7 || id==8 ? 2 : 0;
        for(int i=0;i<2;i++) {
            if(i>=openings) break;
            vec2 bend=vec2(0); int mask=0; vec4 cover=vec4(0); vec3 glow=vec3(0);
            opening(i,ray,pos,angle,0.0,Far,bend,mask,cover,glow);
            if(bend!=vec2(0) || cover.a>0.0 || glow!=vec3(0)) {
                if(cover.a>0.0) cover.rgb/=cover.a;
                events[count++]=GasEvent(gasMouth(i,ray),bend,cover,glow,mask,i,false);
            }
        }
        if(id==5 || id==6) {
            vec4 volume=gasSegment(frame.gas,ray,0.0,100.0,angle);
            float front=gasSegment(frame.gas,ray,0.0,8.0,angle).a;
            expected=gasOver(volume,GasFixtureRock)+front*GasFixtureLight;
            if(count!=0 || front>=0.999) return vec4(1,0,0,1);
        } else if(id==7) {
            if(count!=2 || events[0].depth<=events[1].depth || events[1].cover.a<1.0) return vec4(1,0,0,1);
            expected=gasOver(gasSegment(frame.gas,ray,0.0,events[1].depth,angle),events[1].cover.rgb+events[1].glow);
        } else expected=GasFixtureRock; // A nearer opaque source still suppresses the full external cover.
    }
    gasFixtureMarches=0;
    vec3 actual=gasComposite(frame.gas,events,count,pos,frame.lens.y,angle);
    int marches=gasFixtureMarches;
    vec3 error=abs(actual-expected);
    bool passed=max(error.x,max(error.y,error.z))<0.0002;
    if(id==7 || id==8) passed=passed && marches==1;
    // Left: actual production-compositor output. Right quarter: green pass/red failure; blue encodes marches/16.
    if(gl_FragCoord.x/frame.lens.z>0.75) return vec4(passed ? vec3(0,1,float(marches)/16.0) : vec3(1,0,0),1);
    return vec4(pow(filmic(actual),vec3(1.0/2.2)),1);
}
