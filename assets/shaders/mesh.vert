#version 450
#include "frame.glsl"

layout(location = 0) in vec3 position;
layout(location = 1) in vec3 normal;

// Per instance: a 3x4 model matrix (rows), camera-relative; base colour with material kind in alpha (0 rock, 1 hull,
// 2 the pilot's own hull), plus four times one more than the passage slot whose opening cuts the hull.
layout(location = 2) in vec4 row0;
layout(location = 3) in vec4 row1;
layout(location = 4) in vec4 row2;
layout(location = 5) in vec4 tint;

layout(location = 0) out vec3 worldNormal;
layout(location = 1) out vec3 worldPosition;
layout(location = 2) out vec3 localPosition;
layout(location = 3) out vec4 color;
layout(location = 4) flat out int cut;

void main()
{
    vec4 p = vec4(position, 1.0);
    worldPosition = vec3(dot(row0, p), dot(row1, p), dot(row2, p));
    worldNormal = vec3(dot(row0.xyz, normal), dot(row1.xyz, normal), dot(row2.xyz, normal));
    localPosition = position;
    int slot = int((tint.a + 0.5) * 0.25);
    cut = slot - 1;
    color = vec4(tint.rgb, tint.a - 4.0 * float(slot));
    gl_Position = frame.viewProjection * vec4(worldPosition, 1.0);
}
