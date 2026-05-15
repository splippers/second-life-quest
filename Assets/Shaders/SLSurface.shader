// SLSurface — URP Lit-compatible surface shader for Second Life prims.
// Extends the standard Lit behaviour with per-face UV rotation (SL texture rotation)
// and Fullbright mode (zero ambient/diffuse lighting, direct emission only).
Shader "SLQuest/SLSurface"
{
    Properties
    {
        _BaseMap       ("Texture",      2D)         = "white" {}
        _BaseColor     ("Color",        Color)      = (1,1,1,1)
        [Toggle] _Fullbright ("Fullbright", Float)  = 0
        _Glow          ("Glow",         Range(0,1)) = 0
        _Smoothness    ("Smoothness",   Range(0,1)) = 0
        _Metallic      ("Metallic",     Range(0,1)) = 0
        [Normal] _BumpMap ("Normal Map",2D)         = "bump" {}
        _UVRotation    ("UV Rotation",  Float)      = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Enum(Off,0,On,1)] _ZWrite ("ZWrite", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("SrcBlend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("DstBlend", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            Cull   [_Cull]
            ZWrite [_ZWrite]
            Blend  [_SrcBlend] [_DstBlend]

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma shader_feature UV_ROTATION
            #pragma shader_feature _FULLBRIGHT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float  _Fullbright;
                float  _Glow;
                float  _Smoothness;
                float  _Metallic;
                float  _UVRotation;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float3 tangentWS   : TEXCOORD3;
                float3 bitangentWS : TEXCOORD4;
                float  fogFactor   : TEXCOORD5;
            };

            float2 RotateUV(float2 uv, float radians)
            {
                float s = sin(radians);
                float c = cos(radians);
                // Rotate around centre (0.5, 0.5)
                uv -= 0.5;
                uv  = float2(uv.x * c - uv.y * s,
                             uv.x * s + uv.y * c);
                return uv + 0.5;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.positionWS  = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.normalWS    = TransformObjectToWorldNormal(IN.normalOS);

                VertexNormalInputs tbn = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);
                OUT.tangentWS   = tbn.tangentWS;
                OUT.bitangentWS = tbn.bitangentWS;

                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
#ifdef UV_ROTATION
                OUT.uv = RotateUV(OUT.uv, _UVRotation);
#endif
                OUT.fogFactor = ComputeFogFactor(OUT.positionHCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float4 baseColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;

                // Normal mapping
                float4 normalSample = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv);
                float3 normalTS = UnpackNormal(normalSample);
                float3 normalWS = TransformTangentToWorld(
                    normalTS,
                    float3x3(IN.tangentWS, IN.bitangentWS, IN.normalWS));
                normalWS = normalize(normalWS);

                half4 color;

#ifdef _FULLBRIGHT
                // Fullbright: ignore lighting, use base colour + glow as emission
                color = baseColor;
                color.rgb = min(color.rgb * (1 + _Glow * 4), 16.0);
#else
                // Standard PBR lighting (URP)
                InputData inputData = (InputData)0;
                inputData.positionWS     = IN.positionWS;
                inputData.normalWS       = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord    = TransformWorldToShadowCoord(IN.positionWS);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo      = baseColor.rgb;
                surfaceData.alpha       = baseColor.a;
                surfaceData.smoothness  = _Smoothness;
                surfaceData.metallic    = _Metallic;
                surfaceData.emission    = baseColor.rgb * _Glow;
                surfaceData.normalTS    = normalTS;

                color = UniversalFragmentPBR(inputData, surfaceData);
#endif
                color.rgb = MixFog(color.rgb, IN.fogFactor);
                return color;
            }
            ENDHLSL
        }

        // Shadow caster pass
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex   ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
    CustomEditor "UnityEditor.Rendering.Universal.ShaderGUI.LitShader"
}
