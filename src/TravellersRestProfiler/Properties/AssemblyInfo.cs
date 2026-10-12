using System.Reflection;
using System.Runtime.InteropServices;

// The project disables the SDK's generated assembly info, so the version has to be declared
// here rather than relying on the Version property in the project file. Without these the
// shipped DLL reports 0.0.0.0 in its file properties, which looks like an unfinished build to
// anyone who right-clicks it.
//
// These are the numbers a user sees, so they must match PluginVersion in Plugin.cs and the
// release tag. All three are 0.1.0.
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: AssemblyInformationalVersion("0.1.0")]

[assembly: AssemblyTitle("Travellers Rest Performance Diagnostic")]
[assembly: AssemblyDescription("Measures loading, freezes and memory in Travellers Rest and writes a report.")]
[assembly: AssemblyProduct("Travellers Rest Performance Diagnostic")]
[assembly: AssemblyCompany("AcTePuKc")]
[assembly: AssemblyCopyright("Copyright © AcTePuKc")]
[assembly: ComVisible(false)]
[assembly: Guid("8f3a1c62-5d47-4b19-9a2e-6c1f7d0b3e54")]
