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
        private const string k_configPath = "ProjectSettings/ILForge_CompilerSettings.txt";
        private const string k_codeGenClassName = "ILForge_Generate";

        private readonly Type _serviceAttributeType = typeof(ServiceAttribute);
        private readonly Type _wiredAttributeType = typeof(WiredAttribute);
        private readonly Type _afterWiredAttributeType = typeof(AfterWiredAttribute);
        private readonly Type _wiredRegisterAttributeType = typeof(WiredRegisterAttribute);

        private class ServiceEntry
        {
            public TypeReference ParamType;
            public string FieldName;
        }

        private class WiredTarget
        {
            public FieldDefinition BackingField;
            public TypeReference ScopeType;
            public string OriginalName;
        }

        public override ILPostProcessor GetInstance() => this;

        public override bool WillProcess(ICompiledAssembly asm)
        {
            if (!File.Exists(k_configPath)) return false;

            var lines = File.ReadAllLines(k_configPath);
            if (lines.Length == 0) return false;

            if (!bool.TryParse(lines[0], out var isEnabled) || !isEnabled) return false;

            for (var i = 1; i < lines.Length; i++)
            {
                if (string.Equals(lines[i], asm.Name, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        public override ILPostProcessResult Process(ICompiledAssembly asm)
        {
            var diagnostics = new List<DiagnosticMessage>();
            var assembly = CodeGenHelpers.AssemblyDefinitionFor(asm);
            var module = assembly.MainModule;

            var services = CollectServices(module, diagnostics);
            GenerateCodeInAssembly(services, module);
            InjectServiceBindings(module, diagnostics);

            var allTypes = new List<TypeDefinition>();
            foreach (var type in module.Types)
            {
                allTypes.Add(type);
                CollectNestedTypes(type, allTypes);
            }

            foreach (var type in allTypes)
            {
                ProcessType(type, module, diagnostics);
            }

            return CodeGenHelpers.GetResult(assembly, diagnostics);
        }

        private void CollectNestedTypes(TypeDefinition parentType, List<TypeDefinition> allTypes)
        {
            if (!parentType.HasNestedTypes) return;
            foreach (var nestedType in parentType.NestedTypes)
            {
                allTypes.Add(nestedType);
                CollectNestedTypes(nestedType, allTypes);
            }
        }

        private List<ServiceEntry> CollectServices(ModuleDefinition module, List<DiagnosticMessage> diagnostics)
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
                        : module.ImportReference(typeof(GlobalScope));

                    var scopeName = scopeType.Name.Replace("Scope", "");

                    foreach (var p in method.Parameters)
                    {
                        if (p.ParameterType.IsGenericParameter)
                        {
                            diagnostics.AddError(method, $"Cannot wire unbound generic parameter '{p.ParameterType.Name}' in '{method.Name}'.");
                            continue;
                        }

                        var typeName = FormatTypeName(p.ParameterType);
                        services.Add(new ServiceEntry { ParamType = p.ParameterType, FieldName = $"{scopeName}_{typeName}" });
                    }
                }
            }

            return services;
        }

        private void GenerateCodeInAssembly(List<ServiceEntry> services, ModuleDefinition module)
        {
            if (services.Count == 0) return;
            var codeGen = GetOrCreateCodeGenerate(module);
            foreach (var s in services) AddServiceField(codeGen, s.ParamType, s.FieldName, module);
        }

        private void InjectServiceBindings(ModuleDefinition module, List<DiagnosticMessage> diagnostics)
        {
            var codeGen = module.Types.FirstOrDefault(t => t.Name == k_codeGenClassName);
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
                        if (p.ParameterType.IsGenericParameter) continue;

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

        private TypeDefinition GetOrCreateCodeGenerate(ModuleDefinition module)
        {
            var type = module.Types.FirstOrDefault(t => t.Name == k_codeGenClassName);
            if (type != null) return type;

            type = new TypeDefinition("", k_codeGenClassName, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
            module.Types.Add(type);
            return type;
        }

        private void AddServiceField(TypeDefinition codeGen, TypeReference paramType, string fieldName, ModuleDefinition module)
        {
            if (codeGen.Fields.Any(f => f.Name == fieldName)) return;

            var field = new FieldDefinition(fieldName, FieldAttributes.Public | FieldAttributes.Static, module.ImportReference(paramType));
            codeGen.Fields.Add(field);
        }

        public void ProcessType(TypeDefinition type, ModuleDefinition module, List<DiagnosticMessage> diagnostics)
        {
            var wiredTargets = new List<WiredTarget>();

            foreach (var field in type.Fields)
            {
                var attr = field.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == _wiredAttributeType.FullName);
                if (attr == null) continue;

                if (field.IsStatic)
                {
                    diagnostics.AddError(type.Methods.FirstOrDefault(), $"[Wired] cannot be used on static field '{field.Name}' in '{type.Name}'.");
                    continue;
                }

                if (field.FieldType.IsGenericParameter)
                {
                    diagnostics.AddError(type.Methods.FirstOrDefault(), $"[Wired] cannot resolve unbound generic type '{field.FieldType.Name}' in '{type.Name}'.");
                    continue;
                }

                field.Attributes &= ~FieldAttributes.InitOnly;
                var scopeType = attr.ConstructorArguments.Count > 0 ? attr.ConstructorArguments[0].Value as TypeReference : module.ImportReference(typeof(GlobalScope));
                wiredTargets.Add(new WiredTarget { BackingField = field, ScopeType = scopeType, OriginalName = field.Name });
            }

            foreach (var prop in type.Properties)
            {
                var attr = prop.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == _wiredAttributeType.FullName);
                if (attr == null) continue;

                var backingField = type.Fields.FirstOrDefault(f => f.Name == $"<{prop.Name}>k__BackingField");
                if (backingField != null)
                {
                    if (backingField.IsStatic)
                    {
                        diagnostics.AddError(type.Methods.FirstOrDefault(), $"[Wired] cannot be used on static property '{prop.Name}' in '{type.Name}'.");
                        continue;
                    }

                    if (backingField.FieldType.IsGenericParameter)
                    {
                        diagnostics.AddError(type.Methods.FirstOrDefault(), $"[Wired] cannot resolve unbound generic type for property '{prop.Name}' in '{type.Name}'.");
                        continue;
                    }

                    backingField.Attributes &= ~FieldAttributes.InitOnly;
                    var scopeType = attr.ConstructorArguments.Count > 0 ? attr.ConstructorArguments[0].Value as TypeReference : module.ImportReference(typeof(GlobalScope));
                    wiredTargets.Add(new WiredTarget { BackingField = backingField, ScopeType = scopeType, OriginalName = prop.Name });
                }
                else
                {
                    diagnostics.AddWarning(type.Methods.FirstOrDefault(), $"Cannot wire property '{prop.Name}' in '{type.Name}'. Only auto-properties are supported.");
                }
            }

            if (wiredTargets.Count == 0) return;

            var initWiredMethod = GetOrCreateInitWired(type, module);
            InjectFieldsIntoMethod(wiredTargets, initWiredMethod, module, diagnostics);

            ProcessAfterWired(type, module, initWiredMethod, diagnostics);
        }

        private void ProcessAfterWired(TypeDefinition type, ModuleDefinition module, MethodDefinition initWiredMethod, List<DiagnosticMessage> diagnostics)
        {
            var afterMethods = new List<(MethodDefinition Method, int Order)>();

            foreach (var method in type.Methods)
            {
                var attr = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == _afterWiredAttributeType.FullName);
                if (attr == null) continue;
                
                if (method.Parameters.Count > 0)
                {
                    diagnostics.AddError(method, $"[AfterWired] method '{method.Name}' in '{type.Name}' must not have any parameters.");
                    continue;
                }

                var order = 0;
                if (attr.ConstructorArguments.Count > 0 && attr.ConstructorArguments[0].Value is int orderValue) order = orderValue;
                afterMethods.Add((method, order));
            }

            afterMethods.Sort((a, b) => a.Order.CompareTo(b.Order));

            var executorMethod = GetOrCreateAfterWiredExecutor(type, module);
            var il = executorMethod.Body.GetILProcessor();

            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, initWiredMethod));

            foreach (var target in afterMethods)
            {
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, target.Method));

                if (target.Method.ReturnType.FullName != "System.Void")
                {
                    il.Append(il.Create(OpCodes.Pop));
                }
            }

            il.Append(il.Create(OpCodes.Ret));

            var registerMethods = FindWiredRegisterMethods(type, diagnostics);
            if (registerMethods.Count > 0)
            {
                foreach (var registerMethod in registerMethods)
                {
                    InjectMethodCall(registerMethod, executorMethod);
                }
            }
            else if (IsMonoBehaviour(type))
            {
                var awake = GetOrCreateAwake(type, module);
                InjectMethodCall(awake, executorMethod);
            }
            else
            {
                var ctors = type.Methods.Where(m => m.IsConstructor && !m.IsStatic).ToList();
                if (ctors.Count == 0) return;

                foreach (var ctor in ctors)
                {
                    InjectIntoConstructor(ctor, executorMethod);
                }
            }
        }

        private List<MethodDefinition> FindWiredRegisterMethods(TypeDefinition type, List<DiagnosticMessage> diagnostics)
        {
            var results = new List<MethodDefinition>();

            foreach (var method in type.Methods)
            {
                var attr = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == _wiredRegisterAttributeType.FullName);
                if (attr == null) continue;

                if (method.IsStatic)
                {
                    diagnostics.AddError(method, $"[WiredRegister] cannot be used on static method '{method.Name}' in '{type.Name}'.");
                    continue;
                }

                if (method.Parameters.Count > 0)
                {
                    diagnostics.AddError(method, $"[WiredRegister] method '{method.Name}' in '{type.Name}' must not have any parameters.");
                    continue;
                }

                if (!method.HasBody)
                {
                    diagnostics.AddError(method, $"[WiredRegister] method '{method.Name}' in '{type.Name}' must have a body.");
                    continue;
                }

                results.Add(method);
            }

            return results;
        }

        private void InjectIntoConstructor(MethodDefinition ctor, MethodDefinition methodToCall)
        {
            if (!ctor.HasBody) return;
            var il = ctor.Body.GetILProcessor();

            Instruction insertPoint = null;
            var declaringType = ctor.DeclaringType;
            var baseType = declaringType.BaseType;
            var callsThisCtor = false;

            foreach (var instruction in ctor.Body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Call) continue;

                var methodRef = instruction.Operand as MethodReference;
                if (methodRef == null || methodRef.Name != ".ctor") continue;

                var resolvedMethodType = methodRef.DeclaringType.Resolve();
                var resolvedBaseType = baseType?.Resolve();

                if (resolvedMethodType != null && resolvedMethodType.FullName == declaringType.FullName)
                {
                    callsThisCtor = true;
                    break;
                }

                if (resolvedBaseType != null && resolvedMethodType != null &&
                    resolvedMethodType.FullName == resolvedBaseType.FullName)
                {
                    insertPoint = instruction.Next;
                    break;
                }
            }

            if (callsThisCtor) return;

            if (insertPoint == null)
            {
                if (declaringType.IsValueType)
                {
                    insertPoint = ctor.Body.Instructions.First();
                }
                else
                {
                    return;
                }
            }

            il.InsertBefore(insertPoint, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(insertPoint, il.Create(OpCodes.Call, methodToCall));
        }

        private FieldReference FindServiceField(ModuleDefinition currentModule, string fieldName)
        {
            var localCodeGen = currentModule.Types.FirstOrDefault(t => t.Name == k_codeGenClassName);
            if (localCodeGen != null)
            {
                var localField = localCodeGen.Fields.FirstOrDefault(f => f.Name == fieldName);
                if (localField != null) return localField;
            }

            foreach (var asmRef in currentModule.AssemblyReferences)
            {
                var resolvedAsm = currentModule.AssemblyResolver.Resolve(asmRef);
                if (resolvedAsm == null) continue;

                var refModule = resolvedAsm.MainModule;
                var refCodeGen = refModule.Types.FirstOrDefault(t => t.Name == k_codeGenClassName);
                if (refCodeGen == null) continue;

                var refField = refCodeGen.Fields.FirstOrDefault(f => f.Name == fieldName);
                if (refField == null) continue;

                return currentModule.ImportReference(refField);
            }

            return null;
        }

        private void InjectFieldsIntoMethod(List<WiredTarget> targets, MethodDefinition method, ModuleDefinition module, List<DiagnosticMessage> diagnostics)
        {
            var il = method.Body.GetILProcessor();
            var first = method.Body.Instructions.First();

            foreach (var target in targets)
            {
                var holderFieldName = BuildFieldNameFromTarget(target);
                var holderFieldRef = FindServiceField(module, holderFieldName);

                if (holderFieldRef == null)
                {
                    diagnostics.AddError(method,
                        $"Dependency not found for '{target.OriginalName}' in '{method.DeclaringType.Name}'. Missing [Service] in current or referenced assemblies.");
                    continue;
                }

                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldsfld, holderFieldRef));
                il.InsertBefore(first, il.Create(OpCodes.Stfld, target.BackingField));
            }
        }

        private MethodDefinition GetOrCreateInitWired(TypeDefinition type, ModuleDefinition module)
        {
            var initMethod = type.Methods.FirstOrDefault(m => m.Name == "ILForge_InitWired");
            if (initMethod != null) return initMethod;

            initMethod = new MethodDefinition("ILForge_InitWired", MethodAttributes.Private, module.TypeSystem.Void);
            type.Methods.Add(initMethod);
            var il = initMethod.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ret));
            return initMethod;
        }

        private MethodDefinition GetOrCreateAfterWiredExecutor(TypeDefinition type, ModuleDefinition module)
        {
            var executorMethod = type.Methods.FirstOrDefault(m => m.Name == "ILForge_ExecuteAfterWired");
            if (executorMethod != null)
            {
                executorMethod.Body.Instructions.Clear();
                return executorMethod;
            }

            executorMethod = new MethodDefinition("ILForge_ExecuteAfterWired", MethodAttributes.Private, module.TypeSystem.Void);
            type.Methods.Add(executorMethod);
            return executorMethod;
        }

        private void InjectMethodCall(MethodDefinition targetMethod, MethodDefinition methodToCall)
        {
            if (!targetMethod.HasBody) return;
            var il = targetMethod.Body.GetILProcessor();
            var insertPoint = targetMethod.Body.Instructions.First();
            il.InsertBefore(insertPoint, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(insertPoint, il.Create(OpCodes.Call, methodToCall));
        }

        private string BuildFieldNameFromTarget(WiredTarget target)
        {
            var scopeName = target.ScopeType.Name.Replace("Scope", "");
            var typeName = FormatTypeName(target.BackingField.FieldType);
            return $"{scopeName}_{typeName}";
        }

        private string BuildFieldNameFromTypeAndScope(TypeReference paramType, MethodDefinition method, ModuleDefinition module)
        {
            var attr = method.CustomAttributes.First(a => a.AttributeType.FullName == _serviceAttributeType.FullName);
            var scopeType = attr.ConstructorArguments.Count > 0 ? attr.ConstructorArguments[0].Value as TypeReference : module.ImportReference(typeof(GlobalScope));
            var scopeName = scopeType?.Name.Replace("Scope", "");
            var typeName = FormatTypeName(paramType);
            return $"{scopeName}_{typeName}";
        }

        private static string FormatTypeName(TypeReference typeRef)
        {
            return typeRef.FullName
                .Replace(".", "_")
                .Replace("/", "_")
                .Replace("<", "_")
                .Replace(">", "_")
                .Replace("`", "_")
                .Replace("[", "_")
                .Replace("]", "_")
                .Replace(",", "_");
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

        private MethodDefinition GetOrCreateAwake(TypeDefinition type, ModuleDefinition module)
        {
            var awake = type.Methods.FirstOrDefault(m => m.Name == "Awake" && m.Parameters.Count == 0 && m.ReturnType.FullName == "System.Void");
            if (awake != null) return awake;

            awake = new MethodDefinition("Awake", MethodAttributes.Family | MethodAttributes.HideBySig, module.TypeSystem.Void);
            type.Methods.Add(awake);
            var il = awake.Body.GetILProcessor();

            var currentBaseType = type.BaseType;
            MethodDefinition baseAwake = null;

            while (currentBaseType != null)
            {
                var baseTypeDef = currentBaseType.Resolve();
                if (baseTypeDef == null) break;

                baseAwake = baseTypeDef.Methods.FirstOrDefault(m => m.Name == "Awake" && m.Parameters.Count == 0 && m.ReturnType.FullName == "System.Void");
                if (baseAwake != null)
                {
                    if (baseAwake.IsVirtual) awake.Attributes |= MethodAttributes.Virtual;
                    if (baseAwake.IsPrivate) baseAwake = null;
                    
                    break;
                }

                if (baseTypeDef.FullName == "UnityEngine.MonoBehaviour") break;
                currentBaseType = baseTypeDef.BaseType;
            }

            var retInst = il.Create(OpCodes.Ret);
            if (baseAwake != null)
            {
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, module.ImportReference(baseAwake)));
            }

            il.Append(retInst);
            return awake;
        }
    }
}