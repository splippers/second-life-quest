#version 450

layout(location = 0) in vec2 fragUV;
layout(location = 1) in vec4 fragColor;

layout(set = 0, binding = 0) uniform sampler2D uiTex;

layout(location = 0) out vec4 outColor;

void main() {
    vec4 tex = texture(uiTex, fragUV);
    outColor  = tex * fragColor;
    if (outColor.a < 0.01) discard;
}
