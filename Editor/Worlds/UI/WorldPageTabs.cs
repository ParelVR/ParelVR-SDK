using System;
using System.Collections.Generic;
using System.Globalization;
using ParelVR.SDK.Core.Auth;
using ParelVR.SDK.Core.ControlPanel;
using ParelVR.SDK.Core.Http;
using ParelVR.SDK.Core.Settings;
using ParelVR.SDK.Core.Util;
using ParelVR.SDK.Worlds.Components;
using ParelVR.SDK.Worlds.Validation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ParelVR.SDK.Worlds.UI
{
    /// <summary>
    /// What the Credits and Monetization sections of the Builder share: they both edit the scene's World
    /// Descriptor and rebuild themselves whenever a row is added or removed. The Builder hosts them inside its
    /// own sections, so they are not tabs.
    /// </summary>
    public abstract class WorldPageTabBase
    {
        public abstract string TabName { get; }
        public abstract int TabOrder { get; }

        protected ParelWorldDescriptor Descriptor;
        private VisualElement _root;

        public void BuildUI(VisualElement container)
        {
            _root = new VisualElement();
            _root.AddToClassList("bk-page");
            container.Add(_root);
            Rebuild();
        }

        public void OnShown() => Rebuild();

        protected void Rebuild()
        {
            if (_root == null) return;
            _root.Clear();

            Descriptor = UnityEngine.Object.FindFirstObjectByType<ParelWorldDescriptor>();
            if (Descriptor == null)
            {
                _root.Add(new HelpBox("Set up this scene as a world in the Builder tab first.", HelpBoxMessageType.Info));
                return;
            }

            Build(_root);
        }

        protected abstract void Build(VisualElement root);

        /// <summary>Changes the descriptor so the edit is undoable and saved with the scene.</summary>
        protected void Change(Action edit, bool rebuild = false)
        {
            if (Descriptor == null) return;
            Undo.RecordObject(Descriptor, "Edit World " + TabName);
            edit();
            EditorUtility.SetDirty(Descriptor);
            EditorSceneManager.MarkSceneDirty(Descriptor.gameObject.scene);
            if (rebuild) Rebuild();
        }

        // ---- small pieces both tabs are made of ---------------------------------------------

        protected static Label Heading(string text)
        {
            var heading = new Label(text);
            heading.AddToClassList("bk-section-title");
            return heading;
        }

        protected static Label Note(string text)
        {
            var note = new Label(text);
            note.AddToClassList("bk-note");
            return note;
        }

        protected static VisualElement Card()
        {
            var card = new VisualElement();
            card.AddToClassList("bk-panel");
            return card;
        }

        protected static VisualElement Row()
        {
            return new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 2 } };
        }

        protected TextField Text(string label, string value, Action<string> set, bool multiline = false)
        {
            var field = new TextField(label) { value = value ?? string.Empty, multiline = multiline, isDelayed = true };
            field.style.flexGrow = 1;
            if (multiline) field.style.minHeight = 46;
            field.RegisterValueChangedCallback(evt => Change(() => set(evt.newValue)));
            return field;
        }

        protected FloatField Price(string label, float value, Action<float> set)
        {
            var field = new FloatField(label) { value = value, isDelayed = true };
            field.RegisterValueChangedCallback(evt =>
            {
                float clean = Mathf.Max(0f, (float)Math.Round(evt.newValue, 2));
                Change(() => set(clean));
                field.SetValueWithoutNotify(clean);
            });
            return field;
        }

        protected Button Small(string text, Action onClick)
        {
            return new Button(onClick) { text = text, style = { marginLeft = 4 } };
        }

        protected static string NewId(string prefix)
        {
            return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }
    }

    /// <summary>
    /// Optional credits: custom role titles, each with people under it. A ParelVR account is picked by searching
    /// for the user and clicking them, so a credit always points at a real account; anyone else gets a checked
    /// profile link.
    /// </summary>
    public class WorldCreditsTab : WorldPageTabBase
    {
        public override string TabName => "Credits";
        public override int TabOrder => 20;

        [Serializable] private sealed class UserResult { public string playFabId; public string username; public string displayName; public string avatarUrl; }
        [Serializable] private sealed class SearchResponse { public bool success; public string message; public UserResult[] results; }

        private const int MinimumSearchLength = 2;
        private const int SearchDelayMs = 350;
        private const int MaximumResults = 8;
        private int _searchVersion;

        protected override void Build(VisualElement root)
        {
            root.Add(Note("Credit the people who helped make this world. Give each role your own title, for example \"Developer of UI\", then add people to it. " +
                          "Search for a ParelVR account and click it to add it; players can open that person's profile from the credit, and they do not have to be your friend. " +
                          "Someone without an account can be credited with a link to their profile on another platform."));

            List<ParelWorldCreditRole> roles = Descriptor.credits;
            for (int r = 0; r < roles.Count; r++)
            {
                ParelWorldCreditRole role = roles[r];
                VisualElement card = Card();

                VisualElement head = Row();
                head.Add(Text("Title", role.title, v => role.title = v));
                head.Add(Small("Remove role", () => Change(() => roles.Remove(role), true)));
                card.Add(head);

                foreach (ParelWorldCreditPerson person in role.people.ToArray())
                {
                    card.Add(PersonRow(role, person));
                }

                VisualElement add = Row();
                add.style.marginTop = 8;
                add.Add(new Button(() => Change(() => role.people.Add(new ParelWorldCreditPerson { kind = ParelCreditKind.ParelVRAccount }), true)) { text = "+ Add ParelVR account" });
                add.Add(Small("+ Add external link", () => Change(() => role.people.Add(new ParelWorldCreditPerson { kind = ParelCreditKind.ExternalLink }), true)));
                card.Add(add);
                root.Add(card);
            }

            root.Add(new Button(() => Change(() => roles.Add(new ParelWorldCreditRole { title = "Role" }), true)) { text = "+ Add credit role", style = { alignSelf = Align.FlexStart } });
        }

        private VisualElement PersonRow(ParelWorldCreditRole role, ParelWorldCreditPerson person)
        {
            var box = new VisualElement();
            box.AddToClassList("bk-person");

            if (person.kind == ParelCreditKind.ParelVRAccount)
            {
                if (string.IsNullOrWhiteSpace(person.username)) BuildAccountSearch(box, role, person);
                else BuildAccountChip(box, role, person);
                return box;
            }

            VisualElement top = Row();
            top.Add(Text("Name", person.displayName, v => person.displayName = v.Trim()));
            top.Add(Small("Remove", () => Change(() => role.people.Remove(person), true)));
            box.Add(top);

            var status = new Label { style = { fontSize = 11, whiteSpace = WhiteSpace.Normal, marginTop = 2 } };
            Action<ParelCreditLinkValidator.Result> show = result =>
            {
                status.text = string.IsNullOrWhiteSpace(person.url) ? "Paste a link to their profile." : result.message;
                status.EnableInClassList("bk-text-good", result.ok);
                status.EnableInClassList("bk-text-bad", !result.ok);
            };

            VisualElement linkRow = Row();
            TextField link = Text("Profile link", person.url, v =>
            {
                // A link that passes is stored in its cleaned form (no tracking tail).
                ParelCreditLinkValidator.Result checkedNow = ParelCreditLinkValidator.Check(v);
                person.url = checkedNow.ok ? checkedNow.url : v.Trim();
                show(checkedNow);
            });
            linkRow.Add(link);
            linkRow.Add(Small("Check link", async () =>
            {
                status.text = "Checking...";
                status.RemoveFromClassList("bk-text-good");
                status.RemoveFromClassList("bk-text-bad");
                ParelCreditLinkValidator.Result live = await ParelCreditLinkValidator.ResolveAsync(person.url);
                show(live);
            }));
            box.Add(linkRow);
            box.Add(status);
            show(ParelCreditLinkValidator.Check(person.url));
            return box;
        }

        // ---- a ParelVR account that has been picked ---------------------------------------------

        private void BuildAccountChip(VisualElement box, ParelWorldCreditRole role, ParelWorldCreditPerson person)
        {
            VisualElement row = Row();
            row.Add(Avatar(null, string.IsNullOrWhiteSpace(person.displayName) ? person.username : person.displayName));
            row.Add(Names(person.displayName, person.username));
            row.Add(new VisualElement { style = { flexGrow = 1 } });
            row.Add(Small("Change", () => Change(() => { person.username = string.Empty; person.displayName = string.Empty; }, true)));
            row.Add(Small("Remove", () => Change(() => role.people.Remove(person), true)));
            box.Add(row);
        }

        // ---- searching for the account to credit ------------------------------------------------

        private void BuildAccountSearch(VisualElement box, ParelWorldCreditRole role, ParelWorldCreditPerson person)
        {
            VisualElement row = Row();
            var search = new TextField { style = { flexGrow = 1 } };
            search.AddToClassList("bk-search");
            search.textEdition.placeholder = "Search ParelVR users by name or username";
            row.Add(search);
            row.Add(Small("Remove", () => Change(() => role.people.Remove(person), true)));
            box.Add(row);

            var status = new Label("Type at least " + MinimumSearchLength + " characters, then click the person to credit.");
            status.AddToClassList("bk-note");
            status.style.marginTop = 4;
            box.Add(status);

            var results = new VisualElement();
            box.Add(results);

            IVisualElementScheduledItem pending = null;
            search.RegisterValueChangedCallback(evt =>
            {
                string query = (evt.newValue ?? string.Empty).Trim().TrimStart('@');
                pending?.Pause();
                int version = ++_searchVersion;
                if (query.Length < MinimumSearchLength)
                {
                    results.Clear();
                    status.text = "Type at least " + MinimumSearchLength + " characters, then click the person to credit.";
                    return;
                }
                status.text = "Searching...";
                pending = search.schedule.Execute(() => Search(query, version, role, person, results, status)).StartingIn(SearchDelayMs);
            });
            search.schedule.Execute(() => search.Focus()).StartingIn(50);
        }

        private async void Search(string query, int version, ParelWorldCreditRole role, ParelWorldCreditPerson person, VisualElement results, Label status)
        {
            var found = new List<UserResult>();
            try
            {
                SearchResponse response = await ParelApiClient.GetJsonAsync<SearchResponse>("/api/parelvr/users/search?q=" + Uri.EscapeDataString(query));
                if (version != _searchVersion) return; // a newer search has started since
                if (response?.results != null) found.AddRange(response.results);
            }
            catch (Exception e)
            {
                if (version != _searchVersion) return;
                results.Clear();
                status.text = "Search failed: " + e.Message;
                return;
            }

            // The search leaves the signed-in account out of its own results; crediting yourself is the common case.
            bool selfMatches = Contains(ParelSession.Username, query) || Contains(ParelSession.DisplayName, query);
            if (selfMatches && !string.IsNullOrEmpty(ParelSession.Username) && !found.Exists(u => Same(u.username, ParelSession.Username)))
                found.Insert(0, new UserResult { playFabId = ParelSession.UserId, username = ParelSession.Username, displayName = ParelSession.DisplayName });

            found.RemoveAll(u => u == null || string.IsNullOrWhiteSpace(u.username));
            results.Clear();
            if (found.Count == 0)
            {
                status.text = "No ParelVR account matches \"" + query + "\".";
                return;
            }

            status.text = found.Count > MaximumResults ? "Showing the first " + MaximumResults + " matches. Keep typing to narrow it down." : "Click the person to credit.";
            for (int i = 0; i < found.Count && i < MaximumResults; i++)
            {
                UserResult user = found[i];
                bool already = role.people.Exists(p => p != person && p.kind == ParelCreditKind.ParelVRAccount && Same(p.username, user.username));

                VisualElement item = Row();
                item.AddToClassList("bk-search-result");
                item.Add(Avatar(user.avatarUrl, string.IsNullOrWhiteSpace(user.displayName) ? user.username : user.displayName));
                item.Add(Names(user.displayName, user.username));
                item.Add(new VisualElement { style = { flexGrow = 1 } });
                if (Same(user.username, ParelSession.Username)) item.Add(Pill("You"));
                if (already)
                {
                    item.Add(Pill("Already credited here"));
                    item.SetEnabled(false);
                }
                else
                {
                    item.RegisterCallback<ClickEvent>(_ => Change(() =>
                    {
                        person.username = user.username.Trim().TrimStart('@');
                        person.displayName = (user.displayName ?? string.Empty).Trim();
                    }, true));
                }
                results.Add(item);
            }
        }

        private static bool Contains(string text, string part) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool Same(string a, string b) =>
            string.Equals((a ?? string.Empty).Trim().TrimStart('@'), (b ?? string.Empty).Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase);

        private static VisualElement Names(string displayName, string username)
        {
            var names = new VisualElement { style = { flexShrink = 1 } };
            var name = new Label(string.IsNullOrWhiteSpace(displayName) ? username : displayName);
            name.AddToClassList("bk-person-name");
            names.Add(name);
            var handle = new Label("@" + username);
            handle.AddToClassList("bk-person-handle");
            names.Add(handle);
            return names;
        }

        private static Label Pill(string text)
        {
            var pill = new Label(text);
            pill.AddToClassList("bk-pill");
            pill.AddToClassList("bk-pill-neutral");
            pill.style.marginLeft = 6;
            return pill;
        }

        /// <summary>The account's picture, or its initial until the picture arrives or when there is none.</summary>
        private static VisualElement Avatar(string url, string name)
        {
            var avatar = new VisualElement();
            avatar.AddToClassList("bk-avatar");
            var initial = new Label(string.IsNullOrWhiteSpace(name) ? "?" : name.Trim().Substring(0, 1).ToUpperInvariant());
            initial.AddToClassList("bk-avatar-initial");
            avatar.Add(initial);
            if (string.IsNullOrWhiteSpace(url)) return avatar;

            Action<Texture2D> show = texture =>
            {
                if (texture == null) return;
                avatar.style.backgroundImage = new StyleBackground(texture);
                initial.style.display = DisplayStyle.None;
            };
            show(ParelTextureCache.GetOrFetch(url, show));
            return avatar;
        }
    }

    /// <summary>Monetization: the currency, then Create Store, Create Subscription / Tiers and Create In-game Product.</summary>
    public class WorldMonetizationTab : WorldPageTabBase
    {
        public override string TabName => "Monetization";
        public override int TabOrder => 30;

        private static readonly List<string> Currencies = new List<string>
        {
            "USD", "EUR", "GBP", "CAD", "AUD", "NZD", "JPY", "KRW", "CNY", "INR", "BRL", "MXN", "CHF", "SEK", "NOK", "DKK", "PLN", "CZK",
            "HUF", "TRY", "ZAR", "SGD", "HKD", "TWD", "THB", "PHP", "IDR", "MYR", "AED", "SAR", "ILS", "ARS", "CLP", "COP",
        };

        protected override void Build(VisualElement root)
        {
            ParelWorldMonetization money = Descriptor.monetization;

            if (!Currencies.Contains(money.currency)) money.currency = "USD";
            var currency = new PopupField<string>("Your currency", Currencies, money.currency);
            currency.RegisterValueChangedCallback(evt => Change(() => money.currency = evt.newValue, true));
            root.Add(currency);
            root.Add(Note("The backend automatically calculates the prices for other currencies."));

            BuildStore(root, money);
            BuildTiers(root, money);
            BuildInGame(root, money);
        }

        // ---- Create Store ---------------------------------------------------------------------

        private void BuildStore(VisualElement root, ParelWorldMonetization money)
        {
            root.Add(Heading("Create Store"));
            root.Add(Note("A Shop tab on your world's page in the menu."));

            VisualElement card = Card();
            var enabled = new Toggle("Show a store on this world") { value = money.storeEnabled };
            enabled.RegisterValueChangedCallback(evt => Change(() => money.storeEnabled = evt.newValue, true));
            card.Add(enabled);

            if (money.storeEnabled)
            {
                card.Add(Text("Store name", money.storeName, v => money.storeName = v));
                card.Add(Text("Tagline", money.storeTagline, v => money.storeTagline = v));

                card.Add(new Label("Theme") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 8 } });
                ParelWorldShopTheme theme = Descriptor.shopTheme;
                card.Add(Colour("Highlight", theme.highlight, v => theme.highlight = v));
                card.Add(Colour("Background colour", theme.background, v => theme.background = v));
                var image = new ObjectField("Background image") { objectType = typeof(Texture2D), allowSceneObjects = false, value = theme.backgroundImage };
                image.RegisterValueChangedCallback(evt => Change(() => theme.backgroundImage = evt.newValue as Texture2D));
                card.Add(image);
                card.Add(Note("Optional. When an image is set it is shown instead of the background colour."));
                card.Add(Colour("Header text", theme.headerText, v => theme.headerText = v));
                card.Add(Colour("Sub text", theme.subText, v => theme.subText = v));
                card.Add(Colour("Text", theme.text, v => theme.text = v));
                card.Add(Colour("Card colour", theme.card, v => theme.card = v));

                card.Add(new Label("Products") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 8 } });
                foreach (ParelWorldStoreProduct product in money.products.ToArray())
                {
                    VisualElement item = Card();
                    VisualElement head = Row();
                    head.Add(Text("Name", product.name, v => product.name = v));
                    head.Add(Small("Remove", () => Change(() => money.products.Remove(product), true)));
                    item.Add(head);
                    item.Add(Text("Description", product.description, v => product.description = v, true));
                    item.Add(Price("Price (" + money.currency + ")", product.price, v => product.price = v));
                    var picture = new ObjectField("Picture") { objectType = typeof(Texture2D), allowSceneObjects = false, value = product.image };
                    picture.RegisterValueChangedCallback(evt => Change(() => product.image = evt.newValue as Texture2D));
                    item.Add(picture);
                    card.Add(item);
                }

                card.Add(new Button(() => Change(() => money.products.Add(new ParelWorldStoreProduct { id = NewId("item"), name = "New product" }), true))
                    { text = "+ Add product", style = { alignSelf = Align.FlexStart } });
            }

            root.Add(card);
        }

        // ---- Create Subscription / Tiers ------------------------------------------------------

        private void BuildTiers(VisualElement root, ParelWorldMonetization money)
        {
            root.Add(Heading("Create Subscription / Tiers"));
            root.Add(Note("Monthly support tiers for this world, each with its own price and perks."));

            foreach (ParelWorldSubscriptionTier tier in money.tiers.ToArray())
            {
                VisualElement card = Card();
                VisualElement head = Row();
                head.Add(Text("Tier name", tier.name, v => tier.name = v));
                head.Add(Small("Remove", () => Change(() => money.tiers.Remove(tier), true)));
                card.Add(head);
                card.Add(Text("Description", tier.description, v => tier.description = v, true));
                card.Add(Price("Price per month (" + money.currency + ")", tier.pricePerMonth, v => tier.pricePerMonth = v));
                card.Add(Text("Perks (one per line)", tier.perks, v => tier.perks = v, true));
                root.Add(card);
            }

            root.Add(new Button(() => Change(() => money.tiers.Add(new ParelWorldSubscriptionTier { id = NewId("tier"), name = "New tier" }), true))
                { text = "+ Add tier", style = { alignSelf = Align.FlexStart } });
        }

        // ---- Create In-game Product -----------------------------------------------------------

        private void BuildInGame(VisualElement root, ParelWorldMonetization money)
        {
            root.Add(Heading("Create In-game Product"));
            root.Add(Note("Something players buy inside the world, like the ability to fly. Your world script decides what a purchase does:\n\n" +
                          "    if (VoltEconomy.Owns(\"fly_pass\")) { /* let them fly */ }\n\n" +
                          "Transaction_Successful is true once the player's purchase of that product went through and false if it failed or they never bought it. " +
                          "VoltEconomy.Purchase(\"fly_pass\") asks the player to buy it."));

            foreach (ParelWorldInGameProduct product in money.inGameProducts.ToArray())
            {
                VisualElement card = Card();
                VisualElement head = Row();
                head.Add(Text("Name", product.name, v => product.name = v));
                head.Add(Small("Remove", () => Change(() => money.inGameProducts.Remove(product), true)));
                card.Add(head);
                card.Add(Text("Script id", product.id, v => product.id = CleanId(v)));
                card.Add(Text("Description", product.description, v => product.description = v, true));
                card.Add(Price("Price (" + money.currency + ")", product.price, v => product.price = v));
                var consumable = new Toggle("Can be bought again (consumable)") { value = product.consumable };
                consumable.RegisterValueChangedCallback(evt => Change(() => product.consumable = evt.newValue));
                card.Add(consumable);
                root.Add(card);
            }

            root.Add(new Button(() => Change(() => money.inGameProducts.Add(new ParelWorldInGameProduct { id = NewId("product"), name = "New in-game product" }), true))
                { text = "+ Add in-game product", style = { alignSelf = Align.FlexStart } });

            root.Add(Note("\nPrices are saved with your world when you publish. Purchases switch on once ParelVR payments are live."));
        }

        private ColorField Colour(string label, Color value, Action<Color> set)
        {
            var field = new ColorField(label) { value = value, showAlpha = false };
            field.RegisterValueChangedCallback(evt => Change(() => set(evt.newValue)));
            return field;
        }

        /// <summary>Script ids are lower-case letters, digits and underscores, so they are easy to type in code.</summary>
        private static string CleanId(string value)
        {
            var clean = new System.Text.StringBuilder();
            foreach (char c in (value ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture))
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_') clean.Append(c);
                else if (c == ' ' || c == '-') clean.Append('_');
            }

            return clean.Length > 0 ? clean.ToString() : NewId("product");
        }
    }
}
