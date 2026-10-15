// Compiled against publicized reference DLLs; at runtime the members are internal, so skip access checks
// for these assemblies (CoreCLR honours this attribute by name).
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Barotrauma")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("BarotraumaCore")]

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) => AssemblyName = assemblyName;
        public string AssemblyName { get; }
    }
}
