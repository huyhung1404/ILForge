using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ILForge;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;
using Unity.ILForge.CodeGen.Helpers;

namespace Unity.ILForge.CodeGen
{
    public class WiredWeaver : ILPostProcessor
    {
        private static HashSet<string> allowedAssemblies;
        private const string configPath = "Assets/Editor/WeaverAssemblies.txt";
        private static readonly Type serviceAttributeType = typeof(ServiceAttribute);
        private static readonly Type wiredAttributeType = typeof(WiredAttribute);
        private static readonly Type afterWiredAttributeType = typeof(AfterWiredAttribute);

        private class ServiceEntry
        {
            public TypeReference ParamType;
            public string FieldName;
        }

        private static void LoadAssemblyList()
        {
            if (allowedAssemblies != null) return;

            allowedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(configPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? string.Empty);
                File.WriteAllText(configPath, "Assembly-CSharp");
                return;
            }

            foreach (var line in File.ReadAllLines(configPath))
            {
                var name = line.Trim();
                if (!string.IsNullOrEmpty(name))
                    allowedAssemblies.Add(name);
            }
        }

        public override ILPostProcessor GetInstance() => this;

        public override bool WillProcess(ICompiledAssembly asm)
        {
            LoadAssemblyList();
            return allowedAssemblies.Contains(asm.Name);
        }

        public override ILPostProcessResult Process(ICompiledAssembly asm)
        {
            var diagnostics = new List<DiagnosticMessage>();
            var assembly = CodeGenHelpers.AssemblyDefinitionFor(asm);
            var module = assembly.MainModule;

            var services = CollectServices(module);

            GenerateCodeInAssembly(services, module);
            InjectServiceBindings(module);

            foreach (var type in module.Types) ProcessType(type, module);

            return CodeGenHelpers.GetResult(assembly, diagnostics);
        }

        private static List<ServiceEntry> CollectServices(ModuleDefinition module)
        {
            var services = new List<ServiceEntry>();

            foreach (var type in module.Types)
            {
                foreach (var method in type.Methods)
                {
                    var attr = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == serviceAttributeType.FullName);

                    if (attr == null) continue;

                    var scopeType = attr.ConstructorArguments.Count > 0
                        ? (TypeReference)attr.ConstructorArguments[0].Value
                        : null;

                    var scopeName = scopeType != null
                        ? scopeType.Name.Replace("Scope", "")
                        : "Global";

                    services.AddRange(from p in method.Parameters
                        let typeName = p.ParameterType.FullName.Replace(".", "_")
                            .Replace("/", "_")
                            .Replace("<", "_")
                            .Replace(">", "_")
                        select new ServiceEntry { ParamType = p.ParameterType, FieldName = $"{scopeName}_{typeName}" });
                }
            }

