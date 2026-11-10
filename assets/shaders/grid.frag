#version 450
#include "frame.glsl"

layout(location = 0) in vec3 planePosition;
layout(location = 0) out vec4 outColor;

float lines(vec2 coordinate, float spacing, float widthPixels)
{
    vec2 scaled = coordinate / spacing;
    vec2 derivative = fwidth(scaled);
    vec2 distanceToLine = abs(fract(scaled - 0.5) - 0.5) / max(derivative, vec2(1e-6));
    float line = 1.0 - min(min(distanceToLine.x, distanceToLine.y) / widthPixels, 1.0);
    // Lines closer together than a few pixels would turn into a solid sheet; fade them out instead.
    return line * (1.0 - smoothstep(0.15, 0.5, max(derivative.x, derivative.y)));
}

void main()
{
    float sectorSize = frame.gridPlane.y;
    vec2 kilometre = planePosition.xz + frame.grid.xy;
    // Sector boundaries lie half a sector from each sector's centre.
    vec2 sector = planePosition.xz + frame.grid.zw + sectorSize * 0.5;

    float close = lines(kilometre, 100.0, 1.0) * 0.14;
    float fine = max(lines(kilometre, 1000.0, 1.0) * 0.28, close);
    float coarse = lines(kilometre, 5000.0, 1.2) * 0.4;
    float boundary = lines(sector, sectorSize, 2.0);

    float range = length(planePosition);
    float fade = 1.0 - smoothstep(60000.0, 115000.0, range);
    vec3 color = mix(vec3(0.25, 0.45, 0.7), vec3(1.0, 0.69, 0.13), boundary);
    float alpha = max(max(fine, coarse), boundary) * fade * frame.gridPlane.z;
    outColor = vec4(color, alpha * 0.5);
}
