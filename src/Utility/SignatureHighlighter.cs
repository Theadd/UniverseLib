﻿using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UniverseLib.Runtime;

namespace UniverseLib.Utility
{
    /// <summary>
    /// For Unity rich-text syntax highlighting of Types and Members.
    /// </summary>
    public static class SignatureHighlighter
    {
        public const string NAMESPACE = "#a8a8a8";

        public const string CONST = "#92c470";

        public const string CLASS_STATIC = "#3a8d71";
        public const string CLASS_INSTANCE = "#2df7b2";

        public const string STRUCT = "#0fba3a";
        public const string INTERFACE = "#9b9b82";

        public const string FIELD_STATIC = "#8d8dc6";
        public const string FIELD_INSTANCE = "#c266ff";

        public const string METHOD_STATIC = "#b55b02";
        public const string METHOD_INSTANCE = "#ff8000";

        public const string PROP_STATIC = "#588075";
        public const string PROP_INSTANCE = "#55a38e";

        public const string LOCAL_ARG = "#a6e9e9";

        public const string OPEN_COLOR = "<color=";
        public const string CLOSE_COLOR = "</color>";
        public const string OPEN_ITALIC = "<i>";
        public const string CLOSE_ITALIC = "</i>";

        public static readonly Regex ArrayTokenRegex = new(@"\[,*?\]");

        public static readonly Color StringOrange = new(0.83f, 0.61f, 0.52f);
        public static readonly Color EnumGreen = new(0.57f, 0.76f, 0.43f);
        public static readonly Color KeywordBlue = new(0.3f, 0.61f, 0.83f);
        public static readonly string keywordBlueHex = KeywordBlue.ToHex();
        public static readonly Color NumberGreen = new(0.71f, 0.8f, 0.65f);

        private static readonly Dictionary<string, string> typeToRichType = new();
        private static readonly Dictionary<string, string> highlightedMethods = new();

        private static readonly Dictionary<Type, string> builtInTypesToShorthand = new()
        {
            { typeof(object),  "object" },
            { typeof(string),  "string" },
            { typeof(bool),    "bool" },
            { typeof(byte),    "byte" },
            { typeof(sbyte),   "sbyte" },
            { typeof(char),    "char" },
            { typeof(decimal), "decimal" },
            { typeof(double),  "double" },
            { typeof(float),   "float" },
            { typeof(int),     "int" },
            { typeof(uint),    "uint" },
            { typeof(long),    "long" },
            { typeof(ulong),   "ulong" },
            { typeof(short),   "short" },
            { typeof(ushort),  "ushort" },
            { typeof(void),    "void" },
        };

        internal static string GetClassColor(Type type)
        {
            if (type.IsAbstract && type.IsSealed)
                return CLASS_STATIC;
            else if (type.IsEnum || type.IsGenericParameter)
                return CONST;
            else if (type.IsValueType)
                return STRUCT;
            else if (type.IsInterface)
                return INTERFACE;
            else
                return CLASS_INSTANCE;
        }

        private static bool TryGetNamespace(Type type, out string ns)
        {
            var ret = !string.IsNullOrEmpty(ns = type.Namespace?.Trim());
            return ret;
        }

        private static StringBuilder OpenColor(StringBuilder sb, string color) => sb.Append(OPEN_COLOR).Append(color).Append('>');

        /// <summary>
        /// Highlight the full signature of the Type, including optionally the Namespace, and optionally combined with a MemberInfo.
        /// </summary>
        public static string Parse(Type type, bool includeNamespace, MemberInfo memberInfo = null)
        {
            if (type == null)
                throw new ArgumentNullException("type");

            var sb = new StringBuilder();

            if (type.IsByRef)
                OpenColor(sb, $"#{keywordBlueHex}").Append("ref ").Append(CLOSE_COLOR);

            // Namespace

            // Never include namespace for built-in types.
            Type nsTest = type;
            while (nsTest.HasElementType)
                nsTest = nsTest.GetElementType();
            includeNamespace &= !builtInTypesToShorthand.ContainsKey(nsTest);

            bool isGeneric = type.IsGenericParameter || (type.HasElementType && type.GetElementType().IsGenericParameter);

            if (!isGeneric)
            {
                if (includeNamespace && TryGetNamespace(type, out string ns))
                    OpenColor(sb, NAMESPACE).Append(ns).Append(CLOSE_COLOR).Append('.');

                // Declaring type

                var declaring = type.DeclaringType;
                while (declaring != null)
                {
                    sb.Append(HighlightType(declaring));
                    sb.Append('.');
                    declaring = declaring.DeclaringType;
                }
            }

            // Highlight the type name

            sb.Append(HighlightType(type));

            // If memberInfo, highlight the member info

            if (memberInfo != null)
            {
                sb.Append('.');

                int start = sb.Length - 1;
                OpenColor(sb, GetMemberInfoColor(memberInfo, out bool isStatic))
                    .Append(memberInfo.Name)
                    .Append(CLOSE_COLOR);

                if (isStatic)
                {
                    sb.Insert(start, OPEN_ITALIC);
                    sb.Append(CLOSE_ITALIC);
                }

                if (memberInfo is MethodInfo method)
                {
                    var args = method.GetGenericArguments();
                    if (args.Length > 0)
                        sb.Append('<').Append(ParseGenericArgs(args, true)).Append('>');
                }
            }

            return sb.ToString();
        }

