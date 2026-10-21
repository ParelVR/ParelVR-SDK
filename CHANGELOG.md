# Changelog

## 1.5.2 (2026-10-21)

- Remove SDK removes all of it. It also takes the SDK out of Packages/manifest.json, so Package Manager no longer
  downloads it again when it was installed from Git and then updated. Files Unity still has open (its DLLs) no
  longer stop the rest from being deleted, and are deleted as soon as Unity closes. It also clears the updater's
  ProjectSettings/ParelSdkInstall.json and Temp/ParelSdkUpdate.

## 1.5.1 (2026-10-21)

- Play mode takes the mouse when the simulator starts, so you look around by moving it. Esc frees the mouse; a click
  on the Game view takes it again.
- Play mode puts you at the world's ParelVR Spawn Point (the enabled one with the highest priority, facing as set),
  as the client does, instead of only at an object named Spawn.

## 1.5.0 (2026-10-21)

Volt API 1.2. Worlds built with this SDK need the ParelVR client update that brings API 1.2; older clients refuse them
(VLT1003).

### New in VoltCS

- VoltRandom: random numbers that are the same on every client for a seed and an index, plus VoltRandom.Hash.
- VoltEvents.Broadcast: calls a method by name on every script of the world.
- VoltRuntime.Language: the player's two-letter language code.
- VoltPlayer: GetTag/SetTag, GetFingerCurl, GetGesture, GetAvatarParameter, SetAvatarParameter, SetVoiceGain,
  SetVoiceDistance and ResetVoice.
- OnHoverEnter and OnHoverExit events for objects the player points at.
- VoltDialogs: notifications and yes/no questions on the player's HUD, answered through OnDialogResult.
- VoltObjectPool: scene objects the master spawns and returns for everyone, late joiners included.
- VoltNetworking.InterestDistance: synced objects and scripts far from every other player send less often.
- VoltInstance.Kick, Ban, Unban, IsBanned and BannedNames (master only).
- VoltLeaderboard and VoltAchievements, kept in world and player storage.

### Editor

- Hot reload: saving a script in play mode compiles it and swaps it into the running world, keeping field values
  (Volt Settings, on by default; ParelVR SDK > Volt > Hot Reload, Ctrl+Alt+R).
- Volt Graph: a node editor for world scripts (Create > ParelVR > Volt Graph); Generate writes a VoltCS script.
- Network Cost window: bytes per update and per second for every script's synced fields.
- Test in VR (ParelVR SDK > Volt > Test in VR): play mode uses a headset through XR Plug-in Management. Grip grabs,
  trigger uses, left stick moves, right stick turns.
- The ParelVR SDK panel is an ordinary window now and docks next to the Scene and Game views.

### Fixes

- Play mode: objects no longer vanish and reappear as you look around. The simulator camera ignored a stale occlusion
  bake the way the client already does.
- Play mode: pickups and interactables can be clicked through trigger zones; the mouse wheel pushes and pulls a held
  object.
- Play mode no longer stalls on big scenes while the scene is prepared, a compiler that hangs is stopped after two
  minutes, and a simulator that fails to start leaves the world running without scripts instead of taking the editor
  down.

## 1.4.0 (2026-10-20)

### Quest worlds

- Selected Platform in the Build bar is now a dropdown: pick Windows or Android to switch the project's build
  target from the SDK window. If that platform's build support isn't installed, it says how to add it in Unity Hub.
- While Android is the build target, the world validations include a Quest Compatibility check. Missing shaders,
  shaders with compile errors, geometry shaders, tessellation and Android Graphics APIs without Vulkan and
  OpenGLES3 have to be fixed before building.
- It also warns about GrabPass shaders, texture compression other than ASTC, textures over 2048px, point and spot
  lights with realtime shadows, Realtime reflection probes and missing scripts.
- Every Quest issue has an Auto Fix, and Auto Fix All applies them all at once. Shader fixes switch materials to
  Simple Lit (URP) or Legacy Diffuse, which also changes how they look on Windows; texture fixes cap the size for
  Android only.
- Warnings are now listed under Validations without stopping the build.

## 1.3.0 (2026-10-20)

### World Roles and Permissions

- **ParelVR SDK > World Roles and Permissions**: add co-owners and moderators to your uploaded worlds and choose what each role can do from the world's Settings page on the ParelVR website (edit world settings, ban players, ban groups from hosting, shut down instances, send announcements).
- Owners, co-owners and moderators are protected from instance-master kicks, mutes and warnings.

## 1.2.0 (2026-10-19)

### Check for Updates

