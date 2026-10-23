# Changelog

## 1.8.1 (2026-10-23)

### Portals in worlds

- **Local Portal In / Local Portal Out**: walking through a Local Portal In takes the player to its Local Portal Out in
  the same world, keeping where they stand in the doorway and the way they walk. Both draw a doorway outline, a ring
  at the foot and a facing line in the Scene view, like the Spawn Point. Several Ins may share one Out; "Two Way"
  lets an In be walked into from either side.
- **World Portal**: enter a World ID; walking through takes the player to that world. "Show Doorway" draws the
  ParelVR portal on all three (off: use your own models).

### Motion Bones

- Walking, flying and smooth turning no longer leave bones trailing far behind (moving left left them stuck out to
  the right): the avatar's own movement is mostly carried through, so only starting, stopping and sharp moves swing
  them, the way creators expect.

## 1.8.0 (2026-10-21)

### Platforms: build once, ParelVR makes the standalone version

- The world and avatar Builders have a Platforms section: PC / PCVR and Meta / Standalone VR. You build on PC only;
  with Meta / Standalone VR on, each publish also sends ParelVR what the PC bundle was built from (the scene or avatar
  and the assets it uses, never scripts), and ParelVR's servers make the standalone build from it: shaders moved to
  mobile ones, textures compressed and sized for a headset, sound compressed, realtime shadows, realtime probes and
  post-processing turned off, particles capped, and on avatars no lights, cloth or cameras. It usually takes a few
  minutes; the Builder and the Content Manager show where it is (queued, converting, ready or why it failed).
- Removed: the platform dropdown on the world Builder (which switched the project to Android) and "Also publish an
  Android (Quest) build" on the avatar Builder. The Quest Compatibility checks and their auto-fixes are gone too; they
  changed your PC materials, and the server now does that work on its own copy.
- A project still set to Android is offered the switch back to Windows when you publish a world.
- Turning a platform off stops offering the world or avatar there; turning it back on brings the last build back
  until the next one is ready.
- New mobile shaders in Runtime/Shaders/Mobile: ParelVR/Mobile/Toon Lit, Standard Lite, Unlit and Particles.

## 1.7.0 (2026-10-21)

Volt API 1.4. Worlds built with this SDK need the ParelVR client update that brings API 1.4.

### Multi-client play mode

- ParelVR SDK > Volt > Multi-Client Play Mode: every window of the project in play mode is another player of the same
  instance, each with its own Game view, its own copy of the world and its own network session, so synced fields,
  ownership, network events and master hand-over can be tested in the editor. Add players with Unity's Multiplayer
  Play Mode (ParelVR SDK > Volt > Add Players); a second editor on the same project works too.
- Other players show as the loading avatar with their nameplate, posed from their head and hands, and world triggers,
  raycasts and OverlapSpherePlayers find them as players.
- Volt Settings: port, an added delay for every message and a share of unreliable messages to drop, to see how scripts
  cope with a poor connection. The top of the Game view shows who you are, who is master and how many are in.

### Render textures (Volt API 1.4)

- VoltRenderTexture.Create(width, height) makes a render texture (16 to 2048 px a side, 16 per world, 16 million
  pixels in all), freed with the world or with Release(); Clear(color) fills it. Render textures that are part of the
  world can be assigned in the inspector as VoltTexture / VoltRenderTexture fields.
- VoltCamera.TargetTexture sets what a world camera draws into; VoltCamera.Render() draws one picture on request.
- VoltRenderer.SetTexture(property, texture) and SetTexture(texture) put a texture on one renderer (screens, monitors,
  portals); GetTexture reads it. VoltImage.Texture shows one on a raw image.

### Avatar simulator

- ParelVR SDK > Avatar Simulator > Test Avatar in Home Hub (or Test in Simulator on the Builder tab): play mode puts
  you in the ParelVR Home Hub, fetched from the backend with its scripts, wearing the selected avatar. The world is
  cached and only downloaded again when the backend has a newer build. Without a connection: a plain floor.
