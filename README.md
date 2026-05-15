# SLQuest — Second Life Native Client for Meta Quest 3

A full-featured Second Life viewer built in Unity 6 targeting the Meta Quest 3.

## Architecture

```
SLQuest/
├── Assets/Scripts/
│   ├── Core/           — Application root, event bus, thread dispatcher
│   ├── Network/        — GridClient wrapper, login, HTTP capabilities
│   ├── World/          — Region, objects, terrain, prim mesh builder
│   ├── Avatar/         — Local and remote avatars, appearance/baking
│   ├── Assets/         — Texture (J2K) and LLMesh loading + disk cache
│   ├── Chat/           — Local chat, IMs, script dialogs
│   ├── Inventory/      — Tree browser, wear/rez/drop
│   ├── Building/       — Prim create/edit/link, property editor
│   ├── Scripting/      — LSLBridge (viewer-side script event handling)
│   ├── VR/             — VRRig, locomotion, hand controllers, teleport arc
│   ├── UI/             — World-space panels (login, chat, inventory, build, map)
│   ├── Voice/          — Vivox positional voice
│   └── Rendering/      — MaterialConverter, SLSurface shader
└── Assets/Shaders/
    └── SLSurface.shader — URP Lit + UV-rotation + Fullbright
```

### Key design decisions

| Decision | Why |
|---|---|
| libopenmetaverse (C#) | Complete SL protocol implementation — UDP, HTTP caps, asset pipeline |
| Unity 6 + URP | Meta XR SDK requires Unity; URP hits Quest 3 perf targets |
| MainThreadDispatcher | libopenmetaverse fires all callbacks on background threads; Unity API is main-thread only |
| World-space UI (Canvas) | Grabbable, repositionable panels suit VR better than head-locked HUDs |
| HTTP caps over UDP | GetTexture/GetMesh2 caps are faster and more reliable than UDP asset transfers |

## Prerequisites

### Development machine
- Unity 6 (6000.0.x LTS) — install via Unity Hub
- Android Build Support module + Android SDK/NDK (for APK building)
- Meta XR SDK — installed automatically via `Packages/manifest.json`
- NuGetForUnity — installs libopenmetaverse from NuGet

### Quest 3
- Developer Mode enabled (Meta Developer account required)
- Sidequest or `adb` for sideloading during development

## First-time setup

### 1. Open the project in Unity
```bash
# Unity Hub → Open → select this directory
# Accept any Package Manager prompts
```

### 2. Install libopenmetaverse via NuGetForUnity
```
Unity menu → NuGet → Manage NuGet Packages → search "OpenMetaverseNet" → Install
```

### 3. Configure Meta XR SDK
```
Unity menu → Meta → Tools → Project Setup Tool → Fix All
```

This sets:
- XR Plugin Management → OpenXR → Meta Quest 3 feature set
- Android → Minimum API Level 32
- Stereo Rendering Mode → Multiview
- Fixed Foveated Rendering → enabled

### 4. Register your app with Meta
1. Go to developer.oculus.com → Create App
2. Copy the App ID into `Assets/Plugins/Oculus/OculusProjectConfig.asset`

### 5. Configure Android Build Settings
```
File → Build Settings → Android
- Texture Compression: ASTC
- Scripting Backend: IL2CPP
- Target Architecture: ARM64
```

### 6. Build & deploy
```
File → Build Settings → Build And Run
```
Or via CLI:
```bash
/path/to/Unity -batchmode -projectPath . -buildTarget Android \
  -executeMethod BuildScript.BuildAPK -quit
```

## Scene setup

The bootstrap scene needs one root GameObject with:
- `SLApplication` (with all subsystem references assigned)
- `OVRManager` (Meta XR)
- `OVRCameraRig` → `VRRig`
- `MainThreadDispatcher`
- `CapabilityHandler`
- `LSLBridge`

All subsystem MonoBehaviours are children of SLApplication.

## Protocol notes

### Login
SLQuest identifies itself honestly per Linden Lab's Third-Party Viewer Policy:
```
Channel: "SLQuest Beta"
Version: "0.1.0"
```
Using a false viewer identity violates the TPV policy.

### Coordinate system
Second Life uses a left-handed system with Z-up.  
Unity uses a left-handed system with Y-up.  
All conversions: `unityX = slX`, `unityY = slZ`, `unityZ = slY`.

### Asset pipeline
1. Check disk cache (`Application.persistentDataPath/SLCache/`)
2. Fetch via `GetTexture` / `GetMesh2` HTTP capability
3. Fall back to UDP asset pipeline (libopenmetaverse)

Textures are JPEG2000 (`.j2c`) decoded via OpenJPEG bundled in libopenmetaverse.  
Meshes are LLMesh binary format decoded in `MeshDecoder.cs`.

## Vivox voice
Second Life's voice service uses Vivox with per-region positional channels.  
Credentials are provisioned by the SL grid after login — no separate Vivox account needed.  
Unity Vivox SDK (`com.unity.vivox`) is declared in `Packages/manifest.json`.

## Feature status

| Feature | Status |
|---|---|
| Login (Agni / Aditi / custom grid) | ✅ |
| Region entry + sim traversal | ✅ |
| Prim object rendering | ✅ |
| Mesh object rendering (LLMesh) | ✅ |
| Terrain (heightmap) | ✅ |
| Avatar movement + AgentUpdate | ✅ |
| Remote avatar rendering | ✅ |
| Local chat + IMs | ✅ |
| Inventory browser | ✅ |
| Prim building (create/move/rotate/scale/link) | ✅ |
| Texture loading (J2K via caps + UDP) | ✅ |
| Avatar appearance / baked textures | ✅ Fetches server bakes; client baking TODO |
| Voice (Vivox positional) | ✅ |
| Script dialogs / permissions | ✅ |
| Gesture playback | 🔲 TODO |
| In-world media surfaces | 🔲 TODO |
| Marketplace browser | 🔲 TODO |
| Group chat | 🔲 TODO |
| Estate tools | 🔲 TODO |
| PBSM (Physically Based materials via RenderMaterials cap) | 🔲 TODO |
| Client-side avatar baking | 🔲 TODO |

## License
This project is not affiliated with or endorsed by Linden Research, Inc.  
Second Life® is a registered trademark of Linden Research, Inc.  
libopenmetaverse is BSD-licensed.  
Unity and the Meta XR SDK are subject to their respective licenses.
