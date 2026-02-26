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
        private const string k_configPath = "Assets/Editor/WeaverAssemblies.txt";
        private static HashSet<string> _allowedAssemblies;
        private static readonly Type _serviceAttributeType = typeof(ServiceAttribute);
        private static readonly Type _wiredAttributeType = typeof(WiredAttribute);
        private static readonly Type _afterWiredAttributeType = typeof(AfterWiredAttribute);

        private class ServiceEntry
        {
            public TypeReference ParamType;
            public string FieldName;
        }

        private static void LoadAssemblyList()
        {
            if (_allowedAssemblies != null) return;

            _allowedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(k_configPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(k_configPath) ?? string.Empty);
                File.WriteAllText(k_configPath, "Assembly-CSharp");
                return;
            }

            foreach (var line in File.ReadAllLines(k_configPath))
            {
                var name = line.Trim();
                if (!string.IsNullOrEmpty(name))
                    _allowedAssemblies.Add(name);
            }
        }

        public override ILPostProcessor GetInstance() => this;

        public override bool WillProcess(ICompiledAssembly asm)
        {
            LoadAssemblyList();
            return _allowedAssemblies.Contains(asm.Name);
        }

        public override ILPostProcessResult Process(ICompiledAssembly asm)
        {
            var diagnostics = new List<DiagnosticMessage>();
            var assembly = CodeGenHelpers.AssemblyDefinitionFor(asm);
            var module = assembly.MainModule;

            var services = CollectServices(module);

            GenerateCodeInAssembly(services, module);
            InjectServiceBindings(module, diagnostics);

            foreach (var type in module.Types) ProcessType(type, module, diagnostics);

            return CodeGenHelpers.GetResult(assembly, diagnostics);
        }

        private static List<ServiceEntry> CollectServices(ModuleDefinition module)
        {
            var services = new List<ServiceEntry>();

            foreach (var type in module.Types)
            {
                foreach (var method in type.Methods)
                {
                    var attr = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == _serviceAttributeType.FullName);

                    if (attr == null) continue;

                    var scopeType = attr.ConstructorArguments.Count > 0
                        ? (TypeReference)attr.ConstructorArguments[0].Value
                        : null;

                    var scopeName = scopeType != null
                        ? scopeType.Name.Replace("Scope", "")
                        : "Global";

                    services.AddRange(from p in method.Parameters
                                      let typeName = p.ParameterType.FullName
                                          .Replace(".", "_")
                                          .Replace("/", "_")
                                          .Replace("<", "_")
                                          .Replace(">", "_")
                                          .Replace("`", "_")
                                          .Replace("[", "_")
                                          .Replace("]", "_")
                                          .Replace(",", "_")
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

        private static void InjectServiceBindings(ModuleDefinition module, List<DiagnosticMessage> diagnostics)
        {
            var codeGen = module.Types.FirstOrDefault(t => t.Name == "ILForge_Generate");
            if (codeGen == null) return;

            foreach (var type in module.Types)
            {
                foreach (var method in type.Methods)
                {
                    if (method.CustomAttributes.All(a => a.AttributeType.FullName != _serviceAttributeType.FullName)) continue;

                    if (!method.HasBody) continue;

                    var il = method.Body.GetILProcessor();
                    var first = method.Body.Instructions.First();

                    foreach (var p in method.Parameters)
                    {
                        var fieldName = BuildFieldNameFromTypeAndScope(p.ParameterType, method, module);
                        var holderField = codeGen.Fields.FirstOrDefault(f => f.Name == fieldName);
                        if (holderField == null)
                        {
                            diagnostics.AddError(method, $"Failed to inject service '{p.ParameterType.Name}' into '{method.Name}'. Field not found.");
                            continue;
                        }

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
                TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed,
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

        public void ProcessType(TypeDefinition type, ModuleDefinition module, List<DiagnosticMessage> diagnostics)
        {
            var wiredFields = type.Fields
                .Where(f => f.CustomAttributes.Any(a => a.AttributeType.FullName == _wiredAttributeType.FullName))
                .ToList();

            if (wiredFields.Count == 0) return;

            var initWiredMethod = GetOrCreateInitWired(type, module);
            InjectFieldsIntoMethod(wiredFields, initWiredMethod, module, diagnostics);

            var afterMethods = type.Methods
                .Where(m => m.CustomAttributes.Any(a => a.AttributeType.FullName == _afterWiredAttributeType.FullName))
                .ToList();

            if (afterMethods.Count > 0)
            {
                foreach (var method in afterMethods)
                {
                    InjectMethodCall(method, initWiredMethod, module);
                }
            }
            else
            {
                // Only inject into Awake if it's a MonoBehaviour. Otherwise, warn or just skip.
                if (IsMonoBehaviour(type))
                {
                    var awake = GetOrCreateAwake(type, module);
                    InjectMethodCall(awake, initWiredMethod, module);
                }
                else
                {
                    diagnostics.AddWarning(type.Methods.FirstOrDefault(), $"Class '{type.Name}' has [Wired] fields but is not a MonoBehaviour and has no [AfterWired] method. Dependencies will not be automatically injected.");
                }
            }
        }

        private bool IsMonoBehaviour(TypeDefinition type)
        {
            var baseType = type.BaseType;
            while (baseType != null)
            {
                if (baseType.FullName == "UnityEngine.MonoBehaviour") return true;

                var baseTypeDef = baseType.Resolve();
                if (baseTypeDef == null) break;
                baseType = baseTypeDef.BaseType;
            }
            return false;
        }

        private static void InjectMethodCall(MethodDefinition targetMethod, MethodDefinition methodToCall, ModuleDefinition module)
        {
            if (!targetMethod.HasBody) return;
            var il = targetMethod.Body.GetILProcessor();

            // To be totally safe with Evaluation Stack (esp in Constructors or MonoBehaviours where `base.Awake()` might be called),
            // We should ensure that we push 'this' (Ldarg_0) and Call our method *right at the beginning*, 
            // but if there are base constructor/method calls, doing it too early could sometimes corrupt the stack depending on the existing bytecode.
            // For standard Awake methods, inserting at First() is generally safe unless it's an auto-generated Awake from our own code
            // where we explicitly put base.Awake() first. 
            Instruction insertPoint = targetMethod.Body.Instructions.First();

            // If this Awake was just auto-generated by us and has a base.Awake call, we'll insert AFTER the base call (which is instruction 2, since 0 is ldarg.0 and 1 is Call base)
            // But actually, it's safer to just insert at First() for everything, BECAUSE `ILForge_InitWired` doesn't consume anything from stack except `this`, and returns void.
            il.InsertBefore(insertPoint, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(insertPoint, il.Create(OpCodes.Call, methodToCall));
        }

        private static MethodDefinition GetOrCreateInitWired(TypeDefinition type, ModuleDefinition module)
        {
            var initMethod = type.Methods.FirstOrDefault(m => m.Name == "ILForge_InitWired");
            if (initMethod != null) return initMethod;

            initMethod = new MethodDefinition("ILForge_InitWired", MethodAttributes.Private, module.TypeSystem.Void);
            type.Methods.Add(initMethod);
            var il = initMethod.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ret));
            return initMethod;
        }

        private static void InjectFieldsIntoMethod(List<FieldDefinition> fields, MethodDefinition method, ModuleDefinition module, List<DiagnosticMessage> diagnostics)
        {
            var codeGen = module.Types.FirstOrDefault(t => t.Name == "ILForge_Generate");
            if (codeGen == null)
            {
                diagnostics.AddError(method, $"Cannot inject [Wired] fields because no [Service] was found in the assembly.");
                return;
            }

            var il = method.Body.GetILProcessor();
            var first = method.Body.Instructions.First();

            foreach (var field in fields)
            {
                var holderFieldName = BuildFieldNameFromField(field, module);
                var holderField = codeGen.Fields.FirstOrDefault(f => f.Name == holderFieldName);
                if (holderField == null)
                {
                    diagnostics.AddError(method, $"Dependency not found for [Wired] field '{field.Name}' in '{method.DeclaringType.Name}'. Missing [Service].");
                    continue;
                }

                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldsfld, module.ImportReference(holderField)));
                il.InsertBefore(first, il.Create(OpCodes.Stfld, field));
            }
        }

        private static string BuildFieldNameFromField(FieldDefinition field, ModuleDefinition module)
        {
            var wiredAttr = field.CustomAttributes
                .FirstOrDefault(a => a.AttributeType.FullName == _wiredAttributeType.FullName);

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
                .Replace(">", "_")
                .Replace("`", "_")
                .Replace("[", "_")
                .Replace("]", "_")
                .Replace(",", "_");

            return $"{scopeName}_{typeName}";
        }

        private static string BuildFieldNameFromTypeAndScope(TypeReference paramType, MethodDefinition method, ModuleDefinition module)
        {
            var attr = method.CustomAttributes.First(a => a.AttributeType.FullName == _serviceAttributeType.FullName);

            TypeReference scopeType = null;

            if (attr.ConstructorArguments.Count > 0) scopeType = attr.ConstructorArguments[0].Value as TypeReference;

            scopeType ??= module.ImportReference(typeof(GlobalScope));

            var scopeName = scopeType.Name.Replace("Scope", "");

            var typeName = paramType.FullName
                .Replace(".", "_")
                .Replace("/", "_")
                .Replace("<", "_")
                .Replace(">", "_")
                .Replace("`", "_")
                .Replace("[", "_")
                .Replace("]", "_")
                .Replace(",", "_");

            return $"{scopeName}_{typeName}";
        }

        private static MethodDefinition GetOrCreateAwake(TypeDefinition type, ModuleDefinition module)
        {
            var awake = type.Methods.FirstOrDefault(m => m.Name == "Awake" && m.Parameters.Count == 0 && m.ReturnType.FullName == "System.Void");
            if (awake != null) return awake;

            // Notice we make it Virtual and Protected if the class is unsealed and part of a hierarchy, but Private is fine for Unity magic methods if we don't care about strict OOP visiblity.
            // Unity invokes Awake via reflection regardless of visibility, but making it Family (protected) or Private depends on base class.
            // For safety, we will just use Family (protected) so subclasses can override it if they want to later.
            awake = new MethodDefinition("Awake", MethodAttributes.Family | MethodAttributes.HideBySig, module.TypeSystem.Void);
            type.Methods.Add(awake);
            var il = awake.Body.GetILProcessor();

            // Try to find base.Awake() to call it (Looping up the hierarchy)
            var currentBaseType = type.BaseType;
            MethodDefinition baseAwake = null;

            while (currentBaseType != null)
            {
                var baseTypeDef = currentBaseType.Resolve();
                if (baseTypeDef == null) break;

                baseAwake = baseTypeDef.Methods.FirstOrDefault(m => m.Name == "Awake" && m.Parameters.Count == 0 && m.ReturnType.FullName == "System.Void");
                if (baseAwake != null)
                {
                    // If we found a base Awake, our new Awake must be marked virtual if the base is virtual, or we just call it.
                    if (baseAwake.IsVirtual)
                    {
                        awake.Attributes |= MethodAttributes.Virtual;
                    }
                    break;
                }

                if (baseTypeDef.FullName == "UnityEngine.MonoBehaviour") break;

                currentBaseType = baseTypeDef.BaseType;
            }

            var retInst = il.Create(OpCodes.Ret);

            if (baseAwake != null)
            {
                var importedBaseAwake = module.ImportReference(baseAwake);
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, importedBaseAwake)); // Call (not Callvirt) to strictly call the base implementation
            }

            il.Append(retInst);
            return awake;
        }
    }
}