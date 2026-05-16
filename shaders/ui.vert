#version 450

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUV;

layout(push_constant) uniform PC {
    mat4  transform;
    vec4  color;
    float uvOffsetX;
    float uvOffsetY;
    float uvScaleX;
    float uvScaleY;
} pc;

layout(location = 0) out vec2 fragUV;
layout(location = 1) out vec4 fragColor;

void main() {
    gl_Position = pc.transform * vec4(inPosition, 1.0);
    fragUV      = inUV * vec2(pc.uvScaleX, pc.uvScaleY) + vec2(pc.uvOffsetX, pc.uvOffsetY);
    fragColor   = pc.color;
}
