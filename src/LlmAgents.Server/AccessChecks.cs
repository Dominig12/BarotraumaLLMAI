// The game is compiled against publicized reference DLLs (internal members made public).
// At runtime the real assemblies still mark them internal, so we ask the runtime to skip
// access checks for them. CoreCLR honours this attribute by name; it is not in the BCL.
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("DedicatedServer")]
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
