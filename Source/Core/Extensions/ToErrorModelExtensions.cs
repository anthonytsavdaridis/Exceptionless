﻿#region Copyright 2014 Exceptionless

// This program is free software: you can redistribute it and/or modify it 
// under the terms of the GNU Affero General Public License as published 
// by the Free Software Foundation, either version 3 of the License, or 
// (at your option) any later version.
// 
//     http://www.gnu.org/licenses/agpl-3.0.html

#endregion

using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security;
using Exceptionless.Logging;
using Exceptionless.Models;
using Exceptionless.Models.Collections;
using Module = Exceptionless.Models.Module;
using StackFrame = System.Diagnostics.StackFrame;
#if !EMBEDDED
using CodeSmith.Core.Extensions;
using NLog.Fluent;

namespace Exceptionless.Core.Extensions {
    public
#else

namespace Exceptionless.Extensions {
    internal
#endif
        static class ToErrorModelExtensions {
        private static readonly ConcurrentDictionary<string, Module> _moduleCache = new ConcurrentDictionary<string, Module>();
        private static readonly string[] _exceptionExclusions = new[] { "HelpLink", "InnerException", "Message", "Source", "StackTrace", "TargetSite", "HResult" };

        /// <summary>
        /// Sets the properties from an exception.
        /// </summary>
        /// <param name="exception">The exception to populate properties from.</param>
        /// <param name="log">The log implementation used for diagnostic information.</param>
        public static Error ToErrorModel(this Exception exception
#if EMBEDDED
            , IExceptionlessLog log = null
#endif
            ) {
            if (exception == null)
                throw new ArgumentNullException("exception");

#if EMBEDDED
            if (log == null)
                log = new NullExceptionlessLog();
#endif

            Type type = exception.GetType();

            var error = new Error {
                Message = exception.GetMessage(),
#if EMBEDDED
                Modules = GetLoadedModules(log),
#else
                Modules = GetLoadedModules(),
#endif
                Type = type.FullName
            };
            error.PopulateStackTrace(error, exception);

#if !SILVERLIGHT
            try {
                PropertyInfo info = type.GetProperty("HResult", BindingFlags.NonPublic | BindingFlags.Instance);
                if (info != null)
                    error.Code = info.GetValue(exception, null).ToString();
            } catch (Exception) {}
#endif

            if (exception.TargetSite != null) {
                error.TargetMethod = new Method();
                error.TargetMethod.PopulateMethod(error, exception.TargetSite);
            }

            // TODO: Test adding non-serializable objects to ExtendedData and see what happens
            try {
                Dictionary<string, object> extraProperties = type.GetPublicProperties().Where(p => !_exceptionExclusions.Contains(p.Name)).ToDictionary(p => p.Name, p => {
                    try {
                        return p.GetValue(exception, null);
                    } catch {}
                    return null;
                });

                extraProperties = extraProperties.Where(kvp => !ValueIsEmpty(kvp.Value)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                if (extraProperties.Count > 0 && !error.ExtendedData.ContainsKey(ExtendedDataDictionary.EXCEPTION_INFO_KEY)) {
                    error.AddObject(new ExtendedDataInfo {
                        Data = extraProperties,
                        Name = ExtendedDataDictionary.EXCEPTION_INFO_KEY,
                        IgnoreSerializationErrors = true,
                        MaxDepthToSerialize = 5
                    });
                }
            } catch {}

            if (exception.InnerException != null)
                error.Inner = exception.InnerException.ToErrorModel();

            return error;
        }

        private static bool ValueIsEmpty(object value) {
            if (value == null)
                return true;

            if (value is IEnumerable) {
                if (!(value as IEnumerable).Cast<Object>().Any())
                    return true;
            }

            return false;
        }

        private static readonly List<string> _msPublicKeyTokens = new List<string> {
            "b77a5c561934e089",
            "b03f5f7f11d50a3a",
            "31bf3856ad364e35"
        };

        private static string GetMessage(this Exception exception) {
            string defaultMessage = String.Format("Exception of type '{0}' was thrown.", exception.GetType().FullName);
            string message = !exception.Message.IsNullOrEmpty()
                ? String.Join(" ", exception.Message.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim()
                : null;

            return !String.IsNullOrEmpty(message) ? message : defaultMessage;
        }

        private static ModuleCollection GetLoadedModules(
#if EMBEDDED
            IExceptionlessLog log,
#endif
            bool includeSystem = false, bool includeDynamic = false) {
            var modules = new ModuleCollection();

            int id = 1;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
#if PFX_LEGACY_3_5
                try {
                    if (!includeDynamic && assembly.ManifestModule is System.Reflection.Emit.MethodBuilder)
                        continue;   
                } catch (NotImplementedException ex) {
#if EMBEDDED
                    log.Error(ex, "An error occurred while checking if the current assembly is a dynamic assembly.");  
#endif
                }
#else
                if (!includeDynamic && assembly.IsDynamic)
                    continue;

                try {
                    if (!includeDynamic && String.IsNullOrEmpty(assembly.Location))
                        continue;
                } catch (SecurityException ex) {
                    const string message = "An error occurred while getting the Assembly.Location value. This error will occur when when you are not running under full trust.";
#if EMBEDDED
                    log.Error(typeof(ExceptionlessClient), ex, message);
#else
                    Trace.WriteLine(String.Format("{0} Exception: {1}", message, ex));
#endif
                }

#endif

                if (!includeSystem) {
                    try {
                        string publicKeyToken = assembly.GetAssemblyName().GetPublicKeyToken().ToHex();
                        if (_msPublicKeyTokens.Contains(publicKeyToken))
                            continue;

                        object[] attrs = assembly.GetCustomAttributes(typeof(GeneratedCodeAttribute), true);
                        if (attrs.Length > 0)
                            continue;
                    } catch {}
                }

#if EMBEDDED
                var module = assembly.ToModuleInfo(log);
                module.ModuleId = id;
                modules.Add(assembly.ToModuleInfo(log));
#else
                var module = assembly.ToModuleInfo();
                module.ModuleId = id;
                modules.Add(assembly.ToModuleInfo());
#endif
                id++;
            }

            return modules;
        }

        private static void PopulateStackTrace(this ErrorInfo error, Error root, Exception exception) {
            StackFrame[] frames = null;
            try {
                var st = new StackTrace(exception, true);
                frames = st.GetFrames();
            } catch {}

            if (frames == null)
                return;

            foreach (StackFrame frame in frames) {
                var stackFrame = new Exceptionless.Models.StackFrame {
                    LineNumber = frame.GetFileLineNumber(),
                    Column = frame.GetFileColumnNumber(),
                    FileName = frame.GetFileName()
                };

                //stackFrame.ExtendedData["ILOffset"] = frame.GetILOffset();
                //stackFrame.ExtendedData["NativeOffset"] = frame.GetNativeOffset();

                stackFrame.PopulateMethod(root, frame.GetMethod());

                error.StackTrace.Add(stackFrame);
            }
        }

        private static void PopulateMethod(this Method method, Error root, MethodBase methodBase) {
            if (methodBase == null)
                return;

            method.Name = methodBase.Name;
            if (methodBase.DeclaringType != null) {
                method.DeclaringNamespace = methodBase.DeclaringType.Namespace;
                if (methodBase.DeclaringType.MemberType == MemberTypes.NestedType)
                    method.DeclaringType = methodBase.DeclaringType.DeclaringType.Name + "+" + methodBase.DeclaringType.Name;
                else
                    method.DeclaringType = methodBase.DeclaringType.Name;
            }

            //method.ExtendedData["Attributes"] = (int)methodBase.Attributes;
            if (methodBase.IsGenericMethod) {
                foreach (Type type in methodBase.GetGenericArguments())
                    method.GenericArguments.Add(type.Name);
            }

            foreach (ParameterInfo parameter in methodBase.GetParameters()) {
                var parm = new Parameter {
                    Name = parameter.Name,
                    Type = parameter.ParameterType.Name,
                    TypeNamespace = parameter.ParameterType.Namespace
                };

#if !SILVERLIGHT
                //parm.ExtendedData["IsIn"] = parameter.IsIn;
#endif
                //parm.ExtendedData["IsOut"] = parameter.IsOut;
                //parm.ExtendedData["IsOptional"] = parameter.IsOptional;

                if (parameter.ParameterType.IsGenericParameter) {
                    foreach (Type type in parameter.ParameterType.GetGenericArguments())
                        parm.GenericArguments.Add(type.Name);
                }

                method.Parameters.Add(parm);
            }

            method.ModuleId = GetModuleId(root, methodBase.Module);
        }

        private static int GetModuleId(Error root, System.Reflection.Module module) {
            foreach (Module mod in root.Modules) {
                if (module.Assembly.FullName.StartsWith(mod.Name, StringComparison.OrdinalIgnoreCase))
                    return mod.ModuleId;
            }

            return -1;
        }

        public static Module ToModuleInfo(this System.Reflection.Module module, IExceptionlessLog log = null) {
            return ToModuleInfo(module.Assembly, log);
        }

        public static Module ToModuleInfo(this Assembly assembly, IExceptionlessLog log = null) {
            if (assembly == null)
                return null;

            if (log == null)
                log = new NullExceptionlessLog();

            Action<Exception, string> logError;
#if EMBEDDED
            logError = (e, message) => log.Error(e, message);
#else
            logError = (e, message) => Log.Error().Exception(e).Message(message).Report().Write();
#endif

            Module module = _moduleCache.GetOrAdd(assembly.FullName, k => {
                var mod = new Module();
                AssemblyName name = assembly.GetAssemblyName();
                if (name != null) {
                    mod.Name = name.Name;
                    mod.Version = name.Version.ToString();
                    byte[] pkt = name.GetPublicKeyToken();
                    if (pkt.Length > 0)
                        mod.ExtendedData["PublicKeyToken"] = pkt.ToHex();
                }

                string infoVersion = assembly.GetInformationalVersion();
                if (!String.IsNullOrEmpty(infoVersion) && infoVersion != mod.Version)
                    mod.ExtendedData["ProductVersion"] = infoVersion;

                string fileVersion = assembly.GetFileVersion();
                if (!String.IsNullOrEmpty(fileVersion) && fileVersion != mod.Version)
                    mod.ExtendedData["FileVersion"] = fileVersion;

                DateTime? creationTime = assembly.GetCreationTime();
                if (creationTime.HasValue)
                    mod.CreatedDate = creationTime.Value;

                DateTime? lastWriteTime = assembly.GetLastWriteTime();
                if (lastWriteTime.HasValue)
                    mod.ModifiedDate = lastWriteTime.Value;

                return mod;
            });

            if (module != null) {
                if (assembly == Assembly.GetEntryAssembly())
                    module.IsEntry = true;
            }

            return module;
        }
    }
}