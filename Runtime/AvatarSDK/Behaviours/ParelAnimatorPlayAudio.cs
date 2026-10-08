using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// VRChat's Animator Play Audio: plays (or stops) clips on one of the avatar's AudioSources when
    /// its state is entered or exited, with random / round-robin / parameter-chosen clips.
    /// </summary>
    public sealed class ParelAnimatorPlayAudio : StateMachineBehaviour
    {
        public enum Order
        {
            Random = 0,
            UniqueRandom = 1,
            Roundrobin = 2,
            Parameter = 3,
        }

        public enum ApplySettings
        {
            NeverApply = 0,
            AlwaysApply = 1,
            ApplyIfStopped = 2,
        }

        [Tooltip("Path of the AudioSource's object, relative to the avatar root (empty = the root).")]
        public string sourcePath = string.Empty;
        public AudioClip[] clips = new AudioClip[0];
        public Order playbackOrder = Order.Random;
        [Tooltip("Parameter order: the parameter holding the clip index.")]
        public string parameterName = string.Empty;
        public Vector2 volume = new Vector2(1f, 1f);
        public Vector2 pitch = new Vector2(1f, 1f);
        public bool loop;
        public ApplySettings clipsApplySettings = ApplySettings.AlwaysApply;
        public ApplySettings volumeApplySettings = ApplySettings.AlwaysApply;
        public ApplySettings pitchApplySettings = ApplySettings.AlwaysApply;
        public ApplySettings loopApplySettings = ApplySettings.AlwaysApply;
        public bool stopOnEnter;
        public bool playOnEnter = true;
        public bool stopOnExit;
        public bool playOnExit;
        [Tooltip("Seconds to wait before playing.")]
        public float delayInSeconds;

        private int _roundRobin;
        private readonly List<int> _unplayed = new List<int>();
        private static readonly System.Random Rng = new System.Random();

        [Tooltip("Logged to the console each time this state is entered (handy while building).")]
        public string debugString = string.Empty;

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (!string.IsNullOrEmpty(debugString)) Debug.Log($"[ParelAnimatorPlayAudio] {debugString}", animator);
            AudioSource source = Resolve(animator);
            if (source == null) return;
            if (stopOnEnter) source.Stop();
            if (playOnEnter) Play(animator, source);
        }

        public override void OnStateExit(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            AudioSource source = Resolve(animator);
            if (source == null) return;
            if (stopOnExit) source.Stop();
            if (playOnExit) Play(animator, source);
        }

        private AudioSource Resolve(Animator animator)
        {
            if (animator == null) return null;
            Transform root = animator.transform;
            ParelAvatarDescriptor descriptor = animator.GetComponentInParent<ParelAvatarDescriptor>();
            if (descriptor != null) root = descriptor.transform;
            Transform target = string.IsNullOrEmpty(sourcePath) ? root : root.Find(sourcePath);
            return target != null ? target.GetComponent<AudioSource>() : null;
        }

        private void Play(Animator animator, AudioSource source)
        {
            bool stopped = !source.isPlaying;

            if (ShouldApply(clipsApplySettings, stopped))
            {
                AudioClip clip = PickClip(animator);
                if (clip != null) source.clip = clip;
            }
            if (ShouldApply(volumeApplySettings, stopped)) source.volume = Mathf.Clamp01(RandomIn(volume));
            if (ShouldApply(pitchApplySettings, stopped)) source.pitch = Mathf.Clamp(RandomIn(pitch), -3f, 3f);
            if (ShouldApply(loopApplySettings, stopped)) source.loop = loop;

            if (source.clip == null) return;
            if (delayInSeconds > 0f) source.PlayDelayed(delayInSeconds);
            else source.Play();
        }

        private static bool ShouldApply(ApplySettings setting, bool stopped)
        {
            return setting == ApplySettings.AlwaysApply || (setting == ApplySettings.ApplyIfStopped && stopped);
        }

        private static float RandomIn(Vector2 range)
        {
            float min = Mathf.Min(range.x, range.y);
            float max = Mathf.Max(range.x, range.y);
            return min + (float)Rng.NextDouble() * (max - min);
        }

        private AudioClip PickClip(Animator animator)
        {
            if (clips == null || clips.Length == 0) return null;
            int count = clips.Length;
            switch (playbackOrder)
            {
                case Order.Roundrobin:
                {
                    AudioClip clip = clips[_roundRobin % count];
                    _roundRobin = (_roundRobin + 1) % count;
                    return clip;
                }
                case Order.UniqueRandom:
                {
                    if (_unplayed.Count == 0)
                    {
                        for (int i = 0; i < count; i++) _unplayed.Add(i);
                    }
                    int slot = Rng.Next(0, _unplayed.Count);
                    int index = _unplayed[slot];
                    _unplayed.RemoveAt(slot);
                    return clips[index];
                }
                case Order.Parameter:
                {
                    IParelAvatarParameterSink sink = ParelAvatarSinks.Find(animator);
                    if (sink == null || !sink.TryGetParameter(parameterName, out float value)) return clips[0];
                    return clips[Mathf.Clamp(Mathf.RoundToInt(value), 0, count - 1)];
                }
                default:
                    return clips[Rng.Next(0, count)];
            }
        }
    }
}
