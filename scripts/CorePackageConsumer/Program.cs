using System.Reflection;
using System.Text;

internal static class Program {
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;

    private static object Call(object instance, string name, params object[] arguments) {
        return instance.GetType().GetMethod(name, Members)!.Invoke(instance, arguments)!;
    }

    private static object Property(object instance, string name) {
        return instance.GetType().GetProperty(name, Members)!.GetValue(instance)!;
    }

    private static int Main(string[] arguments) {
        try {
            string assemblyName = arguments.Length == 0 ? "Brows.TTY" : "Brows.TTY.Win32";
            Assembly assembly = Assembly.Load(assemblyName);
            string[] references = assembly.GetReferencedAssemblies().Select(value => value.Name!).ToArray();
            bool hasUiDependency = references.Any(name => name.StartsWith("Presentation")
                || name == "WindowsBase" || name == "Brows.TTY.Windows");
            if (hasUiDependency) {
                throw new InvalidOperationException("Unexpected WPF dependency.");
            }
            if (assemblyName == "Brows.TTY") {
                if (references.Contains("Brows.TTY.Win32")) {
                    throw new InvalidOperationException("Core depends on the Windows transport.");
                }
                Type type = assembly.GetType("Brows.Ghostty.TerminalEngine", true)!;
                List<byte> output = new();
                using IDisposable engine = (IDisposable)Activator.CreateInstance(type, Members, null,
                    new object[] { (ushort)80, (ushort)24, new Action<byte[]>(bytes => output.AddRange(bytes)) },
                    null)!;
                byte[] input = Encoding.UTF8.GetBytes("\u001b]10;?\u0007\u001b]11;#0d1117\u0007core-package-ok");
                Call(engine, "Write", input, input.Length);
                object frame = Call(engine, "ReadFrame", true);
                object info = Property(frame, "Info");
                uint background = (uint)info.GetType().GetField("Background", Members | BindingFlags.Public)!
                    .GetValue(info)!;
                if (background != 0xff0d1117u) {
                    throw new InvalidOperationException("Core background failed.");
                }
                if (!Encoding.UTF8.GetString(output.ToArray()).Contains("10;rgb:ffff/ffff/ffff")) {
                    throw new InvalidOperationException("Core terminal query failed.");
                }
            }
            else {
                Type type = assembly.GetType("Brows.Win32.TerminalSession", true)!;
                string command = "\"" + Environment.GetEnvironmentVariable("ComSpec") + "\" /d /q /c exit /b 17";
                using IDisposable session = (IDisposable)Activator.CreateInstance(type, Members, null,
                    new object[] { command, null!, (ushort)80, (ushort)24 }, null)!;
                Task<uint> completion = (Task<uint>)Property(session, "Completion");
                if (!completion.Wait(TimeSpan.FromSeconds(15)) || completion.Result != 17) {
                    throw new InvalidOperationException("Transport process lifecycle failed.");
                }
            }
            Console.WriteLine(assemblyName + " package passed without WPF: " + Environment.Version);
            return 0;
        }
        catch (Exception exception) {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
