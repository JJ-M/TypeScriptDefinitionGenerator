﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TypeScriptDefinitionGenerator.Helpers;

namespace TypeScriptDefinitionGenerator
{
    internal static class IntellisenseWriter
    {
        private static Dictionary<string, string> ExtendsPlaceholders { get; set; } = new Dictionary<string, string>();
        /// <summary>
        /// Generates TypeScript file for given C# class/enum (IntellisenseObject).
        /// </summary>
        /// <param name="objects">IntellisenseObject of class/enum</param>
        /// <param name="sourceItemPath">Path to C# source file</param>
        /// <returns>TypeScript file content as string</returns>
        public static string WriteTypeScript(IList<IntellisenseObject> objects, string sourceItemPath)
        {
            var sb = new StringBuilder();
            if (Options.AddAmdModuleName) {
                var moduleName = Path.GetFileNameWithoutExtension(sourceItemPath)
                    + Path.GetFileNameWithoutExtension(Utility.GetDefaultExtension(sourceItemPath));
                sb.AppendLine($"/// <amd-module name='{moduleName}'/>");
            }

            sb.AppendLine("// ------------------------------------------------------------------------------");
            sb.AppendLine("// <auto-generated>");
            sb.AppendFormat("//     This file was generated by TypeScript Definition Generator v{0}\r\n", Vsix.Version);
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("// ------------------------------------------------------------------------------");

            string export = !Options.DeclareModule ? "export " : string.Empty;
            string prefixModule = Options.DeclareModule ? "\t" : string.Empty;

            var sbBody = new StringBuilder();

            var neededImports = new List<string>();
            var imports = new List<string>();
            var exports = new List<string>();

            foreach (var ns in objects.GroupBy(o => o.Namespace))
            {
                if (Options.DeclareModule)
                {
                    sbBody.AppendFormat("declare module {0} {{\r\n", ns.Key);
                }

                foreach (IntellisenseObject io in ns)
                {
                    WriteTypeScriptComment(io.Summary, sbBody, prefixModule);

                    if (io.IsEnum)
                    {
                        string type = "const enum ";
                        sbBody.Append(prefixModule).Append(export).Append(type).Append(Utility.CamelCaseClassName(io.Name)).Append(" ");
                        if (!Options.DeclareModule) { exports.Add(Utility.CamelCaseClassName(io.Name)); }

                        sbBody.AppendLine("{");
                        WriteTSEnumDefinition(sbBody, prefixModule + "\t", io.Properties);
                        sbBody.Append(prefixModule).AppendLine("}");
                    }
                    else
                    {
                        string type = Options.ClassInsteadOfInterface ? "class " : "interface ";
                        sbBody.Append(prefixModule).Append(export).Append(type).Append(Utility.CamelCaseClassName(io.Name)).Append(" ");
                        if (!Options.DeclareModule) { exports.Add(Utility.CamelCaseClassName(io.Name)); }

                        string[] summaryLines = io.Summary?.Split(new string[] { Environment.NewLine }, StringSplitOptions.None);
                        string optionsLine = summaryLines?.SingleOrDefault(l => l.StartsWith("TypeScriptDefinitionGenerator:"));
                        var ignoreBase = optionsLine != null && optionsLine.Contains("IgnoreBaseType");

                        if (!string.IsNullOrEmpty(io.BaseName) && !ignoreBase)
                        {
                            var extendsContent = string.Empty;
                            sbBody.Append("#{ExtendsPlaceholder_" + io.BaseName + "}");
                            if (!string.IsNullOrEmpty(io.BaseNamespace) && io.BaseNamespace != io.Namespace)
                            {
                                extendsContent = $"extends {io.BaseNamespace}.{Utility.CamelCaseClassName(io.BaseName)} ";
                            }
                            else
                            {
                                extendsContent = $"extends {Utility.CamelCaseClassName(io.BaseName)} ";
                            }

                            if (!ExtendsPlaceholders.ContainsKey(io.BaseName))
                            {
                                ExtendsPlaceholders.Add(io.BaseName, extendsContent);
                        }
                        }

                        sbBody.AppendLine("{");
                        WriteTSInterfaceDefinition(sbBody, prefixModule + "\t", io.Properties);
                        sbBody.Append(prefixModule).AppendLine("}");
                        // Remember client-side references for which we need imports.
                        // Dictionary are built-in into TS, they need no imports.
                        neededImports.AddRange(io.Properties.Where(p => p.Type.ClientSideReferenceName != null &&
                            !p.Type.IsDictionary).Select(p => p.Type.ClientSideReferenceName));
                        // Remember that this class was already included (imported)
                        imports.Add(Utility.CamelCaseClassName(io.Name));
                    }
                }

                if (Options.DeclareModule)
                {
                    sbBody.AppendLine("}");
                }
            }

            neededImports.RemoveAll(n => imports.Contains(n));

            // if interface, import external interfaces and base classes
            if (!Options.DeclareModule)
            {
                var references = objects.SelectMany(o => o.References).Distinct();
                foreach (var reference in references)
                {
                    var referencePathRelative = Utility.GetRelativePath(sourceItemPath, reference);
                    // remove trailing ".ts" which is not expected for TS imports
                    referencePathRelative = referencePathRelative.Substring(0, referencePathRelative.Length - 3);
                    // make sure path contains forward slashes which are expected by TS
                    referencePathRelative = referencePathRelative.Replace(Path.DirectorySeparatorChar, '/');
                    var referenceName = Utility.RemoveDefaultExtension(Path.GetFileName(reference));

                    // skipped indirect references
                    if (!neededImports.Contains(referenceName))
                    {
                        continue;
                    }

                    sb.AppendLine($"import {{ {referenceName} }} from \"{referencePathRelative}\";");
                    imports.Add(referenceName);
                }

                // also import base classes if not yet imported
                var baseClasses = objects.Select(o => o.BaseName).Where(b => b != null && !imports.Contains(b)).Distinct();
                foreach (var b in baseClasses)
                {
                    var expectedBaseClassPath = Path.Combine(Path.GetDirectoryName(sourceItemPath), b + ".cs");
                    if (!File.Exists(expectedBaseClassPath))
                    {
                        var warningMessage =
                        $"Sorry, ignoring base class '{b}' because expected source file does not exist: {expectedBaseClassPath} ";
                        sb.AppendLine($"// {warningMessage}");
                        VSHelpers.WriteOnOutputWindow(warningMessage);
                        // remove placeholder from sbBody to prevent "extends " to be inserted later
                        sbBody.Replace("#{ExtendsPlaceholder_" + b + "}", string.Empty);
                    }
                    else
                    {
                        sb.AppendLine($"import {{ {b} }} from \"./{b}.generated\";");
                        imports.Add(b);
                    }
                }

                var notImportedNeededImports = neededImports.Except(imports).Except(exports).ToList();
                if (notImportedNeededImports.Any())
                {
                    var warningMessage =
                        $"Sorry, needed imports missing: {string.Join(", ", notImportedNeededImports)}. " +
                        $"Make sure file names match contained class/enum name.";
                    sb.AppendLine($"// {warningMessage}");
                    VSHelpers.WriteOnOutputWindow(warningMessage);
                }
            }

            foreach (var placeholder in ExtendsPlaceholders)
            {
                sbBody.Replace("#{ExtendsPlaceholder_" + placeholder.Key + "}", placeholder.Value);
            }

            sb.Append(sbBody);

            if (Options.EOLType == EOLType.LF)
                sb.Replace("\r\n", "\n");

            if (!Options.IndentTab)
                sb.Replace("\t", new string(' ', Options.IndentTabSize));

            return sb.ToString();
        }