- Settings > SDK Updates and ParelVR SDK > Check for Updates open a new updates pane. It checks GitHub
  for a newer SDK, shows the installed and new versions with what changed, and installs it on request:
  through the Package Manager for a Git install, or by downloading it (no Git needed) for any other install.
  When everything is current it says so.

### Building and publishing

- Fixed "An abnormal situation has occurred: the PlayerLoop internal function has been called recursively"
  when building a world or an avatar (Build & Publish, Build & Test, Quest builds): bundles are now built from
  the editor's update instead of an await continuation.
- Textures a world or avatar uses have Mip Streaming turned off at build time, so uploads no longer load
  blurry (Settings > Preferences can switch this off).
- Poiyomi shader locking before an avatar build can be switched off in Settings > Preferences.

### Install

- The SDK can be installed without Git: Package Manager > + > Install package from tarball, using the
  `.tgz` from the releases. Installing from the Git URL needs Git; the README says what to do when Unity
  reports "No 'git' executable was found".

### Motion Bones

- Smoother at any frame rate: frames at 90 Hz or faster now take one step of their own length (at
  120 / 144 Hz no frame is skipped, so chains no longer stutter), slower frames are split into up to four
  steps, and the chain root eases to its new position across those steps. Momentum keeps the same decay
  per second at every step length, so existing settings behave as before at 90 Hz.
- Snap turns carry the chain round with the avatar instead of whipping it. The teleport distance that
  resets a chain now grows with the avatar's scale.
- Robustness: a destroyed bone (outfit toggles, avatar edits) makes the chain re-read its hierarchy
  instead of throwing; a NaN setting or degenerate collider resets the chain to its animated pose instead
  of corrupting the transforms; an error in one Motion Bone no longer stops the others (it is logged,
  rebuilt, and stopped after three failures in a row).
- Hand / finger colliders registered as global keep that status when disabled and re-enabled.
- Performance: colliders are filtered (active, valid, permission, nearby) once per Motion Bone per frame
  instead of per bone per step; owner lookups are cached. Motion Bones over the
  `ParelMotionBoneSystem.MaxSimulatedParticles` budget now rest in their original pose and restart cleanly
  instead of freezing mid-motion, and smaller chains later in the list still fit in the remaining budget.
- New, optional: `ParelMotionBoneSystem.CullDistance` / `Viewer` pause far-away Motion Bones (off by
  default); `ParelMotionBone.ResetSimulation()` for scripts that teleport an avatar; `ExternalForce` (in
  Gravity units, not saved, not networked) as a hook for wind or interactions; `IsSuspended` and
  per-frame counters on `ParelMotionBoneSystem` for debugging.
- Presets: the Motion Bone inspector has **Apply Preset...** (Hair, Long Hair, Tail, Ears, Clothing,
  Accessory, Soft Body), also available as `ParelMotionBone.ApplyPreset`. A preset fills Forces, Limits and
  Stretch & Squish and clears their curves; it never touches transforms, radius, colliders, permissions or
  the parameter. Select several Motion Bones to apply one preset to all of them.
- Different behaviour per state (idle, walking, running, falling): animate the Motion Bone's force
  settings from the avatar's own Animator Controller. They are read every frame, so Animator transitions
  blend between settings smoothly.
- Set-up checks, in the inspector and the avatar Builder: Root Transform outside the avatar, chains that
  move humanoid bones (which fight animation and IK), two Motion Bones moving the same bones, empty
  Collider slots, and colliders without a radius. Tooltips on every setting, and a Runtime section in
  Play Mode (state, bone count, totals, Reset Simulation, Clear Pose).
- Migration: none. No serialized field was renamed or removed, so existing Motion Bones load unchanged.
  Above 90 Hz the simulation now runs once per frame, so it costs a little more CPU than before.
- Still missing: self-collision, collision layers / groups beyond Allow Collision, and shared profile
  assets linked across chains.

## 1.1.0

- `VoltPlayer.Fps`: frames per second a player's client is drawing, as an int.
- `VoltPlayer.Ping`: a player's round trip time to the instance server in milliseconds, as an int.
- API 1.1, bytecode 1.0. Worlds built with this SDK need a ParelVR client that provides API 1.1.

## 1.0.0

- The SDK is one package, `com.parelvrsdk.pvr`: the Creator SDK, the avatar and world components and Volt.
- Volt world scripting: the VoltCS compiler, the Volt virtual machine, the ParelVR scripting API, the editor
  simulator and the tools under **ParelVR > Volt**.
- API 1.0, bytecode 1.0.
