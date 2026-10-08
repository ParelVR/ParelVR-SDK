namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// What the SDK writes and the game expects inside an avatar bundle. A bundle holds exactly one
    /// prefab whose root carries a <see cref="ParelAvatarDescriptor"/>; everything it references
    /// (meshes, materials, the FX controller, the Expressions Menu/Parameters) rides along.
    /// </summary>
    public static class ParelAvatarBundleFormat
    {
        public const string SdkVersion = "1.0.0";

        /// <summary>Asset path of the avatar prefab inside the bundle (lower-case, as Unity stores it).</summary>
        public const string PrefabAssetName = "assets/parelvr_avatar_build/avatar.prefab";

        /// <summary>File extension of locally built test avatars ("Build &amp; Test").</summary>
        public const string TestBundleExtension = ".parelavatar";

        /// <summary>Folder (under the game's persistent data path) that "Build &amp; Test" writes into.</summary>
        public const string TestAvatarsFolderName = "SDKTestAvatars";

        /// <summary>The game's persistent data folder, which the SDK writes test avatars into.</summary>
        public const string CompanyName = "Parel LLC";
        public const string ProductName = "ParelVR";
    }
}
