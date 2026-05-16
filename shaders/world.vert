#version 450

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUV;

layout(push_constant) uniform PushConstants {
    mat4 model;
    mat4 view;
    mat4 proj;
    vec4 tint;
    float glow;
    float repeatU;
    float repeatV;
    float offsetU;
    float offsetV;
    float rotation;
    float _pad0;
    float _pad1;
} pc;

layout(location = 0) out vec3 fragPos;
layout(location = 1) out vec3 fragNormal;
layout(location = 2) out vec2 fragUV;
layout(location = 3) out vec4 fragTint;
layout(location = 4) out float fragGlow;

void main() {
    vec4 worldPos = pc.model * vec4(inPosition, 1.0);
    gl_Position   = pc.proj * pc.view * worldPos;

    fragPos    = worldPos.xyz;
    fragNormal = normalize(mat3(transpose(inverse(pc.model))) * inNormal);

    // SL UV transform: repeat, offset, rotation
    vec2 uv = inUV * vec2(pc.repeatU, pc.repeatV) + vec2(pc.offsetU, pc.offsetV);
    if (abs(pc.rotation) > 0.001) {
        float s = sin(pc.rotation), c = cos(pc.rotation);
        uv -= 0.5;
        uv  = vec2(uv.x * c - uv.y * s, uv.x * s + uv.y * c);
        uv += 0.5;
    }
    fragUV   = uv;
    fragTint = pc.tint;
    fragGlow = pc.glow;
}
