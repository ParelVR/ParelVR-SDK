using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.SDK.Worlds.Components
{
    /// <summary>Who a credit points at.</summary>
    public enum ParelCreditKind
    {
        ParelVRAccount,
        ExternalLink,
    }

    /// <summary>One credited person: a ParelVR account (opens their profile in the menu) or a profile link on another platform.</summary>
    [Serializable]
    public class ParelWorldCreditPerson
    {
        public ParelCreditKind kind = ParelCreditKind.ParelVRAccount;
        [Tooltip("ParelVR username. They do not have to be your friend.")]
        public string username = string.Empty;
        [Tooltip("Name shown for an external link.")]
        public string displayName = string.Empty;
        [Tooltip("Their profile page on another platform.")]
        public string url = string.Empty;
    }

    /// <summary>A credit role with a custom title ("Developer of UI") and the people credited under it.</summary>
    [Serializable]
    public class ParelWorldCreditRole
    {
        public string title = string.Empty;
        public List<ParelWorldCreditPerson> people = new List<ParelWorldCreditPerson>();
    }

    /// <summary>Colours of the world's Shop page in the menu.</summary>
    [Serializable]
    public class ParelWorldShopTheme
    {
        public Color highlight = new Color32(0x60, 0x1F, 0xE5, 0xFF);
        public Color background = new Color32(0x0B, 0x0B, 0x0D, 0xFF);
        [Tooltip("Optional. Shown instead of the background colour.")]
        public Texture2D backgroundImage;
        public Color headerText = Color.white;
        public Color subText = new Color32(0xB9, 0xB9, 0xC2, 0xFF);
        public Color text = new Color32(0xE6, 0xE6, 0xEA, 0xFF);
        public Color card = new Color32(0x1C, 0x1C, 0x20, 0xFF);
    }

    [Serializable]
    public class ParelWorldStoreProduct
    {
        public string id = string.Empty;
        public string name = string.Empty;
        public string description = string.Empty;
        [Min(0f)] public float price;
        public Texture2D image;
    }

    [Serializable]
    public class ParelWorldSubscriptionTier
    {
        public string id = string.Empty;
        public string name = string.Empty;
        public string description = string.Empty;
        [Min(0f)] public float pricePerMonth;
        [Tooltip("One perk per line.")]
        public string perks = string.Empty;
    }

    /// <summary>
    /// Something bought inside the world. World scripts ask VoltEconomy.Owns(id)
    /// whether the player's purchase of it went through.
    /// </summary>
    [Serializable]
    public class ParelWorldInGameProduct
    {
        [Tooltip("What your scripts pass to VoltEconomy.Owns.")]
        public string id = string.Empty;
        public string name = string.Empty;
        public string description = string.Empty;
        [Min(0f)] public float price;
        [Tooltip("Can be bought again after it is used up.")]
        public bool consumable;
    }

    /// <summary>Everything a world sells. Prices are in <see cref="currency"/>; the backend converts them for other currencies.</summary>
    [Serializable]
    public class ParelWorldMonetization
    {
        public string currency = "USD";
        public bool storeEnabled;
        public string storeName = string.Empty;
        public string storeTagline = string.Empty;
        public List<ParelWorldStoreProduct> products = new List<ParelWorldStoreProduct>();
        public List<ParelWorldSubscriptionTier> tiers = new List<ParelWorldSubscriptionTier>();
        public List<ParelWorldInGameProduct> inGameProducts = new List<ParelWorldInGameProduct>();
    }
}
