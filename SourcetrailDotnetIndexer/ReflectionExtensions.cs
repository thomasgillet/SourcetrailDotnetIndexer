﻿using CoatiSoftware.SourcetrailDB;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SourcetrailDotnetIndexer
{
    static class ReflectionExtensions
    {
        // matches a name generated by the compiler for auto-generated methods (lambdas, asyncs)
        // i.e. if a Method named "MyFunc" has a lambda inside it, the compiler extracts the code of the lambda
        // into a method named "<MyFunc>b_0" (for example)
        static readonly Regex rxInternName = new Regex("[<](?<name>\\w+)[>]", RegexOptions.Compiled);

        static readonly BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>
        /// Returns the "pretty" name of this <see cref="Type"/>
        /// </summary>
        public static string GetPrettyName(this Type type)
        {
            return NameHelper.TranslateTypeName(type);
        }

        /// <summary>
        /// Determines, whether this type represents the state-machine for the <b><paramref name="outerMethod"/></b>
        /// </summary>
        /// <param name="type">The type to check</param>
        /// <param name="outerMethod">The outer method of the async state-mchine</param>
        /// <param name="asyncWorker">Receives a <see cref="MethodInfo"/> for the worker-method of the async state-machine</param>
        /// <returns>true, if this may be a state-machine for an async method, otherwise false</returns>
        /// <remarks>
        /// Note that we rely on certain naming-conventions from the compiler here.
        /// This will break, if these conventions are changed.
        /// </remarks>
        public static bool IsAsyncStateMachineOf(this Type type, MethodBase outerMethod, out MethodInfo asyncWorker)
        {
            asyncWorker = null;
            if (type.DeclaringType != outerMethod.DeclaringType)
                return false;
            var match = rxInternName.Match(type.Name);
            if (!match.Success || type.DeclaringType == null)
                return false;
            var found = type.DeclaringType.GetMethods(flags).Any(m => m == outerMethod);
            asyncWorker = type.GetMethod("MoveNext", flags);     // this method executes the state machine
            return found && asyncWorker != null;
        }

        /// <summary>
        /// Determines, whether this method is a lambda body of <paramref name="outerMethod"/>
        /// </summary>
        /// <param name="method">The method to check for a lambda</param>
        /// <param name="outerMethod">The method the lambda is defined in</param>
        /// <returns></returns>
        public static bool IsLambdaOf(this MethodBase method, MethodBase outerMethod)
        {
            if (outerMethod == null)
                throw new ArgumentNullException(nameof(outerMethod));

            var match = rxInternName.Match(method.Name);
            return match.Success && (method.GetTopmostNonGeneratedType() == outerMethod.GetTopmostNonGeneratedType());
        }

        /// <summary>
        /// Gets the modifiers of this <see cref="MemberInfo"/> as a string
        /// </summary>
        /// <param name="member"></param>
        /// <returns>A string representing the modifiers applied to this <see cref="MemberInfo"/></returns>
        public static string GetModifiers(this MemberInfo member)
        {
            var result = "";
            if (member is FieldInfo fi)
            {
                if (fi.IsFamilyAndAssembly)
                    result += "private protected ";
                else if (fi.IsFamilyOrAssembly)
                    result += "protected internal ";
                else if (fi.IsAssembly)
                    result += "internal ";
                else if (fi.IsFamily)
                    result += "protected ";
                else if (fi.IsPublic)
                    result += "public ";
                else
                    result += "private ";
                if (fi.IsStatic)
                    result += "static ";
                if (fi.IsLiteral)
                    result += "const ";
            }
            else if (member is PropertyInfo pi)
            {
                var getMethod = pi.GetGetMethod(true);
                var setMethod = pi.GetSetMethod(true);
                // we do not handle separate modifiers for get/set methods
                var method = getMethod ?? setMethod;
                return method.GetModifiers();
            }
            else if (member is EventInfo ei)
            {
                var addMethod = ei.GetAddMethod(true);
                var removeMethod = ei.GetRemoveMethod(true);
                // we do not handle separate modifiers for add/remove methods
                var method = addMethod ?? removeMethod;
                return method.GetModifiers();
            }
            else if (member is MethodInfo mi)
            {
                if (mi.IsFamilyAndAssembly)
                    result += "private protected ";
                else if (mi.IsFamilyOrAssembly)
                    result += "protected internal ";
                else if (mi.IsAssembly)
                    result += "internal ";
                else if (mi.IsFamily)
                    result += "protected ";
                else if (mi.IsPublic)
                    result += "public ";
                else
                    result += "private ";
                if (mi.IsStatic)
                    result += "static ";
            }
            else if (member is ConstructorInfo ci)
            {
                if (ci.IsFamilyAndAssembly)
                    result += "private protected ";
                else if (ci.IsFamilyOrAssembly)
                    result += "protected internal ";
                else if (ci.IsAssembly)
                    result += "internal ";
                else if (ci.IsFamily)
                    result += "protected ";
                else if (ci.IsPublic)
                    result += "public ";
                if (ci.IsStatic)
                    result += "static ";
            }
            else if (member is Type t)
            {
                if (t.IsNested)
                {
                    if (t.IsNestedFamANDAssem)
                        result += "private protected ";
                    else if (t.IsNestedFamORAssem)
                        result += "protected internal ";
                    else if (t.IsNestedAssembly)
                        result += "internal ";
                    else if (t.IsNestedFamily)
                        result += "protected ";
                    else if (t.IsNestedPublic)
                        result += "public ";
                    else
                        result += "private ";
                }
                else
                {
                    if (t.IsPublic)
                        result += "public ";
                    if (!t.IsVisible)
                        result += "internal ";
                    if (t.IsAbstract && t.IsSealed)
                        result += "static ";
                    else if (t.IsAbstract && t.IsClass)
                        result = "abstract ";
                    else if (t.IsSealed && t.IsClass)
                        result += "sealed ";
                }
            }
            else
                throw new ArgumentException($"What's that ? Unhandled member info of type {member.GetType().FullName}", nameof(member));
            return result;
        }

        /// <summary>
        /// Returns the type that is at the top of the nesting-hierarchy for the specified class-member
        /// </summary>
        /// <returns>The <see cref="Type"/>, that is at the top of the nesting-hierarchy for the specified class-member</returns>
        public static Type GetTopmostNonGeneratedType(this MemberInfo member)
        {
            var type = member.DeclaringType;
            while (type.DeclaringType != null && type.IsCompilerGenerated())
                type = type.DeclaringType;
            return type;
        }

        /// <summary>
        /// Determines, whether this type is compiler-generated
        /// </summary>
        /// <param name="type">The <see cref="Type"/> to check</param>
        /// <returns>true, if this type is compiler-generated, otherwise false</returns>
        public static bool IsCompilerGenerated(this Type type)
        {
            while (type != null)
            {
                if (CustomAttributeData.GetCustomAttributes(type).Any(cad => cad.AttributeType == typeof(CompilerGeneratedAttribute)))
                //if (type.IsDefined(typeof(CompilerGeneratedAttribute)))
                    return true;
                type = type.DeclaringType;
            }
            return false;
        }

        /// <summary>
        /// Determines, whether the specified <paramref name="member"/> is declared in a compiler-generated class
        /// </summary>
        /// <returns>true, if <paramref name="member"/> is declared in a compiler-generated class, otherwise false</returns>
        public static bool IsMemberOfCompilerGeneratedClass(this MemberInfo member)
        {
            return member.DeclaringType.IsCompilerGenerated();
        }

        /// <summary>
        /// Returns the "pretty" name of this <see cref="MemberInfo"/> (including the full type-name)
        /// and also return the member-type (if any) into <b>prefix</b> and method-parameters (if any) into <b>postfix</b>
        /// </summary>
        /// <param name="member">Type-member to get the name of</param>
        /// <param name="memberKind">Receives the member-kind, this member represents</param>
        /// <param name="prefix">Receives the field/property-type or method return-type</param>
        /// <param name="postfix">Receives the method-parameters, if this member is a method</param>
        /// <returns>The full name of this member or null if this member is compiler-generated</returns>
        /// <remarks>
        /// Properties and events in .NET are actually methods with a special naming-convention.
        /// (i.e. get_<b>Name</b> / set_<b>Name</b> for properties and add_<b>Name</b> / remove_<b>Name</b> for events)
        /// As we're not interested in these methods, but in the properties/events they represent,
        /// we detect these methods here and return the name of the property/event instead while setting <b>memberKind</b> accordingly
        /// </remarks>
        public static string GetPrettyName(this MemberInfo member, out SymbolKind memberKind, out string prefix, out string postfix)
        {
            memberKind = SymbolKind.SYMBOL_FIELD;
            prefix = "";
            postfix = "";
            if (member.Name.Contains("<") || member.IsMemberOfCompilerGeneratedClass())  // compiler-generated
                return null;

            if (member is ConstructorInfo ctor)
            {
                memberKind = SymbolKind.SYMBOL_METHOD;
                prefix = ctor.GetModifiers();
                if (ctor.IsGenericMethod)
                    postfix += "<" 
                        + string.Join(", ", ctor.GetGenericArguments()
                                            .Select(a => NameHelper.TranslateTypeName(a, true))) 
                        + ">";
                postfix += SerializeParameters(ctor.GetParameters());
                return ctor.DeclaringType.GetPrettyName() + ctor.Name;
            }
            if (member is MethodInfo method)
            {
                // method generated by compiler (i.e. lambdas) ?
                var match = rxInternName.Match(method.Name);
                if (match.Success)
                {
                    if (member.DeclaringType.DeclaringType != null)
                        Debug.Assert(false, "Checkme");
                    var name = match.Groups["name"].Value;
                    // map getter/setter to correct property/event
                    if (IsPropertyOrEventMethod(method, name, out prefix, out string propertyOrEventName))
                        return method.DeclaringType.GetPrettyName() + "." + propertyOrEventName;
                    else
                    {
                        foreach (var meth in method.DeclaringType.GetMethods(flags))
                        {
                            if (meth.Name == name)
                            {
                                if (meth.DeclaringType.Name.Contains("<"))
                                    Debug.Assert(false, "Checkme");
                                memberKind = SymbolKind.SYMBOL_METHOD;
                                prefix = meth.GetModifiers() + meth.ReturnType.GetPrettyName();
                                return meth.DeclaringType.GetPrettyName() + "." + meth.Name;
                            }
                        }
                        Debug.Assert(false, "Method not found");
                    }
                }
                else
                {
                    // regular method
                    var name = method.Name;
                    // map getter/setter to correct property/event
                    if (IsPropertyOrEventMethod(method, name, out prefix, out string propertyOrEventName))
                        return method.DeclaringType.GetPrettyName() + "." + propertyOrEventName;
                    else
                    {
                        memberKind = SymbolKind.SYMBOL_METHOD;
                        prefix = method.GetModifiers() + NameHelper.TranslateTypeName(method.ReturnType);
                        if (method.IsGenericMethod)
                            postfix += "<" + string.Join(", ", method.GetGenericArguments().Select(a => NameHelper.TranslateTypeName(a, true))) + ">";
                        postfix += SerializeParameters(method.GetParameters());
                        return method.DeclaringType.GetPrettyName() + "." + method.Name;
                    }
                }
            }
            else if (member is PropertyInfo property)
            {
                if (member.DeclaringType.Name.Contains("<"))
                    return null;
                prefix = property.GetModifiers() + property.PropertyType.GetPrettyName();
                return property.DeclaringType.GetPrettyName() + "." + property.Name;
            }
            else if (member is FieldInfo field)
            {
                // ignore auto-generated backing fields for properties
                if (member.DeclaringType.Name.Contains("<") || field.Name.StartsWith("<"))
                    return null;
                prefix = field.GetModifiers() + field.FieldType.GetPrettyName();
                return field.DeclaringType.GetPrettyName() + "." + field.Name;
            }
            else if (member is EventInfo eventInfo)
            {
                if (member.DeclaringType.Name.Contains("<"))
                    return null;
                prefix = eventInfo.GetModifiers() + eventInfo.EventHandlerType.GetPrettyName();
                return eventInfo.DeclaringType.GetPrettyName() + "." + eventInfo.Name;
            }
            else if (member is TypeInfo nestedType)
            {
                if (nestedType.Name.Contains("<"))
                    return null;
                memberKind = SymbolKind.SYMBOL_CLASS;
                return nestedType.DeclaringType.GetPrettyName() + "." + nestedType.Name;
            }
            return null;
        }

        /// <summary>
        /// Determines whether the specified method is an accessor (getter/setter) for a property or an event
        /// </summary>
        /// <param name="method">The method to check</param>
        /// <param name="name">The unmangled name of the method</param>
        /// <param name="prefix">Receives the modifiers and property/event-handler type</param>
        /// <param name="propertyOrEventName">Receives the name of the property or event</param>
        /// <returns>true, if the specified method is an accessor-method, false otherwise</returns>
        private static bool IsPropertyOrEventMethod(MethodBase method, string name, out string prefix, out string propertyOrEventName)
        {
            if (name.StartsWith("get_") || name.StartsWith("set_"))
            {
                name = name.Substring(name.IndexOf('_') + 1);
                var prop = method.DeclaringType.GetProperties(flags).FirstOrDefault(p => p.Name == name);
                if (prop != null)
                {
                    prefix = prop.GetModifiers() + prop.PropertyType.GetPrettyName();
                    propertyOrEventName = prop.Name;
                    return true;
                }
                else
                    Debug.Assert(false, "Property not found");
            }
            else if (name.StartsWith("add_") || name.StartsWith("remove_"))
            {
                name = name.Substring(name.IndexOf('_') + 1);
                var ev = method.DeclaringType.GetEvents(flags).FirstOrDefault(e => e.Name == name);
                if (ev != null)
                {
                    prefix = ev.GetModifiers() + ev.EventHandlerType.GetPrettyName();
                    propertyOrEventName = ev.Name;
                    return true;
                }
                else
                    Debug.Assert(false, "Event not found");
            }
            prefix = propertyOrEventName = null;
            return false;
        }

        /// <summary>
        /// Serializes the provided parameters into a string enclosed in parenthesis (common coding convention)
        /// </summary>
        /// <param name="parameters">A list of method-parameters</param>
        /// <returns></returns>
        private static string SerializeParameters(ParameterInfo[] parameters)
        {
            var sb = new StringBuilder("(");
            foreach (var parameter in parameters)
            {
                if (sb.Length > 1)
                    sb.Append(", ");
                sb.AppendFormat("{0} {1}", NameHelper.TranslateTypeName(parameter.ParameterType), parameter.Name);
            }
            sb.Append(')');
            return sb.ToString();
        }

        /// <summary>
        /// Determines, whether the method described by the <see cref="MethodInfo"/> has the same parameters as this method
        /// </summary>
        /// <param name="method"></param>
        /// <param name="other"></param>
        /// <returns>true, if list of parameters matches, otherwise false</returns>
        public static bool HasSameParameters(this MethodInfo method, MethodBase other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other));

            var params1 = method.GetParameters();
            var params2 = other.GetParameters();

            if (params1.Length != params2.Length)
                return false;
            for (var i = 0; i < params1.Length; i++)
            {
                if (params1[i].ParameterType != params2[i].ParameterType)
                    return false;
            }
            return true;
        }
    }
}
