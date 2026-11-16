#version 450
layout(location=0) in vec2 uv;
layout(location=1) in vec4 tint;
layout(location=2) flat in vec2 kind;
layout(location=0) out vec4 color;
void main() {
    float r = length(uv);
    float edge = 1.0 - smoothstep(0.35,1.0,r);
    float grain = 0.8 + 0.2 * sin(uv.x*47.0 + sin(uv.y*31.0));
    float opacity = edge * grain * tint.a;
    if (kind.x > 1.5) opacity *= 0.6 + 0.4 * sin(kind.y * 3.0 + r * 12.0);
    if (opacity < 0.002) discard;
    color = vec4(tint.rgb,opacity);
}
