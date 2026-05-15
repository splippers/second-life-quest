namespace SLQuest.Core
{
    public static class SLConstants
    {
        // Region/sim dimensions
        public const float REGION_SIZE = 256f;
        public const float REGION_HEIGHT = 4096f;
        public const int TERRAIN_PATCH_SIZE = 16;
        public const int TERRAIN_PATCHES_PER_EDGE = 16; // 256 / 16
        public const float METERS_TO_UNITY = 1f;  // SL uses metres; Unity matches

        // Object limits
        public const int MAX_PRIMS_PER_LINKSET = 512;
        public const float MIN_PRIM_SIZE = 0.01f;
        public const float MAX_PRIM_SIZE = 64f;

        // Avatar
        public const float AVATAR_HEIGHT_DEFAULT = 1.8f;
        public const float WALK_SPEED = 3.0f;
        public const float RUN_SPEED = 6.0f;
        public const float FLY_SPEED = 7.0f;

        // Network
        public const int DEFAULT_THROTTLE_TOTAL = 1_500_000; // bits/sec
        public const int ASSET_CACHE_MB = 512;
        public const int TEXTURE_CACHE_MB = 256;

        // SL grid URIs
        public const string AGNI_LOGIN_URI  = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";
        public const string ADITI_LOGIN_URI = "https://login.aditi.lindenlab.com/cgi-bin/login.cgi";

        // Viewer identification (Linden policy requires honest identification)
        public const string VIEWER_NAME    = "SLQuest";
        public const string VIEWER_VERSION = "0.1.0";
        public const string VIEWER_CHANNEL = "SLQuest Beta";

        // VR
        public const float VR_POINTER_DISTANCE = 5f;
        public const float VR_GRAB_DISTANCE    = 0.3f;
    }
}
