// Minimal NUnit runner for the engine-free PVRP Core tests, so they can run without the Unity Editor.
// Supports [Test], [TestCase(...)], Assert/Assume/StringAssert/CollectionAssert. Built and run by run-core-tests.sh.
using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

internal static class CoreTestRunner
{
    private static int Main(string[] args)
    {
        Assembly tests = Assembly.LoadFrom(args.Length > 0 ? args[0] : "ParelVR.Rendering.Core.Tests.dll");
        int passed = 0, failed = 0, skipped = 0;

        foreach (Type type in tests.GetTypes().Where(t => t.IsClass && !t.IsAbstract).OrderBy(t => t.FullName))
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public).OrderBy(m => m.MetadataToken))
            {
                object[] cases = method.GetCustomAttributes(typeof(TestCaseAttribute), false);
                bool isTest = method.GetCustomAttributes(typeof(TestAttribute), false).Length > 0;
                if (!isTest && cases.Length == 0) continue;

                var argumentSets = cases.Length == 0
                    ? new[] { new object[0] }
                    : cases.Cast<TestCaseAttribute>().Select(c => c.Arguments).ToArray();

                foreach (object[] arguments in argumentSets)
                {
                    string label = type.Name + "." + method.Name + (arguments.Length > 0 ? "(" + string.Join(", ", arguments.Select(a => a?.ToString() ?? "null")) + ")" : "");
                    try
                    {
                        object instance = Activator.CreateInstance(type);
                        method.Invoke(instance, Convert(arguments, method.GetParameters()));
                        passed++;
                    }
                    catch (TargetInvocationException e) when (e.InnerException is InconclusiveException || e.InnerException is IgnoreException)
                    {
                        skipped++;
                        Console.WriteLine("SKIP " + label + ": " + e.InnerException.Message.Trim());
                    }
                    catch (TargetInvocationException e) when (e.InnerException is SuccessException)
                    {
                        passed++;
                    }
                    catch (TargetInvocationException e)
                    {
                        failed++;
                        Console.WriteLine("FAIL " + label + ": " + e.InnerException?.Message.Trim());
                    }
                }
            }
        }

        Console.WriteLine($"PVRP Core tests: {passed} passed, {failed} failed, {skipped} skipped");
        return failed == 0 ? 0 : 1;
    }

    private static object[] Convert(object[] arguments, ParameterInfo[] parameters)
    {
        var result = new object[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            object value = i < arguments.Length ? arguments[i] : null;
            Type target = parameters[i].ParameterType;
            if (value != null && target.IsEnum && !(value.GetType().IsEnum)) value = Enum.ToObject(target, value);
            else if (value != null && !target.IsInstanceOfType(value) && value is IConvertible) value = System.Convert.ChangeType(value, target);
            result[i] = value;
        }
        return result;
    }
}
