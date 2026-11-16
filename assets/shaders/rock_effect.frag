#version 450
layout(location=0) in vec2 uv;
layout(location=1) in vec4 tint;
layout(location=2) flat in vec2 kind;
layout(location=0) out vec4 color;
void main() {
    float r = length(uv);
    float edge = 1.0 - smoothstep(0.35,1.0,r);
    float opacity = edge * tint.a;
    if (opacity < 0.002) discard;
    // Premultiplied additive dust can brighten a rock, never attenuate its existing HDR light.
    color = vec4(max(tint.rgb, vec3(0.0)) * opacity, 0.0);
}
