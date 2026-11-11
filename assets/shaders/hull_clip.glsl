// Whether a point of a hull is cut away by a jump's opening, for every pass that draws hulls (the mesh and anything
// laid over a hull, such as its shield), so they all leave out the same part of it. Needs frame.glsl; includes the
// jump functions it uses. Call it from a fragment shader: the pilot's own hull dissolves on a fixed pattern of the
// screen, read from gl_FragCoord.
#ifndef HULL_CLIP_GLSL
#define HULL_CLIP_GLSL
#include "ftl.glsl"
#include "ftl_own.glsl"

// slot: the passage slot whose opening cuts the hull (-1: none), as mesh.vert decodes it from the material. own:
// the pilot's own hull seen from inside its jump (material kind 2), which takes no slot and is cut by the frame's
// own jump instead.
bool hullCut(int slot, bool own, vec3 worldPosition)
{
    // Another hull, or the pilot's own seen from outside: cut flat at its opening's plane, before anything is stored.
    if (slot >= 0 && dot(worldPosition - frame.clips[slot * 2].xyz, frame.clips[slot * 2 + 1].xyz) > 0.0)
        return true;

    // The pilot's own hull inside its own rift or wormhole shows as much of each point as the composite shows of the
    // side it is on, from the same frame constants. Where that is a part of it, as the camera's side dissolves, a
    // fixed pattern on the screen keeps that share of the hull's pixels, so it dissolves with the view around it.
    int ownKind = int(frame.ftl.x + 0.5);
    if (own && frame.ftl.y > 0.5 && (ownKind == 2 || ownKind == 3))
    {
        float shows = ftlOwnHullShows(worldPosition, 2.0 / frame.lens.w * frame.lens.x);
        float pattern = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
        return shows <= pattern;
    }
    return false;
}
#endif