            return services;
        }

        private static void GenerateCodeInAssembly(List<ServiceEntry> services, ModuleDefinition module)
        {
            if (services.Count == 0) return;
            var codeGen = GetOrCreateCodeGenerate(module);
            foreach (var s in services) AddServiceField(codeGen, s.ParamType, s.FieldName, module);
        }

        private static void InjectServiceBindings(ModuleDefinition module)
        {
            var codeGen = module.Types.First(t => t.Name == "ILForge_Generate");

            foreach (var type in module.Types)
            {
                foreach (var method in type.Methods)
                {
                    if (method.CustomAttributes.All(a => a.AttributeType.FullName != serviceAttributeType.FullName)) continue;

                    if (!method.HasBody) continue;

                    var il = method.Body.GetILProcessor();
                    var first = method.Body.Instructions.First();

                    foreach (var p in method.Parameters)
                    {
                        var fieldName = BuildFieldNameFromTypeAndScope(p.ParameterType, method, module);
                        var holderField = codeGen.Fields.First(f => f.Name == fieldName);
                        il.InsertBefore(first, il.Create(OpCodes.Ldarg, p));
                        il.InsertBefore(first, il.Create(OpCodes.Stsfld, module.ImportReference(holderField)));
                    }
                }
            }
        }

        private static TypeDefinition GetOrCreateCodeGenerate(ModuleDefinition module)
        {
            var type = module.Types.FirstOrDefault(t => t.Name == "ILForge_Generate");
            if (type != null) return type;

            type = new TypeDefinition(
                "",
                "ILForge_Generate",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
                module.TypeSystem.Object);

            module.Types.Add(type);
            return type;
        }

        private static void AddServiceField(TypeDefinition codeGen, TypeReference paramType, string fieldName, ModuleDefinition module)
        {
            if (codeGen.Fields.Any(f => f.Name == fieldName)) return;

            var field = new FieldDefinition(
                fieldName,
                FieldAttributes.Public | FieldAttributes.Static,
                module.ImportReference(paramType));

            codeGen.Fields.Add(field);
        }

        public void ProcessType(TypeDefinition type, ModuleDefinition module)
        {
            var wiredFields = type.Fields
                .Where(f => f.CustomAttributes.Any(a => a.AttributeType.FullName == wiredAttributeType.FullName))
                .ToList();

            if (wiredFields.Count == 0) return;

            var afterMethods = type.Methods
                .Where(m => m.CustomAttributes.Any(a => a.AttributeType.FullName == afterWiredAttributeType.FullName))
                .ToList();

            if (afterMethods.Count > 0)
            {
                foreach (var method in afterMethods)
                    InjectFieldsIntoMethod(wiredFields, method, module);
            }
            else
            {
                var awake = GetOrCreateAwake(type, module);
                InjectFieldsIntoMethod(wiredFields, awake, module);
            }
        }

        private static void InjectFieldsIntoMethod(List<FieldDefinition> fields, MethodDefinition method, ModuleDefinition module)
        {
            var il = method.Body.GetILProcessor();
            var first = method.Body.Instructions.First();

            var codeGen = module.Types.First(t => t.Name == "ILForge_Generate");

            foreach (var field in fields)
            {
                var holderFieldName = BuildFieldNameFromField(field, module);
                var holderField = codeGen.Fields.First(f => f.Name == holderFieldName);

                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldsfld, module.ImportReference(holderField)));
                il.InsertBefore(first, il.Create(OpCodes.Stfld, field));
            }
        }

        private static string BuildFieldNameFromField(FieldDefinition field, ModuleDefinition module)
        {
            var wiredAttr = field.CustomAttributes
                .FirstOrDefault(a => a.AttributeType.FullName == wiredAttributeType.FullName);

            TypeReference scopeType = null;

            if (wiredAttr != null && wiredAttr.ConstructorArguments.Count > 0)
            {
                scopeType = wiredAttr.ConstructorArguments[0].Value as TypeReference;
            }

            scopeType ??= module.ImportReference(typeof(GlobalScope));

            var scopeName = scopeType.Name.Replace("Scope", "");

            var typeName = field.FieldType.FullName
                .Replace(".", "_")
                .Replace("/", "_")
                .Replace("<", "_")
                .Replace(">", "_");

            return $"{scopeName}_{typeName}";
        }

        private static string BuildFieldNameFromTypeAndScope(TypeReference paramType, MethodDefinition method, ModuleDefinition module)
        {
            var attr = method.CustomAttributes.First(a => a.AttributeType.FullName == serviceAttributeType.FullName);

            TypeReference scopeType = null;

            if (attr.ConstructorArguments.Count > 0) scopeType = attr.ConstructorArguments[0].Value as TypeReference;

            scopeType ??= module.ImportReference(typeof(GlobalScope));

            var scopeName = scopeType.Name.Replace("Scope", "");

            var typeName = paramType.FullName
                .Replace(".", "_")
                .Replace("/", "_")
                .Replace("<", "_")
                .Replace(">", "_");

            return $"{scopeName}_{typeName}";
        }

        private static MethodDefinition GetOrCreateAwake(TypeDefinition type, ModuleDefinition module)
        {
            var awake = type.Methods.FirstOrDefault(m => m.Name == "Awake");
            if (awake != null) return awake;

            awake = new MethodDefinition("Awake", MethodAttributes.Private, module.TypeSystem.Void);
            type.Methods.Add(awake);
            var il = awake.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ret));
            return awake;
        }
    }
}