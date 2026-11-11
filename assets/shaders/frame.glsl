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
                       // (1 drive working, 2 in transit, 3 arriving), z: progress through the stage 0..1
} frame;
