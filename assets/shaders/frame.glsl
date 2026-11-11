// Per-frame constants shared by every scene shader. Positions are relative to the camera: the world is
// rebased around it every frame so single precision never has to represent a galactic coordinate.
layout(set = 0, binding = 0) uniform Frame
{
    mat4 viewProjection; // Camera-relative world to clip. Reverse depth: 1 at the near plane, 0 at infinity.
    vec4 cameraRight;
    vec4 cameraUp;
    vec4 cameraForward;
    vec4 lens;         // x: tan(half vertical field of view), y: aspect ratio, z: viewport width, w: viewport height
    vec4 sunDirection; // xyz: unit vector toward the sector's star
    vec4 sunColor;     // rgb: star light colour and intensity
    vec4 dust;         // xyz: camera position modulo the dust cell, w: time in seconds
    vec4 drift;        // xyz: camera velocity in m/s, w: speed
    vec4 grid;         // x, y: camera x and z modulo one kilometre; z, w: camera x and z modulo one sector
    vec4 gridPlane;    // x: plane height relative to the camera, y: sector size, z: opacity, w: exposure
    vec4 ftl;          // the viewer's own jump. x: drive kind (1 hyperspace, 2 wormhole, 3 rift), y: stage
                       // (1 drive working, 2 in transit, 3 arriving), z: progress through the stage 0..1, w: seed
    vec4 ftlAxis;      // xyz: the jump's axis, the hull's nose, in world space; w: how far the tunnel has streamed
    vec4 ftlUp;        // xyz: up across the axis; w: hyperspace, how much of the view the tunnel covers, 0..1;
                       // rift or wormhole, how far the camera is past the current opening, 0..1
    vec4 ftlEntry;     // the opening the hull flies into: xyz camera-relative centre, w radius in metres (0: none)
    vec4 ftlExit;      // the opening the hull comes out of: the same
    vec4 ftlShape;     // x: how far hyperspace stretches the stars, y: how far the entry is open, z: the exit,
                       // w: how much of the tunnel's or throat's light falls on the pilot's hull
    vec4 openings[16]; // rifts and wormholes seen from outside, four vectors each: xyz camera-relative centre and w
                       // radius fully open in metres; xyz the axis it faces (the hull's nose); xyz up across it; x
                       // drive kind (2 wormhole, 3 rift), y how far open 0..1, z seed
    vec4 clips[64];    // per followed jump (passage slot), two vectors: xyz a camera-relative point on the plane its
                       // opening cuts its own hull at, w the radius of a window in it through which the hidden side
                       // is still seen (0: none); and xyz the plane's normal toward the hidden side (zero: none)
} frame;