- The avatar runs as in ParelVR: Base layer, Additive, Gesture, Action, FX and Sitting layers, Expression Parameters
  (saved ones are remembered), built-in parameters (velocity, grounded, seated, gestures, VR mode ...), Parameter
  Driver, Locomotion Control and layer controls, Motion Bones and contacts.
- R opens the radial menu with the avatar's Expressions Menu: toggles, buttons, sub menus and the radial, two-axis and
  four-axis puppets. Left / Right Shift + F1-F8 set hand gestures. In VR the left menu button opens it.
- V switches to third person; your head shows in mirrors and third person and hides in your own view.
- T brings out a test hand: someone else's hand that follows the mouse, touches your receivers, bumps Motion Bones and
  grabs them while the mouse button is held.
- Humanoid avatars without a Base layer get a simple idle and walk instead of their import pose.
- World scripts can read the avatar's parameters and fingers (VoltPlayer avatar members).

### Contacts

- Box shape, besides sphere and capsule.
- One receiver, many parameters: besides its main parameter, a receiver can set Touching, Entered, Exited, Proximity,
  Count (how many touch it), Taps (a counter that goes up every touch), Speed, Direction X / Y / Z (where it was
  touched from) and From Self / From Others. A headpat or a boop is one component.
- Pulses are held (Hold, default 0.1 s) so animators and other players never miss them; Smoothing glides proximity,
  speed and direction; Min touch speed applies to taps too; Value sets what Constant and On Enter write.
- Haptics: the controller of the wearer's hand that touches a receiver vibrates.
- Active When: a contact exists only while an avatar parameter is on, instead of animating the component.
- From: react to other avatars, the world's own senders, or both. Box receivers can measure proximity from the front.
- In worlds, a receiver calls On Contact Enter / On Contact Exit, wired to a Volt script's method like a UI button.
- The inspector: presets (Headpat, Boop, Hug, High Five, Stomp, Hand Touch) that place the receiver on the right
  bone, body-part buttons instead of typing tags, "+ Also set" for outputs, a button that adds the parameters to the FX
  controller, a radius handle in the Scene view and live values in play mode.
- Validation: parameters no playable layer has, tags past 16, receivers nothing can touch, events on avatars (ignored).
  Local Only contacts no longer count toward the performance rank.

## 1.6.0 (2026-10-21)

Volt API 1.3. Worlds built with this SDK need the ParelVR client update that brings API 1.3.

### Mirrors

- ParelVR Mirror (GameObject > ParelVR > Mirror, or add the component to a quad): a mirror that renders itself, one
  picture per eye in VR, in ParelVR and in play mode alike. Layers, resolution, anti-aliasing, distance and tint are
  set on the component.
- Custom mirrors: give the component your own material. Its shader receives the reflection as _ReflectionTexLeft and
  _ReflectionTexRight (see the SDK Mirror shader for how to sample them), so frames, tints and effects are yours.
  Mirror scripts of your own do not run in uploaded worlds; this is the way to make one.

### Nameplate badges

- VoltPlayer.SetBadge(object) keeps a world object above that player's nameplate, facing the viewer;
  VoltPlayer.ClearBadge and VoltPlayer.NameplatePosition go with it.

### Play mode

- You have a body: ParelVR's loading avatar stands in for yours (body, head, hands that follow the view or the
  controllers) with your nameplate above it and its loading bar, so mirrors and badges can be tried.
- World UI works: the crosshair (or a VR hand) hovers, clicks, drags sliders and scrolls on world-space canvases. The
  scene's own input modules are switched off while it runs, so nothing clicks twice.
- Typing: selecting a text field (legacy or TextMeshPro) opens an on-screen keyboard in front of you, usable in VR
  and with the crosshair; the physical keyboard types too, and walking keys do not move you while you type. Esc stops
  typing.
- Video players work: YouTube links play as in ParelVR, a scene with a Volt Video Player turns the Video capability on
  and allows the domain of its start address, and a scene with Volt components but no scripts (video players,
  pickups, seats) now gets the world runtime it needs, in play mode and in the uploaded world.

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
