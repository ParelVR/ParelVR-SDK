using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace ParelVR.Rendering.Tests
{
    /// <summary>Runs the PVRP EditMode tests from a menu and writes the results to Logs/PVRP/EditModeTests.txt.</summary>
    public static class PvrpTestMenu
    {
        public const string ResultPath = "Logs/PVRP/EditModeTests.txt";

        /// <summary>
        /// Runs every [Test] / [TestCase] of this assembly directly, in this call. Unlike the Test Runner it never asks
        /// to save modified scenes and never reloads them, so it is safe while someone has unsaved work open.
        /// </summary>
        [MenuItem("ParelVR/Rendering/Tests/Run EditMode Tests")]
        public static void RunDirect()
        {
            int passed = 0, failed = 0, skipped = 0;
            var failures = new StringBuilder();
            Assembly tests = typeof(PvrpTestMenu).Assembly;
            const BindingFlags Public = BindingFlags.Instance | BindingFlags.Public;

            foreach (Type type in tests.GetTypes().Where(t => t.IsClass && !t.IsAbstract).OrderBy(t => t.FullName))
            {
                MethodInfo[] methods = type.GetMethods(Public).OrderBy(m => m.MetadataToken).ToArray();
                MethodInfo[] setUps = methods.Where(m => m.IsDefined(typeof(SetUpAttribute), true)).ToArray();
                MethodInfo[] tearDowns = methods.Where(m => m.IsDefined(typeof(TearDownAttribute), true)).ToArray();

                foreach (MethodInfo method in methods)
                {
                    object[] cases = method.GetCustomAttributes(typeof(TestCaseAttribute), false);
                    if (!method.IsDefined(typeof(TestAttribute), false) && cases.Length == 0) continue;
                    object[][] argumentSets = cases.Length == 0
                        ? new[] { new object[0] }
                        : cases.Cast<TestCaseAttribute>().Select(c => c.Arguments).ToArray();

                    foreach (object[] arguments in argumentSets)
                    {
                        string label = type.Name + "." + method.Name +
                                       (arguments.Length > 0 ? "(" + string.Join(", ", arguments.Select(a => a?.ToString() ?? "null")) + ")" : "");
                        object instance = Activator.CreateInstance(type);
                        Exception error = null;
                        try
                        {
                            foreach (MethodInfo s in setUps) s.Invoke(instance, null);
                            method.Invoke(instance, Convert(arguments, method.GetParameters()));
                        }
                        catch (TargetInvocationException e) { error = e.InnerException ?? e; }
                        finally
                        {
                            foreach (MethodInfo t in tearDowns)
                            {
                                try { t.Invoke(instance, null); }
                                catch (TargetInvocationException e) { error ??= e.InnerException ?? e; }
                            }
                        }

                        if (error == null || error is SuccessException) passed++;
                        else if (error is InconclusiveException || error is IgnoreException)
                        {
                            skipped++;
                            failures.Append("SKIP ").Append(label).Append(": ").AppendLine(error.Message.Trim());
                        }
                        else
                        {
                            failed++;
                            failures.Append("FAIL ").Append(label).Append(": ").AppendLine(error.GetType().Name + ": " + error.Message.Trim());
                        }
                    }
                }
            }

            string summary = $"PVRP EditMode tests: {passed} passed, {failed} failed, {skipped} skipped";
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, summary + "\n" + failures);
            Debug.Log(RenderingLog.Prefix + summary);
        }

        /// <summary>The same tests through Unity's Test Runner. It asks to save modified scenes first.</summary>
        [MenuItem("ParelVR/Rendering/Tests/Run EditMode Tests (Test Runner)")]
        public static void Run()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, "running\n");
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new Callbacks(api));
            api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, assemblyNames = new[] { "ParelVR.Rendering.Tests" } }));
        }

        private static object[] Convert(object[] arguments, ParameterInfo[] parameters)
        {
            var result = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                object value = i < arguments.Length ? arguments[i] : null;
                Type target = parameters[i].ParameterType;
                if (value != null && target.IsEnum && !value.GetType().IsEnum) value = Enum.ToObject(target, value);
                else if (value != null && !target.IsInstanceOfType(value) && value is IConvertible) value = System.Convert.ChangeType(value, target);
                result[i] = value;
            }
            return result;
        }

        private sealed class Callbacks : ICallbacks
        {
            private readonly TestRunnerApi _api;
            private readonly StringBuilder _failures = new StringBuilder();

            public Callbacks(TestRunnerApi api) { _api = api; }

            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.HasChildren) return;
                if (result.TestStatus == TestStatus.Failed)
                    _failures.Append("FAIL ").Append(result.FullName).Append(": ").AppendLine(result.Message?.Trim());
                else if (result.TestStatus == TestStatus.Skipped || result.TestStatus == TestStatus.Inconclusive)
                    _failures.Append("SKIP ").Append(result.FullName).Append(": ").AppendLine(result.Message?.Trim());
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                string summary = $"PVRP EditMode tests: {result.PassCount} passed, {result.FailCount} failed, {result.SkipCount + result.InconclusiveCount} skipped";
                File.WriteAllText(ResultPath, summary + "\n" + _failures);
                Debug.Log(RenderingLog.Prefix + summary);
                _api.UnregisterCallbacks(this);
            }
        }
    }
}
