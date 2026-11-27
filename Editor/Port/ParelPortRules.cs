using System;
using System.Collections.Generic;
using ParelVR.AvatarSDK;
using ParelVR.SDK.Worlds.Components;
using ParelVR.Volt.Interaction;
using ParelVR.Volt.Networking;
using ParelVR.Volt.Video;
using ParelVR.WorldSDK;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;

namespace ParelVR.SDK.Port
{
    internal enum PortKind { Component, Behaviour, Asset }

    /// <summary>
    /// How one kind of component from another platform becomes ParelVR: which types it is (by name, so the other
    /// SDK need not be installed), how to recognise its saved settings when its script is missing, what replaces it
    /// and how its settings carry over. Without a target it is only removed.
    /// </summary>
    internal sealed class PortRule
    {
        public string Name;
        public PortKind Kind;
        public int Order;                                         // lower first: colliders before the bones that use them
        public string[] Types = Array.Empty<string>();             // type names while the other SDK is installed
        public string[] Fingerprint = Array.Empty<string>();       // fields its saved settings always have
        public string[] Without = Array.Empty<string>();           // fields they never have (to tell look-alikes apart)
        public Func<PortItem, Type> Target;                        // null: removed, nothing replaces it
        public Func<PortJob, Component> Custom;                    // makes the replacement itself
        public Action<PortJob> After;                              // wiring that is not a straight copy
        public string Note;
        public readonly Dictionary<string, string[]> Aliases = new Dictionary<string, string[]>();
        public readonly HashSet<string> Skip = new HashSet<string>();
        public readonly Dictionary<string, string[]> EnumValues = new Dictionary<string, string[]>();
        public readonly Dictionary<string, string> EnumNames = new Dictionary<string, string>();

        public PortRule Live(params string[] types) { Types = types; return this; }
        public PortRule Print(params string[] fields) { Fingerprint = fields; return this; }
        public PortRule Not(params string[] fields) { Without = fields; return this; }
        public PortRule To<T>() where T : UnityEngine.Object { Target = _ => typeof(T); return this; }
        public PortRule Alias(string target, params string[] sources) { Aliases[PortValue.Norm(target)] = sources; return this; }
        public PortRule Ignore(params string[] targets) { foreach (string t in targets) Skip.Add(PortValue.Norm(t)); return this; }
        public PortRule Values(string target, params string[] namesByValue) { EnumValues[PortValue.Norm(target)] = namesByValue; return this; }
        public PortRule Rename(string from, string to) { EnumNames[PortValue.Norm(from)] = to; return this; }
        public PortRule Then(Action<PortJob> after) { After = after; return this; }

        public bool MatchesType(Type type)
        {
            for (Type t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                if (Array.IndexOf(Types, t.Name) >= 0) return true;
            return false;
        }

        public bool MatchesFields(PortValue v)
        {
            if (Fingerprint.Length == 0 || v == null || v.K != PortValue.Kind.Map) return false;
            foreach (string f in Fingerprint) if (!v.Has(f)) return false;
            foreach (string f in Without) if (v.Has(f)) return false;
            return true;
        }
    }

    internal static class PortRules
    {
        private static PortRule R(string name, int order = 10, PortKind kind = PortKind.Component) => new PortRule { Name = name, Order = order, Kind = kind };

        // The numbering other SDKs save their choices with, where it differs from ParelVR's.
        private static readonly string[] LipSync = { "Default", "JawFlapBone", "JawFlapBlendShape", "VisemeBlendShape", "VisemeParameterOnly" };
        private static readonly string[] LayerTypes = { "Base", "Deprecated0", "Additive", "Gesture", "Action", "FX", "Sitting", "TPose", "IKPose" };
        private static readonly string[] Mobility = { "Mobile", "Immobilize", "ImmobilizeForVehicle" };

        public static readonly PortRule[] All = Build();

