using OpenMetaverse;
using UnityEngine;

namespace SLQuest.Rendering
{
    /// <summary>
    /// Converts a Second Life face's <see cref="Primitive.TextureEntryFace"/> into
    /// a Unity URP <see cref="Material"/>.
    ///
    /// SL material properties:
    ///   Color4    = RGBA tint (premultiplied in alpha)
    ///   Fullbright = unlit (emission = diffuse colour)
    ///   Glow       = emission intensity
    ///   Shiny      = specular level (None/Low/Medium/High)
    ///   Bump       = normal map bake type
    ///   OffsetU/V, RepeatU/V, Rotation = UV transform
    ///   MediaFlags  = in-world media surface
    /// </summary>
    public static class MaterialConverter
    {
        private static readonly int _BaseMap      = Shader.PropertyToID("_BaseMap");
        private static readonly int _BaseColor    = Shader.PropertyToID("_BaseColor");
        private static readonly int _EmissionMap  = Shader.PropertyToID("_EmissionMap");
        private static readonly int _EmissionColor= Shader.PropertyToID("_EmissionColor");
        private static readonly int _Smoothness   = Shader.PropertyToID("_Smoothness");
        private static readonly int _Metallic     = Shader.PropertyToID("_Metallic");
        private static readonly int _BumpMap      = Shader.PropertyToID("_BumpMap");
        private static readonly int _Cull         = Shader.PropertyToID("_Cull");

        // Cached shader reference (URP Lit)
        private static Shader _litShader;
        private static Shader _unlitShader;

        private static Shader LitShader   => _litShader   ??= Shader.Find("Universal Render Pipeline/Lit");
        private static Shader UnlitShader => _unlitShader ??= Shader.Find("Universal Render Pipeline/Unlit");

        public static Material FromFace(Primitive.TextureEntryFace face, Texture2D texture)
        {
            bool fullbright = (face.Fullbright);
            var mat = new Material(fullbright ? UnlitShader : LitShader);

            // Texture
            if (texture != null)
                mat.SetTexture(_BaseMap, texture);

            // Tint colour
            var c = face.RGBA;
            mat.SetColor(_BaseColor, new Color(c.R, c.G, c.B, c.A));

            // UV transform: SL uses per-face repeats, offsets, and rotation
            SetUVTransform(mat, face);

            // Transparency
            if (c.A < 1f || face.Alpha < 1f)
                SetTransparent(mat, c.A * face.Alpha);

            // Glow / emission
            if (face.Glow > 0f)
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor(_EmissionColor, new Color(c.R, c.G, c.B) * face.Glow * 4f);
            }

            // Specularity → Smoothness
            float smoothness = face.Shiny switch
            {
                Shininess.Low    => 0.25f,
                Shininess.Medium => 0.5f,
                Shininess.High   => 0.85f,
                _                => 0f
            };
            mat.SetFloat(_Smoothness, smoothness);
            mat.SetFloat(_Metallic, 0f);

            return mat;
        }

        public static Material DefaultMaterial()
        {
            var mat = new Material(LitShader);
            mat.SetColor(_BaseColor, Color.white);
            return mat;
        }

        private static void SetUVTransform(Material mat, Primitive.TextureEntryFace face)
        {
            // Unity tiling/offset maps directly to SL's RepeatU/V and OffsetU/V
            float repeatU = face.RepeatU == 0f ? 1f : face.RepeatU;
            float repeatV = face.RepeatV == 0f ? 1f : face.RepeatV;

            mat.SetTextureScale(_BaseMap, new Vector2(repeatU, repeatV));
            mat.SetTextureOffset(_BaseMap, new Vector2(face.OffsetU, face.OffsetV));

            // SL texture rotation is per-face but Unity's material doesn't directly
            // support per-material UV rotation without a custom shader property.
            // We bake it into a UV rotation keyword here for the SLSurface shader.
            if (Mathf.Abs(face.Rotation) > 0.001f)
            {
                mat.SetFloat("_UVRotation", face.Rotation);
                mat.EnableKeyword("UV_ROTATION");
            }
        }

        private static void SetTransparent(Material mat, float alpha)
        {
            mat.SetFloat("_Surface", 1);    // Transparent
            mat.SetFloat("_Blend",   0);    // Alpha
            mat.SetFloat(_Cull,      2);    // Back
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            var col = mat.GetColor(_BaseColor);
            col.a = alpha;
            mat.SetColor(_BaseColor, col);
        }
    }
}
