﻿using CoatiSoftware.SourcetrailDB;
using SourcetrailDotnetIndexer.PdbSupport;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SourcetrailDotnetIndexer
{
    /// <summary>
    /// Handles types found in the assembly
    /// </summary>
    class TypeHandler
    {
        private readonly BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private readonly Assembly[] assemblies;
        private readonly NamespaceFilter nameFilter;
        private readonly NamespaceFilter namespaceFollowFilter;
        private readonly DataCollector dataCollector;
        private readonly PdbLocator pdbLocator;

        /// <summary>
        /// Invoked when a method or constructor is found
        /// </summary>
        public EventHandler<CollectedMethodEventArgs> MethodCollected;

        public TypeHandler(Assembly[] assemblies,
                           NamespaceFilter nameFilter,
                           NamespaceFilter namespaceFollowFilter,
                           DataCollector dataCollector,
                           PdbLocator pdbLocator)
        {
            this.assemblies = assemblies;
            this.nameFilter = nameFilter;
            this.namespaceFollowFilter = namespaceFollowFilter;
            this.dataCollector = dataCollector;
            this.pdbLocator = pdbLocator;

            foreach (var assembly in assemblies)
                Cache.CollectedAssemblies.Add(assembly, 1);
        }

        public Type[] GetInterfaceImplementors(Type interfaceType)
        {
            return Cache.InterfaceImplementations.TryGetValue(interfaceType, out var implementors)
                ? implementors.ToArray()
                : Array.Empty<Type>();
        }

        public int AddToDbIfValid(Type type)
        {
            if (type.HasElementType)
                type = type.GetElementType();

            // may already be collected
            if (Cache.CollectedTypes.TryGetValue(type, out var typeId))
                return typeId;

            if (type.IsGenericParameter
                || type.DeclaringType != null   // nested classes are collected when they are referenced
                || type.Namespace == null       // types generated by the compiler
                || !nameFilter.IsValid(type.Namespace))
                return 0;

            return AddToDb(type);
        }

        public int AddToDb(Type type, bool skipMembers = false)
        {
            if (!Cache.Namespaces.TryGetValue(type.Namespace, out _))
            {
                var nsId = dataCollector.CollectSymbol(type.Namespace, SymbolKind.SYMBOL_NAMESPACE);
                Cache.Namespaces.Add(type.Namespace, nsId);
            }

            var name = type.GetPrettyName();
            if (name == null)
                return 0;

            var kind = SymbolKind.SYMBOL_CLASS;
            if (type.IsEnum)
                kind = SymbolKind.SYMBOL_ENUM;
            else if (type.IsInterface)
                kind = SymbolKind.SYMBOL_INTERFACE;
            else if (type.IsGenericTypeDefinition)
                kind = SymbolKind.SYMBOL_TYPEDEF;
            // if class inherits from Attribute, treat as annotation
            if (typeof(Attribute).IsAssignableFrom(type))
                kind = SymbolKind.SYMBOL_ANNOTATION;

            var classId = dataCollector.CollectSymbol(name, kind);
            if (classId > 0 && !skipMembers)
            {
                foreach (var genType in type.GetGenericArguments())
                {
                    var genTypeId = AddToDbIfValid(genType);
                    if (genTypeId > 0)
                        dataCollector.CollectReference(classId, genTypeId, ReferenceKind.REFERENCE_TYPE_ARGUMENT);
                }
                if (type.IsGenericType && !type.IsGenericTypeDefinition)
                {
                    var genBaseType = type.GetGenericTypeDefinition();
                    var baseTypeId = AddToDbIfValid(genBaseType);
                    if (baseTypeId > 0)
                        dataCollector.CollectReference(classId, baseTypeId, ReferenceKind.REFERENCE_TEMPLATE_SPECIALIZATION);
                }

                // do not collect members for types from foreign assemblies unless they match the specified filter
                if (ShouldCollectType(type))
                    CollectTypeMembers(type, classId);
            }
            return classId;
        }

        public bool ShouldCollectType(Type type)
        {
            return type != null
                && nameFilter.IsValid(type.Namespace)
                && (assemblies.Any(asm => asm == type.Assembly) || namespaceFollowFilter.Matches(type.Namespace));
        }


        private void CollectTypeMembers(Type type, int typeSymbolId)
        {
            // skip, if already collected
            if (Cache.CollectedTypes.ContainsKey(type))
                return;

            if (!Cache.CollectedAssemblies.ContainsKey(type.Assembly))
            {
                Console.WriteLine("Following {0}", type.Assembly.Location);
                Cache.CollectedAssemblies.Add(type.Assembly, 1);
                pdbLocator.AddAssembly(type.Assembly);
            }

            Cache.CollectedTypes[type] = typeSymbolId;

            if (type.BaseType != null)
            {
                // nearly everything inherits from object, always ignore these
                if (type.BaseType != typeof(object))
                {
                    var baseTypeId = AddToDbIfValid(type.BaseType);
                    if (baseTypeId > 0)
                        dataCollector.CollectReference(typeSymbolId, baseTypeId, ReferenceKind.REFERENCE_INHERITANCE);
                }
            }
            // collect interfaces and their implementing classes
            // when we later parse the IL and a method makes a call to an interface-method,
            // we also collect a reference from the calling method to every class that implements that interface
            var ifaces = type.GetInterfaces();
            foreach (var iface in ifaces)
            {
                var ifaceId = AddToDbIfValid(iface);
                if (ifaceId > 0)
                {
                    dataCollector.CollectReference(typeSymbolId, ifaceId, ReferenceKind.REFERENCE_INHERITANCE);

                    if (!Cache.InterfaceImplementations.TryGetValue(iface, out var implementors))
                    {
                        implementors = new List<Type>();
                        Cache.InterfaceImplementations[iface] = implementors;
                    }
                    if (!implementors.Contains(type))
                        implementors.Add(type);
                }
            }
            // collect attributes and treat these as annotations
            foreach (var attribute in type.GetCustomAttributesData())
            {
                var attribId = AddToDbIfValid(attribute.AttributeType);
                if (attribId > 0)
                    dataCollector.CollectReference(typeSymbolId, attribId, ReferenceKind.REFERENCE_ANNOTATION_USAGE);
            }
            // collect all members of this type
            foreach (var member in type.GetMembers(flags))
            {
                var memberId = CollectMember(member, false, out _);
                if (memberId <= 0)
                    continue;

                if (member is MethodInfo method)
                {
                    var tid = AddToDbIfValid(method.ReturnType);
                    if (tid > 0)
                        dataCollector.CollectReference(memberId, tid, ReferenceKind.REFERENCE_TYPE_USAGE);
                    foreach (var param in method.GetParameters())
                    {
                        tid = AddToDbIfValid(param.ParameterType);
                        if (tid > 0)
                            dataCollector.CollectReference(memberId, tid, ReferenceKind.REFERENCE_TYPE_USAGE);
                    }
                    if (method.IsGenericMethod)
                    {
                        foreach (var genArg in method.GetGenericArguments())
                        {
                            tid = AddToDbIfValid(genArg);
                            if (tid > 0)
                                dataCollector.CollectReference(memberId, tid, ReferenceKind.REFERENCE_TYPE_ARGUMENT);
                        }
                    }
                    CollectMethod(method, memberId, typeSymbolId);
                }
                else if (member is ConstructorInfo ctor)
                {
                    foreach (var param in ctor.GetParameters())
                    {
                        var tid = AddToDbIfValid(param.ParameterType);
                        if (tid > 0)
                            dataCollector.CollectReference(memberId, tid, ReferenceKind.REFERENCE_TYPE_USAGE);
                    }
                    CollectMethod(ctor, memberId, typeSymbolId);
                }
                else if (member is TypeInfo nestedType)
                {
                    if (!nestedType.IsCompilerGenerated())     // ignore compiler-generated classes
                        AddToDb(nestedType);
                }
            }
        }

        /// <summary>
        /// Stores a type-member into the sourcetrail database
        /// </summary>
        /// <param name="bare">if true, only the bare member is collected, without attribute-info</param>
        /// <returns>The symbol-id of this member</returns>
        public int CollectMember(MemberInfo member, bool bare, out SymbolKind memberKind)
        {
            if (member is MethodInfo mi && mi.IsGenericMethod)
                member = mi.GetGenericMethodDefinition();
            var name = member.GetPrettyName(out SymbolKind kind, out string prefix, out string postfix);
            var memberId = name == null ? 0 : dataCollector.CollectSymbol(name, kind, prefix, postfix);
            if (memberId > 0 && !bare)
            {
                foreach (var att in member.GetCustomAttributesData())
                {
                    var attribId = AddToDbIfValid(att.AttributeType);
                    if (attribId > 0)
                        dataCollector.CollectReference(memberId, attribId, ReferenceKind.REFERENCE_ANNOTATION_USAGE);
                }
            }
            memberKind = kind;
            return memberId;
        }

        /// <summary>
        /// Stores a found method for later analysis
        /// </summary>
        /// <param name="method">The method, that shoud be analyzed</param>
        /// <param name="methodId">The already collected id of this method</param>
        /// <param name="classId">The already collected id of the class, the method is a member of</param>
        private void CollectMethod(MethodBase method, int methodId, int classId)
        {
            MethodCollected?.Invoke(this, new CollectedMethodEventArgs(new CollectedMethod(method, methodId, classId)));
        }
    }
}
