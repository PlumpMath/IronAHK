using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace IronAHK.Scripting
{
    partial class MethodWriter
    {
        Type EmitMethodInvoke(CodeMethodInvokeExpression invoke)
        {
            MethodInfo target = null;
            Type[] types = null;
            bool staticCall = !(invoke.Method.TargetObject is CodeExpression);

            Depth++;
            Debug("Emitting method invoke " + invoke.Method.MethodName);

            if (invoke.Method.TargetObject == null && Methods.ContainsKey(invoke.Method.MethodName))
            {
                target = Methods[invoke.Method.MethodName].Method;
                types = ParameterTypes[invoke.Method.MethodName];
            }
            else if(staticCall)
            {
                var args = new ArgType[invoke.Parameters.Count];

                for (int i = 0; i < args.Length; i++)
                    args[i] = ArgType.Expression;

                target = Lookup.BestMatch(invoke.Method.MethodName, args);
            }
            else
                target = GetMethodInfo(invoke.Method);
            
            if(target == null)
                throw new CompileException(invoke, "Could not look up "+invoke.Method.MethodName);

            if (types == null && target != null)
            {
                var param = target.GetParameters();
                types = new Type[param.Length];

                for (int i = 0; i < types.Length; i++)
                    types[i] = param[i].ParameterType;
            }
            
            bool hasParams = types.Length == 0 ? false : types[types.Length-1] == typeof(System.Object[]);
            var ByRef = new Dictionary<LocalBuilder, CodeComplexVariableReferenceExpression>();

            Depth++;
            for (int i = 0; i < invoke.Parameters.Count; i++)
            {
                Debug("Emitting parameter " + i);
                
                if(hasParams)
                {
                    if(i == types.Length-1)
                        EmitArrayCreation(typeof(object), invoke.Parameters.Count - types.Length + 1);
                    
                    if(i >= types.Length-1)
                    {
                        EmitArrayInitializer(typeof(object), invoke.Parameters[i], i - types.Length + 1);
                        continue;
                    }
                }
                
                var generated = EmitExpression(invoke.Parameters[i], true);
                
                if(types[i].IsByRef && invoke.Parameters[i] is CodeComplexVariableReferenceExpression)
                {
                    Debug("Parameter "+i+" was by reference");
                    LocalBuilder Temporary = Generator.DeclareLocal(typeof(object));
                    Generator.Emit(OpCodes.Stloc, Temporary);
                    Generator.Emit(OpCodes.Ldloca, Temporary);

                    ByRef.Add(Temporary, invoke.Parameters[i] as CodeComplexVariableReferenceExpression);
                }
                else ForceTopStack(generated, types[i]);
            }
            Depth--;

            Depth++;
            for (int i = invoke.Parameters.Count; i < types.Length; i++)
            {
                Debug("Emitting default parameter " + i + " type " + types[i].Name);
                
                if(types[i].IsByRef)
                {
                    // Unspecified ref parameters are given temporary variable with default contents
                    Type NotRef = types[i].GetElementType();
                    EmitLiteral(NotRef, GetDefaultValueOfType(NotRef));
                    LocalBuilder Temp = Generator.DeclareLocal(NotRef);
                    Generator.Emit(OpCodes.Stloc, Temp);
                    Generator.Emit(OpCodes.Ldloca, Temp);
                }
                else EmitLiteral(types[i], GetDefaultValueOfType(types[i]));
            }
            Depth--;

            Generator.Emit(OpCodes.Call, target);

            foreach(KeyValuePair<LocalBuilder, CodeComplexVariableReferenceExpression> Pair in ByRef)
            {
                EmitComplexVariableReference(Pair.Value, false);
                Generator.Emit(OpCodes.Ldloc, Pair.Key);
                EmitSaveVar();
                Generator.Emit(OpCodes.Pop);
            }

            Depth--;

            return target.ReturnType;
        }

        object GetDefaultValueOfType(Type t)
        {
            bool array = t.IsArray;
            var rank = t.IsArray ? t.GetArrayRank() : -1;

            if (t == typeof(string))
                return string.Empty;
            else if (t == typeof(object[]))
                return new object[] { };
            else if (t == typeof(bool))
                return false;
            else if (t == typeof(int))
                return default(int);
            else if (t == typeof(decimal))
                return default(decimal);
            else if (t == typeof(float))
                return default(float);
            else if (!t.IsInterface && !t.IsClass)
                return t.GetConstructor(new Type[] { }).Invoke(new object[] { });
            else
                return null;
        }

        MethodInfo GetMethodInfo(CodeMethodReferenceExpression reference)
        {
            const string id = "MethodInfo";
            if (reference.UserData.Contains(id) && reference.UserData[id] is MethodInfo)
                return (MethodInfo)reference.UserData[id];

            MethodInfo method = null;

            Type target;
            if(reference.TargetObject is CodeTypeReferenceExpression)
                target = Type.GetType(((CodeTypeReferenceExpression)reference.TargetObject).Type.BaseType);
            else if (reference.TargetObject is CodeExpression)
            {
                target = EmitExpression(reference.TargetObject as CodeExpression);
            }
            else throw new CompileException(reference.TargetObject, "Non-static method referencing neither type nor expression");

            Console.WriteLine(target);

            Type[] parameters = new Type[reference.TypeArguments.Count];

            for (int i = 0; i < reference.TypeArguments.Count; i++)
                parameters[i] = Type.GetType(reference.TypeArguments[i].BaseType);

            while (target != null)
            {
                method = parameters.Length == 0 ? target.GetMethod(reference.MethodName, Type.EmptyTypes) :
                    target.GetMethod(reference.MethodName, parameters);

                if (method != null)
                    return method;

                if (target == typeof(object))
                    break;

                target = target.BaseType;
            }

            throw new CompileException(reference, "Could not find method " + reference.MethodName);
        }
    }
}
