using OpenMetaverse;

namespace SLQuest.Rendering
{
    /// <summary>
    /// Second Life Physically-Based Surface Material (PBSM) data fetched
    /// from the RenderMaterials capability.
    /// </summary>
    public readonly struct PBSMaterial
    {
        // Normal map
        public readonly UUID  NormMapId;
        public readonly float NormRepeatU;
        public readonly float NormRepeatV;
        public readonly float NormOffsetU;
        public readonly float NormOffsetV;
        public readonly float NormRotation;

        // Specular map / tint
        public readonly UUID   SpecMapId;
        public readonly Color4 SpecColor;
        public readonly float  SpecExponent;   // 0-1 (was 0-255 in wire format)
        public readonly float  EnvIntensity;   // 0-1

        // Alpha
        public readonly DiffuseAlphaMode AlphaMode;
        public readonly float            AlphaCutoff; // 0-1

        public PBSMaterial(
            UUID normMapId,   float normRepU, float normRepV,
            float normOffU,   float normOffV, float normRot,
            UUID specMapId,   Color4 specColor, float specExp, float envInt,
            DiffuseAlphaMode alphaMode, float alphaCutoff)
        {
            NormMapId    = normMapId;
            NormRepeatU  = normRepU;
            NormRepeatV  = normRepV;
            NormOffsetU  = normOffU;
            NormOffsetV  = normOffV;
            NormRotation = normRot;
            SpecMapId    = specMapId;
            SpecColor    = specColor;
            SpecExponent = specExp;
            EnvIntensity = envInt;
            AlphaMode    = alphaMode;
            AlphaCutoff  = alphaCutoff;
        }

        public static PBSMaterial FromOSD(OpenMetaverse.StructuredData.OSDMap map)
        {
            const float inv8  = 1f / 255f;
            const float inv16 = 1f / 16384f; // SL stores UV as fixed-point × 16384

            UUID normId = map.ContainsKey("NormMap")
                ? map["NormMap"].AsUUID() : UUID.Zero;

            float normRepU = map.ContainsKey("NormRepeatX") ? map["NormRepeatX"].AsInteger() * inv16 : 1f;
            float normRepV = map.ContainsKey("NormRepeatY") ? map["NormRepeatY"].AsInteger() * inv16 : 1f;
            float normOffU = map.ContainsKey("NormOffsetX") ? map["NormOffsetX"].AsInteger() * inv16 : 0f;
            float normOffV = map.ContainsKey("NormOffsetY") ? map["NormOffsetY"].AsInteger() * inv16 : 0f;
            float normRot  = map.ContainsKey("NormRotation") ? map["NormRotation"].AsInteger() * inv16 : 0f;

            UUID specId = map.ContainsKey("SpecMap")
                ? map["SpecMap"].AsUUID() : UUID.Zero;

            Color4 specColor = Color4.White;
            if (map.ContainsKey("SpecColor") && map["SpecColor"] is OpenMetaverse.StructuredData.OSDBinary specBin)
            {
                byte[] b = specBin.AsBinary();
                if (b.Length >= 4)
                    specColor = new Color4(b[0] * inv8, b[1] * inv8, b[2] * inv8, b[3] * inv8);
            }

            float specExp = map.ContainsKey("SpecExp") ? map["SpecExp"].AsInteger() * inv8 : 0.51f;
            float envInt  = map.ContainsKey("EnvIntensity") ? map["EnvIntensity"].AsInteger() * inv8 : 0f;

            byte alphaModeByte = map.ContainsKey("DiffuseAlphaMode") ? (byte)map["DiffuseAlphaMode"].AsInteger() : (byte)0;
            float alphaCut = map.ContainsKey("AlphaMaskCutoff") ? map["AlphaMaskCutoff"].AsInteger() * inv8 : 0f;

            return new PBSMaterial(
                normId, normRepU, normRepV, normOffU, normOffV, normRot,
                specId, specColor, specExp, envInt,
                (DiffuseAlphaMode)alphaModeByte, alphaCut);
        }
    }

    public enum DiffuseAlphaMode : byte
    {
        None    = 0,
        Blend   = 1,
        Mask    = 2,
        Emissive = 3
    }
}