        private static string HighlightType(Type type)
        {
            string key = type.ToString();

            if (typeToRichType.ContainsKey(key))
                return typeToRichType[key];

            var sb = new StringBuilder();

            if (type.IsByRef)
                type = type.GetElementType();

            int arrayDimensions = 0;
            if (ArrayTokenRegex.Match(type.Name) is Match match && match.Success)
            { 
                arrayDimensions = 1 + match.Value.Count(c => c == ',');
                type = type.GetElementType();
            }

            // Check for built-in types
            if (builtInTypesToShorthand.TryGetValue(type, out string builtInName))
                OpenColor(sb, $"#{keywordBlueHex}").Append(builtInName).Append(CLOSE_COLOR);
            else
                sb.Append(type.Name);

            if (string.IsNullOrEmpty(builtInName))
            {
                if (type.IsGenericParameter || (type.HasElementType && type.GetElementType().IsGenericParameter))
                {
                    sb.Insert(0, $"<color={CONST}>");
                    sb.Append(CLOSE_COLOR);
                }
                else
                {
                    var args = type.GetGenericArguments();

                    if (args.Length > 0)
                    {
                        // remove the `N from the end of the type name
                        // this could actually be >9 in some cases, so get the length of the length string and use that.
                        // eg, if it was "List`15", we would remove the ending 3 chars

                        int suffixLen = 1 + args.Length.ToString().Length;

                        // make sure the typename actually has expected "`N" format.
                        if (sb[sb.Length - suffixLen] == '`')
                            sb.Remove(sb.Length - suffixLen, suffixLen);
                    }

                    // highlight the base name itself
                    // do this after removing the `N suffix, so only the name itself is in the color tags.
                    sb.Insert(0, $"{OPEN_COLOR}{GetClassColor(type)}>");
                    sb.Append(CLOSE_COLOR);

                    // parse the generic args, if any
                    if (args.Length > 0)
                        sb.Append('<').Append(ParseGenericArgs(args)).Append('>');
                }
            }

            if (arrayDimensions > 0)
                sb.Append('[').Append(new string(',', arrayDimensions - 1)).Append(']');

            var ret = sb.ToString();
            typeToRichType.Add(key, ret);
            return ret;
        }

        /// <summary>
        /// Highlight the provided Types into a generic-arguments formatted rich-text string, optionally formatted as unbound generic parameters.
        /// </summary>
        public static string ParseGenericArgs(Type[] args, bool isGenericParams = false)
        {
            if (args.Length < 1)
                return string.Empty;

            var sb = new StringBuilder();

            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                    sb.Append(',').Append(' ');

                if (isGenericParams)
                {
                    sb.Append(OPEN_COLOR).Append(CONST).Append('>').Append(args[i].Name).Append(CLOSE_COLOR);
                    continue;
                }

                sb.Append(HighlightType(args[i]));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Highlight the provided method's signature with it's containing Type, and all arguments.
        /// </summary>
        public static string HighlightMethod(MethodInfo method)
        {
            var sig = method.FullDescription();
            if (highlightedMethods.ContainsKey(sig))
                return highlightedMethods[sig];

            var sb = new StringBuilder();

            // declaring type
            sb.Append(Parse(method.DeclaringType, false));
            sb.Append('.');

            // method name
            var color = !method.IsStatic
                    ? METHOD_INSTANCE
                    : METHOD_STATIC;
            sb.Append($"<color={color}>{method.Name}</color>");

            // generic arguments
            if (method.ContainsGenericParameters)
            {
                sb.Append("<");
                var genericArgs = method.GetGenericArguments();
                for (int i = 0; i < genericArgs.Length; i++)
                {
                    sb.Append($"<color={CONST}>{genericArgs[i].Name}</color>");
                    if (i < genericArgs.Length - 1)
                        sb.Append(", ");
                }
                sb.Append(">");
            }

            // arguments
            sb.Append('(');
            var parameters = method.GetParameters();
            if (parameters.Any())
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    var param = parameters[i];
                    sb.Append(Parse(param.ParameterType, false));
                    if (i < parameters.Length - 1)
                        sb.Append(", ");
                }
            }
            sb.Append(')');

            var ret = sb.ToString();
            highlightedMethods.Add(sig, ret);
            return ret;
        }

        /// <summary>
        /// Get the color used by SignatureHighlighter for the provided member, and whether it is static or not.
        /// </summary>
        public static string GetMemberInfoColor(MemberInfo memberInfo, out bool isStatic)
        {
            isStatic = false;
            if (memberInfo is FieldInfo fi)
            {
                if (fi.IsStatic)
                {
                    isStatic = true;
                    return FIELD_STATIC;
                }

                return FIELD_INSTANCE;
            }
            else if (memberInfo is MethodInfo mi)
            {
                if (mi.IsStatic)
                {
                    isStatic = true;
                    return METHOD_STATIC;
                }

                return METHOD_INSTANCE;
            }
            else if (memberInfo is PropertyInfo pi)
            {
                if (pi.GetAccessors(true)[0].IsStatic)
                {
                    isStatic = true;
                    return PROP_STATIC;
                }

                return PROP_INSTANCE;
            }
            //else if (memberInfo is EventInfo ei)
            //{
            //    if (ei.GetAddMethod().IsStatic)
            //    {
            //        isStatic = true;
            //        return EVENT_STATIC;
            //    }

            //    return EVENT_INSTANCE;
            //}

            throw new NotImplementedException(memberInfo.GetType().Name + " is not supported");
        }
    }
}
