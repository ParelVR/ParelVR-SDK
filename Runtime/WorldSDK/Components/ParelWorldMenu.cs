using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>What a control of a world menu is.</summary>
    public enum ParelWorldMenuControlType
    {
        /// <summary>A button: OnWorldMenuButton(id) for scripts; can switch objects on and off.</summary>
        Button = 0,
        /// <summary>On or off: OnWorldMenuToggle(id, on); can show objects while on (or off).</summary>
        Toggle = 1,
        /// <summary>A value between a minimum and a maximum: OnWorldMenuSlider(id, value); can set sounds' volume.</summary>
        Slider = 2,
        /// <summary>One of several choices, stepped through with arrows: OnWorldMenuOption(id, index); can show one object per choice.</summary>
        Options = 3,
        /// <summary>Opens another page of the menu.</summary>
        PageLink = 4,
        /// <summary>A line of text, for headings and notes.</summary>
        Label = 5
    }

    /// <summary>One control on a page of a world menu.</summary>
    [Serializable]
    public sealed class ParelWorldMenuControl
    {
        [Tooltip("Scripts name the control by this (VoltWorldMenu, OnWorldMenu… events). Unique in the menu.")]
        public string id = "control";
        public ParelWorldMenuControlType type = ParelWorldMenuControlType.Toggle;
        [Tooltip("The control's name in the menu.")]
        public string label = "Control";
        [Tooltip("A short line under the name. Optional.")]
        public string description = string.Empty;
        [Tooltip("A small picture beside the name. Optional.")]
        public Texture2D icon;

        [Tooltip("Toggle: on when the world loads.")]
        public bool startOn;
        [Tooltip("Toggle: switched on while the toggle is on, off while it is off. Button: each press switches them on or off.")]
        public GameObject[] objects = new GameObject[0];
        [Tooltip("Toggle: switched on while the toggle is OFF.")]
        public GameObject[] objectsWhenOff = new GameObject[0];

        [Tooltip("Slider: the smallest value.")]
        public float min;
        [Tooltip("Slider: the largest value.")]
        public float max = 1f;
        [Tooltip("Slider: the value when the world loads.")]
        public float startValue = 0.5f;
        [Tooltip("Slider: whole numbers only.")]
        public bool wholeNumbers;
        [Tooltip("Slider: shown after the value, such as % or m.")]
        public string unit = string.Empty;
        [Tooltip("Slider: shows the value from 0 to 100 (the slider's place between min and max) instead of the value itself.")]
        public bool showAsPercent = true;
        [Tooltip("Slider: these sounds play at the slider's volume (its place between min and max).")]
        public AudioSource[] volumeOf = new AudioSource[0];

        [Tooltip("Options: the choices, in order.")]
        public string[] options = { "Option 1", "Option 2" };
        [Tooltip("Options: the choice picked when the world loads (from 0).")]
        public int startOption;
        [Tooltip("Options: one object per choice; only the picked choice's object is on. Optional.")]
        public GameObject[] objectPerOption = new GameObject[0];

        [Tooltip("Page link: the id of the page it opens.")]
        public string page = string.Empty;
    }

    /// <summary>One page of a world menu.</summary>
    [Serializable]
    public sealed class ParelWorldMenuPage
    {
        [Tooltip("Page links and VoltWorldMenu.Open name the page by this. Unique in the menu.")]
        public string id = "home";
        [Tooltip("The page's name in the menu's list of pages.")]
        public string title = "Home";
        [Tooltip("A small picture beside the name. Optional.")]
        public Texture2D icon;
        [Tooltip("Hide the page from the list on the left; page links (and scripts) still open it.")]
        public bool hidden;
        public List<ParelWorldMenuControl> controls = new List<ParelWorldMenuControl>();
    }

    /// <summary>
    /// The world's own menu inside ParelVR's main menu: while players are in the world, its icon sits in the menu's top
    /// bar and opens a page with the menu's name, its pages down the left and their controls (buttons, toggles,
    /// sliders, option switchers, page links and text). Each player has their own values.
    ///
    /// Without code: toggles switch objects on and off, buttons flip objects, sliders set sounds' volume, option
    /// switchers show one object per choice. With Volt scripts: OnWorldMenuButton / Toggle / Slider / Option tell a
    /// script when a player uses a control, and VoltWorldMenu reads and sets values, names, visibility and the open page.
    /// One per world, anywhere in the scene.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/World SDK/World Menu")]
    public sealed class ParelWorldMenu : MonoBehaviour
    {
        public const int MaxPages = 16;
        public const int MaxControlsPerPage = 32;
        public const int MaxOptions = 32;
        public const int MaxText = 64;

        [Tooltip("The menu's name, shown on its page and when pointing at its icon.")]
        public string menuName = "World";
        [Tooltip("The menu's icon in ParelVR's top bar (square, white on transparent looks best). Optional.")]
        public Texture2D icon;
        [Tooltip("The first page opens with the menu.")]
        public List<ParelWorldMenuPage> pages = new List<ParelWorldMenuPage> { new ParelWorldMenuPage() };

        /// <summary>The page with this id, or null.</summary>
        public ParelWorldMenuPage FindPage(string id)
        {
            if (pages == null || string.IsNullOrEmpty(id)) return null;
            foreach (ParelWorldMenuPage p in pages)
                if (p != null && p.id == id) return p;
            return null;
        }

        /// <summary>The control with this id on any page, or null.</summary>
        public ParelWorldMenuControl FindControl(string id)
        {
            if (pages == null || string.IsNullOrEmpty(id)) return null;
            foreach (ParelWorldMenuPage p in pages)
            {
                if (p?.controls == null) continue;
                foreach (ParelWorldMenuControl c in p.controls)
                    if (c != null && c.id == id) return c;
            }
            return null;
        }

        /// <summary>The menu in the loaded scene, or null.</summary>
        public static ParelWorldMenu Find(UnityEngine.SceneManagement.Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                ParelWorldMenu menu = root.GetComponentInChildren<ParelWorldMenu>(true);
                if (menu != null) return menu;
            }
            return null;
        }
    }
}
