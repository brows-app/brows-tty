Create a Git repository in this directory. The repository will contain the source code for a .NET NuGet package written in C# targeting .NET Framework 4.6.2 and .NET 10.0. The NuGet package will supply a class library with a WPF control that acts as a terminal emulator, allowing end users to type commands, run programs, and do all the things normally done with an operating system's terminal or command prompt.

This control will use the open-source library Ghostty to operate. That library will need to be built for x64 Windows and ARM64 Windows, because those are the supported platforms. Do not use existing .NET bindings for Ghostty from other NuGet packages or elsewhere. Build the bindings and do the p/invoke yourself, so that performance with WPF can be maximized. Use the most up-to-date version of the Ghostty library, and read the documentation carefully to implement the code correctly.

Pay attention to performance. WPF rendering should be smooth and responsive.

Use the cloned repository in F:\dev\me\brows-win32-previewhandler on this machine as an example .NET solution. Copy the AGENTS.md, .editorconfig, .gitattributes, and .gitignore from that directory. Update the AGENTS.md for this project, and follow the pertinent guidance. Do not modify any files in that directory: only read them as an example and for guidance. Pay attention to how Directory.Build.props and Directory.Packages.props files are used, and how the .slnx file organizes the projects in the solution. Use a similar namespace layout.

Use NUnit for unit tests and Moq for mocking, where necessary, but prefer automated integration tests when possible.

Work carefully and check your work as you go so as not to make any mistakes.

A native bridge between ghostty and .NET is a good idea. Do as much work as you can in that native bridge to improve performance. The majority of the code does not have to be C#. It just has to have a usable WPF control. Using another language that .NET can interop with is fine, if it improves performance. The solution in F:\dev\me\brows-private\dev\sln is an example of a .NET solution that has native helper projects.