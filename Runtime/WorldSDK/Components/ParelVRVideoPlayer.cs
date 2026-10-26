using System;
using UnityEngine;
using UnityEngine.Video;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// ParelVR's creator-facing video player entry point. Wraps a standard Unity
    /// <see cref="VideoPlayer"/> (auto-resolved from the same GameObject if not explicitly
    /// assigned) with the handful of controls a world creator actually needs -- play-on-start,
    /// loop, and public Play/Pause/Stop for wiring up buttons or other scripts. Does not
    /// reimplement video decoding/playback itself; that's Unity's own VideoPlayer.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/World SDK/ParelVR Video Player")]
    public sealed class ParelVRVideoPlayer : MonoBehaviour
    {
        [SerializeField] private VideoPlayer player;
        [SerializeField] private bool playOnStart = true;
        [SerializeField] private bool loop = true;

        public VideoPlayer Player => player;
        public bool PlayOnStart { get => playOnStart; set => playOnStart = value; }
        public bool Loop { get => loop; set => loop = value; }

        private void Awake()
        {
            if (player == null)
            {
                player = GetComponent<VideoPlayer>();
            }
        }

        private void Start()
        {
            if (player == null)
            {
                return;
            }

            WarnIfPageUrl(player.url);
            player.isLooping = loop;
            if (playOnStart)
            {
                player.Play();
            }
        }

        private void OnValidate()
        {
            if (player != null) WarnIfPageUrl(player.url);
        }

        private void WarnIfPageUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return;
            string host = uri.Host.ToLowerInvariant();
            if (host == "youtu.be" || host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal) ||
                host == "twitch.tv" || host.EndsWith(".twitch.tv", StringComparison.Ordinal) ||
                host == "kick.com" || host.EndsWith(".kick.com", StringComparison.Ordinal))
            {
                Debug.LogWarning("[ParelVRVideoPlayer] '" + url + "' is a page URL. Unity's VideoPlayer cannot resolve it; use ParelVR VoltVideoPlayer or a direct media URL.", this);
            }
        }

        public void Play() => player?.Play();
        public void Pause() => player?.Pause();
        public void Stop() => player?.Stop();
    }
}
