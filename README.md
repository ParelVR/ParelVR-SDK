# ParelVR SDK

The SDK for making ParelVR content in Unity: avatars, worlds, and world scripts written in VoltCS.

## Requirements

Unity 6 (6000.0 or newer).

## Install

**From a tarball (recommended, no Git needed)**: download `com.parelvrsdk.pvr-<version>.tgz` from the releases,
then Window > Package Manager > + > Install package from tarball, and pick the file. Keep the file where it is:
the project points at it.

**From Git** (Window > Package Manager > + > Install package from git URL):

```
https://github.com/ParelVR/ParelVR-SDK.git
```

This needs Git installed (https://git-scm.com/download/win). If Unity says *No 'git' executable was found*,
install Git, then close and reopen Unity Hub and Unity so they see it, or use the tarball above instead.

**From a .unitypackage**: download `ParelVR-SDK-<version>.unitypackage` from the releases and use
Assets > Import Package > Custom Package.

## What is inside

| Folder | Contents |
|---|---|
| `Runtime/` | Components you put on avatars and worlds |
| `Editor/` | The ParelVR control panel: sign in, build, upload |
| `Volt/` | VoltCS tooling and the in-editor simulator |
| `Assemblies/` | The Volt runtime and the ParelVR scripting API |
| `API/` | The API registry this SDK was built with |
| `Compiler/` | The VoltCS compiler |
| `Documentation~/` | Guides and the API reference |
| `Samples~/` | An example world and starter scripts |

## World scripts in one minute

1. **ParelVR SDK > Volt > Create Volt Script** makes a script that derives from `VoltBehaviour`.
2. Put it on an object in your world scene and press Play: the script runs in the Volt simulator.
3. **ParelVR SDK > Volt > Build & Upload** compiles the scripts, builds the world and uploads both.

Start with `Documentation~/index.md`.