        private static PortRule[] Build()
        {
            var list = new List<PortRule>
            {
                // ── avatars ──────────────────────────────────────────────────
                R("Avatar descriptor", 0).Live("VRCAvatarDescriptor", "VRC_AvatarDescriptor").Print("ViewPosition", "VisemeSkinnedMesh", "lipSync")
                    .To<ParelAvatarDescriptor>()
                    .Alias("viewPoint", "ViewPosition").Alias("jawBone", "lipSyncJawBone")
                    .Alias("jawClosedRotation", "lipSyncJawClosed").Alias("jawOpenRotation", "lipSyncJawOpen")
                    .Alias("jawFlapBlendShape", "MouthOpenBlendShapeName")
                    .Alias("useAutoFootstep", "autoFootsteps").Alias("forceLocomotionAnimations", "autoLocomotion")
                    .Ignore("unityVersion", "layoutUpgraded", "viewPointInitialized", "avatarAnimator")
                    .Values("lipSync", LipSync).Values("type", LayerTypes)
                    .Then(AvatarDescriptorDone),
                R("Avatar descriptor", 0).Live("CVRAvatar").Print("viewPosition", "voicePosition", "bodyMesh")
                    .To<ParelAvatarDescriptor>()
                    .Alias("viewPoint", "viewPosition").Alias("visemeSkinnedMesh", "bodyMesh").Alias("visemeBlendShapes", "visemeBlendshapes")
                    .Alias("enableEyeLook", "useEyeMovement").Alias("lipSync", "visemeMode")
                    .Ignore("unityVersion", "layoutUpgraded", "viewPointInitialized", "avatarAnimator", "blinkBlendShape")
                    .Values("lipSync", "VisemeBlendShape", "JawFlapBlendShape", "JawFlapBone")
                    .Rename("Visemes", "VisemeBlendShape").Rename("SingleBlendshape", "JawFlapBlendShape").Rename("JawBone", "JawFlapBone")
                    .Then(CvrAvatarDone),
                R("Bone physics collider", 1).Live("VRCPhysBoneCollider").Print("shapeType", "insideBounds", "bonesAsSpheres").To<ParelMotionBoneCollider>(),
                R("Bone physics", 2).Live("VRCPhysBone").Print("integrationType", "pull", "immobile").To<ParelMotionBone>()
                    .Alias("ignoreOtherMotionBones", "ignoreOtherPhysBones")
                    .Values("allowCollision", "Nobody", "Everyone", "Everyone").Values("allowGrabbing", "Nobody", "Everyone", "Everyone")
                    .Values("allowPosing", "Nobody", "Everyone", "Everyone")
                    .Rename("False", "Nobody").Rename("True", "Everyone").Rename("Other", "Everyone")
                    .Then(PhysBoneDone),
                R("Contact sender", 1).Live("VRCContactSender").Print("collisionTags", "shapeType", "rootTransform").Not("receiverType")
                    .To<ParelContactSender>().Values("shapeType", "Sphere", "Capsule"),
                R("Contact receiver", 1).Live("VRCContactReceiver").Print("collisionTags", "receiverType").To<ParelContactReceiver>()
                    .Values("shapeType", "Sphere", "Capsule").Values("receiverType", "Constant", "OnEnter", "Proximity"),
                R("Head chop").Live("VRCHeadChop").Print("targetBones", "globalScaleFactor").To<ParelHeadChop>(),
                R("Spatial audio").Live("VRCSpatialAudioSource", "VRC_SpatialAudioSource").Print("Gain", "Far", "VolumetricRadius").To<ParelSpatialAudioSource>(),
                R("Station").Live("VRCStation", "VRC_Station").Print("PlayerMobility", "stationEnterPlayerLocation")
                    .Values("playerMobility", Mobility).Values("mobility", Mobility)
                    .Alias("seatPosition", "stationEnterPlayerLocation").Alias("exitPosition", "stationExitPlayerLocation").Alias("mobility", "PlayerMobility")
                    .Then(job =>
                    {
                        if (job.Target is VoltStation) job.Set("canUseDirectly", PortValue.Num(1));
                    }),
                R("Constraint", 3).Live("VRCParentConstraint", "VRCPositionConstraint", "VRCRotationConstraint", "VRCScaleConstraint", "VRCAimConstraint", "VRCLookAtConstraint")
                    .Print("GlobalWeight", "Sources", "IsActive"),

                // Animator controller state behaviours.
                R("Parameter driver", 10, PortKind.Behaviour).Live("VRCAvatarParameterDriver").Print("parameters", "localOnly", "debugString").To<ParelAvatarParameterDriver>(),
                R("Tracking control", 10, PortKind.Behaviour).Live("VRCAnimatorTrackingControl").Print("trackingHead", "trackingEyes").To<ParelAnimatorTrackingControl>(),
                R("Layer control", 10, PortKind.Behaviour).Live("VRCAnimatorLayerControl").Print("playable", "layer", "goalWeight").To<ParelAnimatorLayerControl>(),
                R("Playable layer control", 10, PortKind.Behaviour).Live("VRCPlayableLayerControl").Print("layer", "goalWeight", "blendDuration").Not("playable").To<ParelPlayableLayerControl>(),
                R("Locomotion control", 10, PortKind.Behaviour).Live("VRCAnimatorLocomotionControl").Print("disableLocomotion").To<ParelAnimatorLocomotionControl>(),
                R("Temporary pose space", 10, PortKind.Behaviour).Live("VRCAnimatorTemporaryPoseSpace").Print("enterPoseSpace", "fixedDelay").To<ParelAnimatorTemporaryPoseSpace>(),
                R("Play audio", 10, PortKind.Behaviour).Live("VRCAnimatorPlayAudio").Print("SourcePath", "Clips").To<ParelAnimatorPlayAudio>(),

                // Expressions assets.
                R("Expressions menu", 10, PortKind.Asset).Live("VRCExpressionsMenu").Print("controls").To<ParelExpressionsMenu>(),
                R("Expression parameters", 10, PortKind.Asset).Live("VRCExpressionParameters").Print("parameters").Not("localOnly").To<ParelExpressionParameters>(),

                // ── worlds ───────────────────────────────────────────────────
                R("World descriptor", 0).Live("VRCSceneDescriptor", "VRC_SceneDescriptor").Print("spawns", "RespawnHeightY").To<ParelWorldDescriptor>()
                    .Ignore("blueprintId").Then(WorldDescriptorDone),
                R("World descriptor", 0).Live("CVRWorld").Print("spawns", "referenceCamera").Not("RespawnHeightY").To<ParelWorldDescriptor>()
                    .Ignore("blueprintId").Then(WorldDescriptorDone),
                R("Pickup").Live("VRCPickup", "VRC_Pickup").Print("AutoHold", "InteractionText", "UseText").To<VoltPickup>()
                    .Alias("throwVelocityBoost", "ThrowVelocityBoostScale").Alias("exactGrip", "ExactGrip", "ExactGun")
                    .Ignore("allowTheft", "autoHold")
                    .Then(job =>
                    {
                        job.Set("allowTheft", PortValue.Num(job.Source.Get("DisallowTheft").AsBool() ? 0 : 1));
                        PortValue hold = job.Source.Get("AutoHold");   // AutoDetect, Yes, No
                        bool no = hold.K == PortValue.Kind.Enum && hold.Text != null ? PortValue.Norm(hold.Text) == "no" : (int)hold.AsNumber() == 2;
                        job.Set("autoHold", PortValue.Num(no ? 0 : 1));
                    }),
                R("Pickup").Live("CVRPickupObject").Print("gripType", "disallowTheft").To<VoltPickup>()
                    .Alias("proximity", "maximumGrabDistance").Alias("exactGrip", "gripOrigin").Ignore("allowTheft")
                    .Then(job => job.Set("allowTheft", PortValue.Num(job.Source.Get("disallowTheft").AsBool() ? 0 : 1))),
                R("Object sync").Live("VRCObjectSync", "VRC_ObjectSync", "CVRObjectSync").Print("AllowCollisionOwnershipTransfer").To<VoltObjectSync>()
                    .Alias("allowOwnershipTransfer", "AllowCollisionOwnershipTransfer"),
                R("Mirror").Live("VRCMirrorReflection", "VRC_MirrorReflection", "CVRMirror").Print("m_ReflectLayers", "m_DisablePixelLights").To<ParelVRMirror>()
                    .Ignore("customMaterial"),
                R("Portal").Live("VRCPortalMarker", "VRC_PortalMarker").To<ParelVRWorldPortal>().Ignore("worldId")
                    .Then(job => job.Session.Note("A portal on '" + job.Go.name + "' pointed at a world on another platform: enter a ParelVR World ID on its World Portal.")),
                R("Video player", 5).Live("VRCUnityVideoPlayer", "VRCAVProVideoPlayer", "VRC_SyncVideoPlayer", "VRC_SyncVideoStream", "CVRVideoPlayer")
                    .Print("autoPlay", "loop", "maximumResolution").To<VoltVideoPlayer>()
                    .Alias("startUrl", "videoURL", "url", "videoUrl", "defaultUrl").Alias("playOnReady", "autoPlay", "playOnAwake")
                    .Alias("screen", "targetMaterialRenderer").Then(VideoPlayerDone),
                R("Video screen", 6).Live("VRCAVProVideoScreen").Print("VideoPlayer", "MaterialIndex"),
                R("Video speaker", 6).Live("VRCAVProVideoSpeaker").Print("VideoPlayer", "Mode").Not("MaterialIndex"),
                R("UI shape").Live("VRCUiShape", "VRC_UiShape").Then(UiShapeDone),
                R("Avatar pedestal").Live("VRCAvatarPedestal", "VRC_AvatarPedestal").Print("ChangeAvatarsOnUse").To<ParelVRAvatarPedestal>()
                    .Ignore("blueprintId", "Placement", "scale")
                    .Then(job =>
                    {
                        job.Set("allowWearing", PortValue.Num(job.Source.Get("ChangeAvatarsOnUse").AsBool() ? 1 : 0));
                        job.Session.Note("An avatar pedestal on '" + job.Go.name + "' showed an avatar from another platform: enter a ParelVR Avatar ID on its Avatar Pedestal.");
                    }),
                R("Upload ID").Live("PipelineManager").Print("blueprintId", "completedSDKPipeline")
                    .Then(job => job.Session.Note("'" + job.Go.name + "' had another platform's upload ID; it was not kept.")),
                R("Udon script").Live("UdonBehaviour").Print("serializedProgramAsset").Then(job => job.Session.UdonRemoved()),
                R("Udon script").Print("programSource", "serializedPublicVariablesBytesString").Then(job => job.Session.UdonRemoved()),

                // A few more from the other platform's avatar kit, as close as ParelVR has.
                R("Pointer").Live("CVRPointer").To<ParelContactSender>().Alias("collisionTags", "type").Then(job =>
                {
                    var sphere = job.Go.GetComponent<SphereCollider>();
                    if (sphere != null) job.Set("radius", PortValue.Num(sphere.radius));
                }),
                R("Settings trigger").Live("CVRAdvancedAvatarSettingsTrigger").Print("areaSize", "areaOffset", "settingName").To<ParelContactReceiver>()
                    .Alias("size", "areaSize").Alias("position", "areaOffset").Alias("parameter", "settingName")
                    .Alias("collisionTags", "allowedTypes").Alias("value", "settingValue").Ignore("shapeType")
                    .Then(job => job.Set("shapeType", new PortValue { K = PortValue.Kind.Enum, Text = "Box" })),
            };
            // The constraint rule makes Unity's own constraints.
            list.Find(r => r.Name == "Constraint").Custom = Constraint;
            // A seat on an avatar stays an avatar station; in a world it becomes a Volt Station.
            list.Find(r => r.Name == "Station").Target = item => item.OnAvatar ? typeof(ParelStation) : typeof(VoltStation);
            return list.ToArray();
        }

