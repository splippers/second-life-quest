#version 450

layout(location = 0) in vec3 fragPos;
layout(location = 1) in vec3 fragNormal;
layout(location = 2) in vec2 fragUV;
layout(location = 3) in vec4 fragTint;
layout(location = 4) in float fragGlow;

layout(set = 0, binding = 0) uniform sampler2D diffuseTex;

layout(location = 0) out vec4 outColor;

// Simple directional light matching SL's default sun angle
const vec3  LIGHT_DIR     = normalize(vec3(0.5, 1.0, 0.5));
const vec3  LIGHT_COLOR   = vec3(1.0, 0.97, 0.88);
const vec3  AMBIENT_COLOR = vec3(0.2, 0.22, 0.28);

void main() {
    vec4 texColor = texture(diffuseTex, fragUV) * fragTint;
    if (texColor.a < 0.01) discard;

    // Diffuse
    float diff    = max(dot(normalize(fragNormal), LIGHT_DIR), 0.0);
    vec3  diffuse = diff * LIGHT_COLOR;
    vec3  ambient = AMBIENT_COLOR;

    vec3 lighting = ambient + diffuse;

    // Glow (fullbright): glow=1 means ignore lighting entirely
    vec3 result = mix(texColor.rgb * lighting, texColor.rgb, fragGlow);

    outColor = vec4(result, texColor.a);
}
