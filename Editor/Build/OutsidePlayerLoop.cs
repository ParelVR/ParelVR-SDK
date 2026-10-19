using System;
using System.Threading.Tasks;
using UnityEditor;

namespace ParelVR.SDK.Build
{
    /// <summary>
    /// Runs a bundle build from the editor's delayed calls instead of an await continuation. An await resumes while
    /// Unity is running its queued async work inside the player loop, and BuildAssetBundles from there fails with
    /// "An abnormal situation has occurred: the PlayerLoop internal function has been called recursively".
    /// </summary>
    public static class OutsidePlayerLoop
    {
        public static Task<T> Run<T>(Func<T> build)
        {
            var done = new TaskCompletionSource<T>();
            EditorApplication.delayCall += () =>
            {
                try { done.SetResult(build()); }
                catch (Exception e) { done.SetException(e); }
            };
            return done.Task;
        }
    }
}
