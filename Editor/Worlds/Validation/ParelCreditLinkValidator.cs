using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace ParelVR.SDK.Worlds.Validation
{
    /// <summary>
    /// Checks an external credit link before it can be published: it has to be a person's profile
    /// page on a known platform. Shop and marketplace pages, link shorteners, raw IP addresses and
    /// anything that is not plain https are turned away.
    ///
    /// Everything here runs on the creator's own computer. The live check sends one anonymous
    /// request straight to the platform (no ParelVR sign-in, no cookies), so the ParelVR backend is
    /// never asked to open a stranger's link and nothing about it is sent anywhere.
    /// </summary>
    public static class ParelCreditLinkValidator
    {
        public sealed class Result
        {
            public bool ok;
            public string message;
            public string platform;
            public string url;
        }

        private sealed class Platform
        {
            public string Name;
            public string[] Hosts;
            public Regex ProfilePath;
            public HashSet<string> Reserved;
        }

        private const string AskForProfile = "Link the person's profile page on another platform (for example X, YouTube, Twitch, GitHub, Instagram, TikTok, Bluesky, ArtStation, LinkedIn, Steam, Patreon or Ko-fi).";

        private static readonly Platform[] Platforms =
        {
            P("X", "^/[A-Za-z0-9_]{1,15}/?$", "home explore search i settings login signup share intent hashtag messages notifications compose tos privacy", "x.com", "twitter.com"),
            P("YouTube", "^/(@[\\w.\\-]{2,40}|channel/[\\w\\-]{10,40}|c/[\\w.\\-]{1,60}|user/[\\w.\\-]{1,60})/?$", "", "youtube.com"),
            P("Twitch", "^/[A-Za-z0-9_]{3,25}/?$", "directory videos downloads store p settings subscriptions inventory wallet drops jobs turbo search", "twitch.tv"),
            P("GitHub", "^/[A-Za-z0-9\\-]{1,39}/?$", "features pricing marketplace explore topics login join settings orgs organizations sponsors about search notifications pulls issues collections trending enterprise", "github.com"),
            P("Instagram", "^/[A-Za-z0-9_.]{1,30}/?$", "p reel reels explore accounts stories direct tv about legal developer", "instagram.com"),
            P("TikTok", "^/@[\\w.\\-]{2,24}/?$", "", "tiktok.com"),
            P("Bluesky", "^/profile/[\\w.\\-:]{3,80}/?$", "", "bsky.app"),
            P("ArtStation", "^/[A-Za-z0-9_\\-]{2,60}/?$", "marketplace store learning jobs search prints blogs challenges about artwork magazine", "artstation.com"),
            P("LinkedIn", "^/in/[\\w\\-%]{2,100}/?$", "", "linkedin.com"),
            P("Steam", "^/(id/[\\w\\-]{2,40}|profiles/\\d{10,20})/?$", "", "steamcommunity.com"),
            P("Patreon", "^/(c/)?[A-Za-z0-9_\\-]{2,64}/?$", "shop home login signup explore search settings pledges product policy about", "patreon.com"),
            P("Ko-fi", "^/[A-Za-z0-9_\\-]{2,64}/?$", "shop s explore account home about gold manage", "ko-fi.com"),
            P("SoundCloud", "^/[A-Za-z0-9_\\-]{2,40}/?$", "discover stream search upload you settings pages charts people", "soundcloud.com"),
            P("Facebook", "^/[A-Za-z0-9.\\-]{3,60}/?$", "marketplace groups watch gaming events login pages help policies privacy ads business", "facebook.com"),
            P("Discord", "^/users/\\d{15,22}/?$", "", "discord.com"),
        };

        private static readonly string[] ShopHosts =
        {
            "booth.pm", "gumroad.com", "etsy.com", "amazon.com", "ebay.com", "payhip.com", "itch.io", "myshopify.com", "shopify.com",
            "jinxxy.com", "redbubble.com", "teespring.com", "spri.ng", "bigcartel.com", "aliexpress.com", "sellfy.com", "lemonsqueezy.com",
            "fourthwall.com", "throne.com", "teepublic.com", "society6.com", "depop.com", "vinted.com", "temu.com", "walmart.com",
        };

        private static readonly HashSet<string> ShopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "shop", "store", "product", "products", "item", "items", "cart", "checkout", "listing", "listings", "buy", "merch", "marketplace", "order",
        };

        /// <summary>The rules that need no network: scheme, host, platform, and whether the path is a profile.</summary>
        public static Result Check(string input)
        {
            string text = (input ?? string.Empty).Trim();
            if (text.Length == 0) return Fail("Enter a link.");
            if (text.Length > 200) return Fail("That link is too long.");
            foreach (char c in text)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c)) return Fail("That link has spaces or hidden characters in it.");
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri uri)) return Fail("That is not a web address. It should start with https://");
            if (uri.Scheme != Uri.UriSchemeHttps) return Fail("Only https:// links are allowed.");
            if (!string.IsNullOrEmpty(uri.UserInfo)) return Fail("Links with a sign-in in them are not allowed.");
            if (!uri.IsDefaultPort) return Fail("Links with a port number are not allowed.");
            if (uri.HostNameType != UriHostNameType.Dns) return Fail("Links to a raw IP address are not allowed.");

            string host = uri.IdnHost.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);
            if (host.StartsWith("m.") || host.StartsWith("mobile.")) host = host.Substring(host.IndexOf('.') + 1);
            if (host.Contains("xn--")) return Fail("Look-alike web addresses are not allowed.");
            if (!host.Contains(".") || host.EndsWith(".local") || host.EndsWith(".internal") || host.EndsWith(".lan") || host == "localhost")
                return Fail("That is not a public web address.");

            foreach (string shop in ShopHosts)
            {
                if (host == shop || host.EndsWith("." + shop)) return Fail("That is a shop, not a profile. " + AskForProfile);
            }

            string path = uri.AbsolutePath;
            foreach (string segment in path.Split('/'))
            {
                if (ShopWords.Contains(segment)) return Fail("That looks like a shop page, not a profile. " + AskForProfile);
            }

            foreach (Platform platform in Platforms)
            {
                if (Array.IndexOf(platform.Hosts, host) < 0) continue;

                string first = path.Trim('/').Split('/')[0];
                if (path.Trim('/').Length == 0 || platform.Reserved.Contains(first) || !platform.ProfilePath.IsMatch(path))
                    return Fail("That " + platform.Name + " link is not a profile page. Link the person's own profile.");

                // Query strings and fragments are dropped: nothing of the creator's browsing rides along.
                return new Result { ok = true, platform = platform.Name, url = "https://" + platform.Hosts[0] + path.TrimEnd('/'), message = platform.Name + " profile" };
            }

            return Fail("That site is not a profile platform ParelVR recognises. " + AskForProfile);
        }

        /// <summary>
        /// <see cref="Check"/>, then one anonymous request to see that the profile is really there
        /// and does not bounce to a different site.
        /// </summary>
        public static async Task<Result> ResolveAsync(string input, CancellationToken ct = default)
        {
            Result offline = Check(input);
            if (!offline.ok) return offline;

            using (UnityWebRequest request = UnityWebRequest.Head(offline.url))
            {
                request.redirectLimit = 3;
                request.timeout = 8;
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (ct.IsCancellationRequested)
                    {
                        request.Abort();
                        return Fail("Check cancelled.");
                    }

                    await Task.Yield();
                }

                if (request.result == UnityWebRequest.Result.ConnectionError)
                    return Fail("Could not reach " + offline.platform + " to check the link. Try again.");
                if (request.responseCode == 404 || request.responseCode == 410)
                    return Fail("That " + offline.platform + " profile does not exist.");

                // Wherever it ended up has to pass the same rules, or it was a redirect somewhere else.
                if (Uri.TryCreate(request.url, UriKind.Absolute, out Uri landed))
                {
                    string landedHost = landed.IdnHost.ToLowerInvariant();
                    bool samePlatform = false;
                    foreach (Platform platform in Platforms)
                    {
                        if (platform.Name != offline.platform) continue;
                        foreach (string host in platform.Hosts)
                        {
                            if (landedHost == host || landedHost.EndsWith("." + host)) samePlatform = true;
                        }
                    }

                    if (!samePlatform) return Fail("That link redirects to a different site, so it was not accepted.");
                }
            }

            offline.message = offline.platform + " profile, checked";
            return offline;
        }

        private static Platform P(string name, string profilePath, string reserved, params string[] hosts)
        {
            return new Platform
            {
                Name = name,
                Hosts = hosts,
                ProfilePath = new Regex(profilePath, RegexOptions.CultureInvariant),
                Reserved = new HashSet<string>(reserved.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase),
            };
        }

        private static Result Fail(string message)
        {
            return new Result { ok = false, message = message };
        }
    }
}
