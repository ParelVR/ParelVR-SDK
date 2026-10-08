using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Backend;
using ParelVR.SDK.Worlds.Components;
using ParelVR.SDK.Worlds.Validation;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Worlds.UI
{
    /// <summary>
    /// Sends a world's credits, shop theme and monetization along with a publish. Credit links are
    /// checked again here, so a link that was typed but never checked cannot slip through.
    /// </summary>
    public static class WorldPagePublisher
    {
        [Serializable] private class PersonDto { public string username; public string displayName; public string url; public string platform; }
        [Serializable] private class RoleDto { public string title; public List<PersonDto> people = new List<PersonDto>(); }
        [Serializable] private class ThemeDto { public string highlight, background, headerText, subText, text, card; public bool hasBackgroundImage; }
        [Serializable] private class ItemDto { public string id, name, description, price; public bool hasImage; }
        [Serializable] private class TierDto { public string id, name, description, pricePerMonth; public List<string> perks = new List<string>(); }
        [Serializable] private class ProductDto { public string id, name, description, price; public bool consumable; }

        [Serializable]
        private class StoreDto
        {
            public string currency;
            public bool enabled;
            public string name, tagline;
            public List<ItemDto> items = new List<ItemDto>();
            public List<TierDto> tiers = new List<TierDto>();
            public List<ProductDto> products = new List<ProductDto>();
        }

        [Serializable]
        private class PageDto
        {
            public List<RoleDto> credits = new List<RoleDto>();
            public ThemeDto theme;
            public StoreDto store;
        }

        /// <summary>Throws with a readable reason when something in the page cannot be published.</summary>
        public static string BuildJson(ParelWorldDescriptor descriptor)
        {
            var page = new PageDto();

            foreach (ParelWorldCreditRole role in descriptor.credits)
            {
                var dto = new RoleDto { title = (role.title ?? string.Empty).Trim() };
                foreach (ParelWorldCreditPerson person in role.people)
                {
                    if (person.kind == ParelCreditKind.ParelVRAccount)
                    {
                        string username = (person.username ?? string.Empty).Trim().TrimStart('@');
                        if (username.Length > 0) dto.people.Add(new PersonDto { username = username });
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(person.url)) continue;
                    ParelCreditLinkValidator.Result link = ParelCreditLinkValidator.Check(person.url);
                    if (!link.ok)
                        throw new Exception("Credit link for \"" + Name(person) + "\" was not accepted: " + link.message);
                    dto.people.Add(new PersonDto { displayName = Name(person), url = link.url, platform = link.platform });
                }

                if (dto.people.Count == 0) continue;
                if (dto.title.Length == 0) throw new Exception("A credit role needs a title (for example \"Developer of UI\").");
                page.credits.Add(dto);
            }

            ParelWorldShopTheme theme = descriptor.shopTheme;
            page.theme = new ThemeDto
            {
                highlight = Hex(theme.highlight),
                background = Hex(theme.background),
                headerText = Hex(theme.headerText),
                subText = Hex(theme.subText),
                text = Hex(theme.text),
                card = Hex(theme.card),
                hasBackgroundImage = theme.backgroundImage != null,
            };

            ParelWorldMonetization money = descriptor.monetization;
            var store = new StoreDto { currency = money.currency, enabled = money.storeEnabled, name = money.storeName, tagline = money.storeTagline };
            var ids = new HashSet<string>();
            if (money.storeEnabled)
            {
                foreach (ParelWorldStoreProduct item in money.products)
                {
                    RequireId(ids, item.id, item.name);
                    store.items.Add(new ItemDto { id = item.id, name = item.name, description = item.description, price = Money(item.price), hasImage = item.image != null });
                }
            }

            foreach (ParelWorldSubscriptionTier tier in money.tiers)
            {
                RequireId(ids, tier.id, tier.name);
                var dto = new TierDto { id = tier.id, name = tier.name, description = tier.description, pricePerMonth = Money(tier.pricePerMonth) };
                foreach (string perk in (tier.perks ?? string.Empty).Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(perk)) dto.perks.Add(perk.Trim());
                }

                store.tiers.Add(dto);
            }

            foreach (ParelWorldInGameProduct product in money.inGameProducts)
            {
                RequireId(ids, product.id, product.name);
                store.products.Add(new ProductDto { id = product.id, name = product.name, description = product.description, price = Money(product.price), consumable = product.consumable });
            }

            page.store = store;
            return JsonUtility.ToJson(page);
        }

        public static async Task PublishAsync(ParelWorldDescriptor descriptor, string worldId, CancellationToken ct)
        {
            await WorldApiClient.SaveWorldPageAsync(worldId, BuildJson(descriptor), ct);

            await UploadImage(worldId, "shop-background", descriptor.shopTheme.backgroundImage, ct);
            if (descriptor.monetization.storeEnabled)
            {
                foreach (ParelWorldStoreProduct item in descriptor.monetization.products)
                {
                    await UploadImage(worldId, "store/items/" + item.id + "/image", item.image, ct);
                }
            }
        }

        /// <summary>Sends the picture's own file (PNG or JPG). Other formats are skipped with a note in the console.</summary>
        private static async Task UploadImage(string worldId, string slot, Texture2D image, CancellationToken ct)
        {
            if (image == null) return;

            string path = AssetDatabase.GetAssetPath(image);
            string extension = Path.GetExtension(path).ToLowerInvariant();
            string type = extension == ".png" ? "image/png" : extension == ".jpg" || extension == ".jpeg" ? "image/jpeg" : null;
            if (type == null || !File.Exists(path))
            {
                Debug.LogWarning("[ParelVR SDK] " + image.name + " was not uploaded: store pictures have to be PNG or JPG files.");
                return;
            }

            await WorldApiClient.UploadWorldPageImageAsync(worldId, slot, File.ReadAllBytes(path), type, ct);
        }

        private static void RequireId(HashSet<string> ids, string id, string name)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new Exception("\"" + name + "\" in Monetization has no id.");
            if (!ids.Add(id)) throw new Exception("Two things in Monetization share the id \"" + id + "\". Ids have to be different.");
        }

        private static string Name(ParelWorldCreditPerson person)
        {
            return string.IsNullOrWhiteSpace(person.displayName) ? person.url : person.displayName.Trim();
        }

        private static string Money(float amount)
        {
            return Mathf.Max(0f, amount).ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string Hex(Color color)
        {
            return "#" + ColorUtility.ToHtmlStringRGB(color);
        }
    }
}
