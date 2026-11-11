#version 450
#include "frame.glsl"

// Per star: direction on the sky and apparent size in pixels; colour and brightness.
layout(location = 0) in vec4 directionSize;
layout(location = 1) in vec4 colorIn;

layout(location = 0) out vec2 corner;
layout(location = 1) out vec3 color;
layout(location = 2) flat out float stretch;

const vec2 corners[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

void main()
{
    vec3 dir = directionSize.xyz;
    corner = corners[gl_VertexIndex];
    color = colorIn.rgb;
    float size = directionSize.w;

    // A hyperspace jump draws each star as a line from where it is toward the point straight behind the hull, as if
    // it were streaming past. The line runs along the great circle through the hull's nose, so it is laid out around
    // the hull and not the view: looking sideways the lines run across the screen, looking back they converge. The
    // far end is the star moved away from the nose in the way a boost moves the sky: tan(angle / 2) is scaled.
    vec3 tail = dir;
    float reach = frame.ftlShape.x;
    if (reach > 0.0)
    {
        vec3 axis = frame.ftlAxis.xyz;
        float c = clamp(dot(dir, axis), -1.0, 1.0);
        vec3 side = dir - axis * c;
        float s = length(side);
        if (s > 1e-4)
        {
            float far = 2.0 * atan((1.0 + reach) * tan(acos(c) * 0.5));
            tail = axis * cos(far) + side / s * sin(far);
        }
    }

    // w = 0 places a point at infinity: it turns with the camera and never moves with it. An end behind the camera
    // is brought forward along the line to just in front of it, so a line that passes the edge of the view still
    // runs off the screen in the right direction.
    vec4 head = frame.viewProjection * vec4(dir, 0.0);
    vec4 end = frame.viewProjection * vec4(tail, 0.0);
    const float front = 1e-3;
    if (head.w < front && end.w < front)
    {
        gl_Position = vec4(0.0, 0.0, 0.0, -1.0);
        stretch = 1.0;
        return;
    }
    if (head.w < front)
        head = mix(head, end, (front - head.w) / (end.w - head.w));
    else if (end.w < front)
        end = mix(end, head, (front - end.w) / (head.w - end.w));

    // In pixels: with no jump the line has no length and the quad is the plain star sprite.
    vec2 halfScreen = frame.lens.zw * 0.5;
    vec2 a = head.xy / head.w * halfScreen;
    vec2 b = end.xy / end.w * halfScreen;
    vec2 span = b - a;
    float halfLength = length(span) * 0.5;
    vec2 along = halfLength > 0.25 ? span / (halfLength * 2.0) : vec2(1.0, 0.0);
    vec2 across = vec2(-along.y, along.x);
    vec2 pixel = (a + b) * 0.5 + along * corner.x * (halfLength + size) + across * corner.y * size;
    gl_Position = vec4(pixel / halfScreen, 0.0, 1.0);
    stretch = (halfLength + size) / size;
}
