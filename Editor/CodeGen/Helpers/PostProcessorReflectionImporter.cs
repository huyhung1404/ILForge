using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace Unity.ILForge.CodeGen.Helpers
{
    internal class PostProcessorReflectionImporter : DefaultReflectionImporter
    {
        private const string k_systemPrivateCoreLib = "System.Private.CoreLib";
        private readonly AssemblyNameReference _correctCorlib;

        public PostProcessorReflectionImporter(ModuleDefinition module) : base(module)
        {
            _correctCorlib = module.AssemblyReferences.FirstOrDefault(a => a.Name == "mscorlib" || a.Name == "netstandard" || a.Name == k_systemPrivateCoreLib);
        }

        public override AssemblyNameReference ImportReference(AssemblyName reference)
        {
            if (_correctCorlib != null && reference.Name == k_systemPrivateCoreLib)
                return _correctCorlib;

            return base.ImportReference(reference);
        }
    }
}