        private static string CleanEnumInitValue(string value)
        {
            value = value.TrimEnd('u', 'U', 'l', 'L'); //uint ulong long
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return value;
            var trimedValue = value.TrimStart('0'); // prevent numbers to be parsed as octal in js.
            if (trimedValue.Length > 0) return trimedValue;
            return "0";
        }

        private static void WriteTypeScriptComment(string comment, StringBuilder sb, string prefix)
        {
            if (string.IsNullOrEmpty(comment)) return;
            string[] commentLines = comment.Split(
                new[] { "\r\n", "\r", "\n" },
                StringSplitOptions.None
            );
            bool isFirstLine = true;
            foreach (var commentLine in commentLines)
            {
                if (isFirstLine)
                {
                    sb.Append(prefix).AppendLine("/** ");
                    isFirstLine = false;
                }

                sb.Append(prefix).Append(" * ").AppendLine(commentLine.Replace("*/", "+/"));
            }
            sb.Append(prefix).AppendLine(" */");
        }

        private static void WriteTSEnumDefinition(StringBuilder sb, string prefix, IEnumerable<IntellisenseProperty> props)
        {
            foreach (var p in props)
            {
                WriteTypeScriptComment(p.Summary, sb, prefix);

                if (p.InitExpression != null)
                {
                    sb.AppendLine(prefix + Utility.CamelCaseEnumValue(p.Name) + " = " + CleanEnumInitValue(p.InitExpression) + ",");
                }
                else
                {
                    sb.AppendLine(prefix + Utility.CamelCaseEnumValue(p.Name) + ",");
                }
            }

        }

        private static void WriteTSInterfaceDefinition(StringBuilder sb, string prefix, IEnumerable<IntellisenseProperty> props)
        {
            foreach (var p in props)
            {
                WriteTypeScriptComment(p.Summary, sb, prefix);
                sb.AppendFormat("{0}{1}: ", prefix, Utility.CamelCasePropertyName(p.NameWithOption));

                if (p.Type.IsKnownType) sb.Append(p.Type.TypeScriptName);
                else
                {
                    if (p.Type.Shape == null) sb.Append("any");
                    else WriteTSInterfaceDefinition(sb, prefix, p.Type.Shape);
                }
                if (p.Type.IsArray) sb.Append("[]");

                sb.AppendLine(";");
            }
        }
    }
}