        // ── finding rules ────────────────────────────────────────────────────

        public static PortRule ForType(Type type, PortKind kind)
        {
            foreach (PortRule r in All)
                if (r.Kind == kind && r.MatchesType(type)) return r;
            return null;
        }

        public static PortRule ForFields(PortValue v, PortKind kind)
        {
            foreach (PortRule r in All)
                if (r.Kind == kind && r.MatchesFields(v)) return r;
            return null;
        }

        /// <summary>Removed with nothing in its place: from another platform, but ParelVR has no counterpart.</summary>
        public static readonly PortRule Unmatched = new PortRule { Name = "No ParelVR counterpart", Order = 20 };

        /// <summary>A missing script whose script is not in the project at all: another platform's, whose SDK is not installed.</summary>
        public static readonly PortRule UnknownMissing = new PortRule { Name = "Missing script", Order = 20 };

        /// <summary>True for a component from another platform's SDK, matched by its namespace (or its base type's).</summary>
        public static bool IsForeign(Type type)
        {
            for (Type t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(StateMachineBehaviour) && t != typeof(ScriptableObject); t = t.BaseType)
            {
                string ns = t.Namespace ?? string.Empty;
                string name = t.Name;
                if (ns.StartsWith("VRC.", StringComparison.Ordinal) || ns == "VRC" || ns.StartsWith("VRCSDK", StringComparison.Ordinal)) return true;
                if (ns.StartsWith("ABI.CCK", StringComparison.Ordinal) || ns.StartsWith("ABI_RC", StringComparison.Ordinal)) return true;
                if (ns == "UdonSharp" || ns.StartsWith("UdonSharp.", StringComparison.Ordinal)) return true;
                if (ns.Length == 0 && name.StartsWith("VRC_", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // ── wiring that is not a straight copy ───────────────────────────────

        private static void AvatarDescriptorDone(PortJob job)
        {
            job.Set("viewPointInitialized", PortValue.Num(1));
            var animator = job.Go.GetComponent<Animator>();
            if (animator != null) job.Set("avatarAnimator", PortValue.Ref(animator));
            var descriptor = (ParelAvatarDescriptor)job.Target;
            // The playable layers keep their controllers; what the controllers hold from the other platform is ported too.
            foreach (ParelAvatarDescriptor.CustomAnimLayer layer in descriptor.BaseAnimationLayers) job.Session.PortController(layer.animatorController);
            foreach (ParelAvatarDescriptor.CustomAnimLayer layer in descriptor.SpecialAnimationLayers) job.Session.PortController(layer.animatorController);
        }

        private static void CvrAvatarDone(PortJob job)
        {
            job.Set("viewPointInitialized", PortValue.Num(1));
            var animator = job.Go.GetComponent<Animator>();
            if (animator != null) job.Set("avatarAnimator", PortValue.Ref(animator));
            PortValue s = job.Source;
            if (s.Has("useVisemeLipsync") && !s.Get("useVisemeLipsync").AsBool()) job.Set("lipSync", new PortValue { K = PortValue.Kind.Enum, Text = "None" });
            if (s.Get("useBlinkBlendshapes").AsBool())
            {
                var mesh = job.Session.Resolve(s.Get("bodyMesh"), "SkinnedMeshRenderer") as SkinnedMeshRenderer;
                PortValue blink = s.Get("blinkBlendshape");
                string name = blink.K == PortValue.Kind.List && blink.List.Count > 0 ? blink.List[0].AsString() : blink.AsString();
                int index = mesh != null && mesh.sharedMesh != null && !string.IsNullOrEmpty(name) ? mesh.sharedMesh.GetBlendShapeIndex(name) : -1;
                if (mesh != null && index >= 0)
                {
                    job.Set("enableEyeLook", PortValue.Num(1));
                    PortValue eye = PortValue.NewMap();
                    eye.Set("eyelidType", new PortValue { K = PortValue.Kind.Enum, Text = "Blendshapes" });
                    eye.Set("eyelidsSkinnedMesh", PortValue.Ref(mesh));
                    PortValue shapes = PortValue.NewList();
                    shapes.List.Add(PortValue.Num(index));
                    shapes.List.Add(PortValue.Num(-1));
                    shapes.List.Add(PortValue.Num(-1));
                    eye.Set("eyelidsBlendshapes", shapes);
                    job.Set("customEyeLookSettings", eye);
                }
            }
            if (!s.Get("overrides").IsNull)
                job.Session.Note("'" + job.Go.name + "' used an animator override from another platform; set its FX layer on the ParelVR Avatar Descriptor.");
        }

        private static void PhysBoneDone(PortJob job)
        {
            // "Other" on the other platform means a filter of self and others.
            Permission(job, "allowCollision", "collisionFilter");
            Permission(job, "allowGrabbing", "grabFilter");
            Permission(job, "allowPosing", "poseFilter");
        }

        private static void Permission(PortJob job, string field, string filterField)
        {
            PortValue v = job.Source.Get(field);
            bool other = v.K == PortValue.Kind.Enum && v.Text != null ? PortValue.Norm(v.Text) == "other" : (int)v.AsNumber() == 2;
            if (!other) return;
            PortValue filter = job.Source.Get(filterField);
            bool self = filter.Get("allowSelf").AsBool(true), others = filter.Get("allowOthers").AsBool(true);
            string to = self && others ? "Everyone" : self ? "SelfOnly" : others ? "OthersOnly" : "Nobody";
            job.Set(field, new PortValue { K = PortValue.Kind.Enum, Text = to });
        }

        private static void WorldDescriptorDone(PortJob job)
        {
            PortValue spawns = job.Source.Get("spawns");
            var points = new List<Transform>();
            if (spawns.K == PortValue.Kind.List)
                foreach (PortValue s in spawns.List)
                {
                    UnityEngine.Object o = job.Session.Resolve(s, "Transform");
                    Transform t = o is Component c ? c.transform : o is GameObject g ? g.transform : null;
                    if (t != null && !points.Contains(t)) points.Add(t);
                }
            if (points.Count == 0) points.Add(job.Go.transform);
            // The first spawn came first there; it gets the highest priority here.
            for (int i = 0; i < points.Count; i++)
            {
                ParelVRSpawnPoint point = points[i].GetComponent<ParelVRSpawnPoint>();
                if (point == null) point = job.Session.Add<ParelVRSpawnPoint>(points[i].gameObject);
                if (point == null) continue;
                PortWriter.Set(point, "priority", PortValue.Num(points.Count - i), null, null);
            }
        }

        private static void VideoPlayerDone(PortJob job)
        {
            var player = (VoltVideoPlayer)job.Target;
            // Screens and speakers were separate components pointing at the player.
            foreach (PortItem other in job.Session.Items)
            {
                if (other.Rule.Name != "Video screen" && other.Rule.Name != "Video speaker") continue;
                if (!job.Session.SameSource(other.Value.Get("VideoPlayer"), job.Item)) continue;
                if (other.Rule.Name == "Video screen" && player.screen == null) player.screen = other.Go.GetComponent<Renderer>();
                if (other.Rule.Name == "Video speaker" && player.audioOutput == null) player.audioOutput = other.Go.GetComponent<AudioSource>();
            }
            if (player.audioOutput == null)
            {
                PortValue sources = job.Source.Get("targetAudioSources");
                if (sources.K == PortValue.Kind.List && sources.List.Count > 0) player.audioOutput = job.Session.Resolve(sources.List[0], "AudioSource") as AudioSource;
            }
            if (player.audioOutput == null) player.audioOutput = job.Go.GetComponent<AudioSource>();
            EditorUtility.SetDirty(player);
        }

        private static void UiShapeDone(PortJob job)
        {
            // It gave the canvas a collider so pointers can reach it; ParelVR's pointers need the same.
            if (job.Go.GetComponent<Collider>() != null || !(job.Go.transform is RectTransform rect)) return;
            BoxCollider box = job.Session.Add<BoxCollider>(job.Go);
            if (box == null) return;
            box.size = new Vector3(rect.rect.width, rect.rect.height, 0.01f);
            box.center = new Vector3((0.5f - rect.pivot.x) * rect.rect.width, (0.5f - rect.pivot.y) * rect.rect.height, 0f);
            box.isTrigger = true;
        }

        // ── constraints become Unity's own ───────────────────────────────────

        private static Component Constraint(PortJob job)
        {
            PortValue v = job.Source;
            string kind = job.Item.Live != null ? job.Item.Live.GetType().Name : v.Has("AimAxis") ? "VRCAimConstraint"
                : v.Has("UseUpTransform") || v.Has("Roll") ? "VRCLookAtConstraint"
                : v.Has("AffectsPositionX") && v.Has("AffectsRotationX") ? "VRCParentConstraint"
                : v.Has("AffectsPositionX") ? "VRCPositionConstraint"
                : v.Has("AffectsRotationX") ? "VRCRotationConstraint"
                : v.Has("AffectsScaleX") ? "VRCScaleConstraint" : null;
            if (kind == null) return null;

            GameObject on = job.Go;
            var target = job.Session.Resolve(v.Get("TargetTransform"), "Transform") as Transform;
            if (target != null) on = target.gameObject;

            Type type = kind == "VRCParentConstraint" ? typeof(ParentConstraint)
                : kind == "VRCPositionConstraint" ? typeof(PositionConstraint)
                : kind == "VRCRotationConstraint" ? typeof(RotationConstraint)
                : kind == "VRCScaleConstraint" ? typeof(ScaleConstraint)
                : kind == "VRCAimConstraint" ? typeof(AimConstraint) : typeof(LookAtConstraint);
            Component made = on.GetComponent(type);
            if (made == null) made = job.Session.Add(on, type);
            if (!(made is IConstraint constraint)) return made;

            var sources = new List<PortValue>();
            PortValue list = v.Get("Sources");
            if (list.K == PortValue.Kind.List) sources.AddRange(list.List);
            else if (list.K == PortValue.Kind.Map)
            {
                // Kept as source0, source1, … and an overflow list, so each can be animated.
                int total = (int)list.Get("totalLength").AsNumber(16);
                for (int i = 0; i < 16 && sources.Count < total; i++)
                {
                    PortValue s = list.Get("source" + i);
                    if (s.IsNull) break;
                    sources.Add(s);
                }
                PortValue overflow = list.Get("overflowList");
                if (overflow.K == PortValue.Kind.List) sources.AddRange(overflow.List);
            }
            while (constraint.sourceCount > 0) constraint.RemoveSource(0);
            foreach (PortValue s in sources)
            {
                var from = job.Session.Resolve(s.Get("SourceTransform"), "Transform") as Transform;
                if (from == null) continue;
                int index = constraint.AddSource(new ConstraintSource { sourceTransform = from, weight = (float)s.Get("Weight").AsNumber(1) });
                if (made is ParentConstraint parent)
                {
                    parent.SetTranslationOffset(index, V3(s.Get("ParentPositionOffset")));
                    parent.SetRotationOffset(index, V3(s.Get("ParentRotationOffset")));
                }
            }
            constraint.weight = (float)v.Get("GlobalWeight").AsNumber(1);
            constraint.constraintActive = v.Get("IsActive").AsBool(true);
            constraint.locked = v.Get("Locked").AsBool(true);

            Axis Axes(string prefix) =>
                (v.Get(prefix + "X").AsBool(true) ? Axis.X : Axis.None) | (v.Get(prefix + "Y").AsBool(true) ? Axis.Y : Axis.None) | (v.Get(prefix + "Z").AsBool(true) ? Axis.Z : Axis.None);
            switch (made)
            {
                case ParentConstraint p:
                    p.translationAtRest = V3(v.Get("PositionAtRest"));
                    p.rotationAtRest = V3(v.Get("RotationAtRest"));
                    p.translationAxis = Axes("AffectsPosition");
                    p.rotationAxis = Axes("AffectsRotation");
                    break;
                case PositionConstraint p:
                    p.translationAtRest = V3(v.Get("PositionAtRest"));
                    p.translationOffset = V3(v.Get("PositionOffset"));
                    p.translationAxis = Axes("AffectsPosition");
                    break;
                case RotationConstraint p:
                    p.rotationAtRest = V3(v.Get("RotationAtRest"));
                    p.rotationOffset = V3(v.Get("RotationOffset"));
                    p.rotationAxis = Axes("AffectsRotation");
                    break;
                case ScaleConstraint p:
                    p.scaleAtRest = V3(v.Get("ScaleAtRest"), Vector3.one);
                    p.scaleOffset = V3(v.Get("ScaleOffset"), Vector3.one);
                    p.scalingAxis = Axes("AffectsScale");
                    break;
                case AimConstraint p:
                    p.rotationAtRest = V3(v.Get("RotationAtRest"));
                    p.rotationOffset = V3(v.Get("RotationOffset"));
                    p.rotationAxis = Axes("AffectsRotation");
                    if (v.Has("AimAxis")) p.aimVector = V3(v.Get("AimAxis"), Vector3.forward);
                    if (v.Has("UpAxis")) p.upVector = V3(v.Get("UpAxis"), Vector3.up);
                    if (v.Has("WorldUpVector")) p.worldUpVector = V3(v.Get("WorldUpVector"), Vector3.up);
                    var aimUp = job.Session.Resolve(v.Get("WorldUpTransform"), "Transform") as Transform;
                    if (aimUp != null) p.worldUpObject = aimUp;
                    break;
                case LookAtConstraint p:
                    p.rotationAtRest = V3(v.Get("RotationAtRest"));
                    p.rotationOffset = V3(v.Get("RotationOffset"));
                    p.roll = (float)v.Get("Roll").AsNumber();
                    p.useUpObject = v.Get("UseUpTransform").AsBool();
                    var lookUp = job.Session.Resolve(v.Get("WorldUpTransform"), "Transform") as Transform;
                    if (lookUp != null) p.worldUpObject = lookUp;
                    break;
            }
            EditorUtility.SetDirty(made);
            return made;
        }

        private static Vector3 V3(PortValue v, Vector3 fallback = default)
        {
            if (v.K != PortValue.Kind.Map) return fallback;
            return new Vector3((float)v.Get("x").AsNumber(fallback.x), (float)v.Get("y").AsNumber(fallback.y), (float)v.Get("z").AsNumber(fallback.z));
        }
    }
}
