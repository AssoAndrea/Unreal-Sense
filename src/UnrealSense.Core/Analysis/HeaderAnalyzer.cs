using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnrealSense.Cpp;
using UnrealSense.Project;
using UnrealSense.Reflection;
using UnrealSense.Workspace;

namespace UnrealSense.Analysis
{
    public sealed class AnalysisContext
    {
        public SpecifierCatalog Catalog { get; set; }
        public SymbolIndex Symbols { get; set; }
        public UnrealProject Project { get; set; }

        /// <summary>Finds the .cpp that implements a header (same file name inside the module).</summary>
        public string FindSourceFile(string headerPath)
        {
            if (headerPath == null) return null;
            var name = Path.GetFileNameWithoutExtension(headerPath) + ".cpp";
            var sibling = Path.Combine(Path.GetDirectoryName(headerPath), name);
            if (File.Exists(sibling)) return sibling;
            var module = Project?.FindModuleForFile(headerPath);
            if (module == null) return null;
            return ModuleScanner.EnumerateFiles(module.Directory, name).FirstOrDefault();
        }
    }

    /// <summary>Unreal-specific inspections for headers, mirroring UHT errors and Rider's UE inspections.</summary>
    public static class HeaderAnalyzer
    {
        public const string MissingGeneratedInclude = "UE0001";
        public const string GeneratedIncludeNotLast = "UE0002";
        public const string GeneratedIncludeWrongName = "UE0003";
        public const string MissingGeneratedBody = "UE0004";
        public const string WrongPrefix = "UE0005";
        public const string PrivateBlueprintAccess = "UE0006";
        public const string ConflictingSpecifiers = "UE0007";
        public const string PureWithoutOutput = "UE0008";
        public const string MissingImplementation = "UE0009";
        public const string MissingRepNotify = "UE0010";
        public const string MissingLifetimeProps = "UE0011";
        public const string RawObjectPointer = "UE0012";
        public const string UnknownSpecifier = "UE0013";
        public const string MissingSpecifierValue = "UE0014";
        public const string FunctionInStruct = "UE0015";
        public const string AssignableNotDynamic = "UE0016";
        public const string UFunctionOnOverride = "UE0017";

        static readonly string[][] ExclusiveGroups =
        {
            new[] { "EditAnywhere", "EditDefaultsOnly", "EditInstanceOnly", "VisibleAnywhere", "VisibleDefaultsOnly", "VisibleInstanceOnly" },
            new[] { "BlueprintReadOnly", "BlueprintReadWrite" },
            new[] { "BlueprintImplementableEvent", "BlueprintNativeEvent" },
            new[] { "Reliable", "Unreliable" },
            new[] { "Server", "Client", "NetMulticast" },
        };

        static readonly Regex RawPointerType = new Regex(@"^(?:class\s+)?(?<t>[AU][A-Z]\w*)\s*\*$", RegexOptions.Compiled);

        public static List<Diagnostic> Analyze(ParsedFile file, AnalysisContext context)
        {
            var result = new List<Diagnostic>();
            bool isHeader = file.FilePath == null || file.FilePath.EndsWith(".h", StringComparison.OrdinalIgnoreCase);

            if (isHeader) CheckGeneratedInclude(file, result);

            foreach (var type in file.Types)
            {
                CheckGeneratedBody(file, type, result);
                CheckPrefix(type, context, result);
                if (!type.IsNativeInterfaceClass)
                    CheckSpecifiers(file, type.Macro, context, result);

                if (type.Kind == ReflectedKind.Struct)
                    foreach (var fn in type.Functions)
                        result.Add(new Diagnostic(FunctionInStruct, DiagnosticSeverity.Error,
                            "USTRUCTs cannot contain UFUNCTIONs.", fn.Macro.Start, "UFUNCTION".Length));

                foreach (var property in type.Properties)
                    CheckProperty(file, type, property, context, result);
                foreach (var function in type.Functions)
                    CheckFunction(file, type, function, context, result);

                CheckReplication(file, type, context, result);
            }
            return result;
        }

        // ------------------------------------------------------------ includes / boilerplate

        static void CheckGeneratedInclude(ParsedFile file, List<Diagnostic> result)
        {
            if (!file.HasReflectedTypes) return;
            var expected = (file.FilePath != null ? Path.GetFileNameWithoutExtension(file.FilePath) : "File") + ".generated.h";
            var generated = file.GeneratedInclude;
            var firstType = file.Types.Count > 0 ? (file.Types[0].Macro.Start) : file.Delegates.First(d => d.IsDynamic).Start;

            if (generated == null)
            {
                var anchor = file.Includes.LastOrDefault(i => i.Start < firstType);
                int insertAt = anchor != null ? anchor.End : FindPragmaOnceEnd(file.Text);
                result.Add(new Diagnostic(MissingGeneratedInclude, DiagnosticSeverity.Error,
                        $"Reflected types require #include \"{expected}\" as the last include.", firstType, MacroLength(file, firstType))
                    .WithFix(new CodeFix($"Add #include \"{expected}\"", new TextEdit(insertAt, 0, $"\n#include \"{expected}\""))));
                return;
            }

            if (!string.Equals(Path.GetFileName(generated.Path), expected, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new Diagnostic(GeneratedIncludeWrongName, DiagnosticSeverity.Error,
                        $"Generated header should be \"{expected}\" (it must match the file name).", generated.Start, generated.Length)
                    .WithFix(new CodeFix($"Change to \"{expected}\"", new TextEdit(generated.Start, generated.Length, $"#include \"{expected}\""))));
            }

            var after = file.Includes.Where(i => i.Start > generated.Start).ToList();
            if (after.Count > 0)
            {
                var last = after.Last();
                int lineStart = file.Text.LastIndexOf('\n', Math.Max(0, generated.Start - 1)) + 1;
                int lineEnd = file.Text.IndexOf('\n', generated.End);
                lineEnd = lineEnd < 0 ? file.Text.Length : lineEnd + 1;
                var directive = file.Text.Substring(generated.Start, generated.Length);
                result.Add(new Diagnostic(GeneratedIncludeNotLast, DiagnosticSeverity.Error,
                        "The .generated.h include must be the last #include in the file.", generated.Start, generated.Length)
                    .WithFix(new CodeFix("Move .generated.h include to the end of the include list",
                        new TextEdit(last.End, 0, "\n" + directive),
                        new TextEdit(lineStart, lineEnd - lineStart, ""))));
            }
        }

        static int MacroLength(ParsedFile file, int start)
        {
            int i = start;
            while (i < file.Text.Length && (char.IsLetterOrDigit(file.Text[i]) || file.Text[i] == '_')) i++;
            return Math.Max(1, i - start);
        }

        static int FindPragmaOnceEnd(string text)
        {
            var m = Regex.Match(text, @"#\s*pragma\s+once[^\n]*");
            return m.Success ? m.Index + m.Length : 0;
        }

        static void CheckGeneratedBody(ParsedFile file, ReflectedType type, List<Diagnostic> result)
        {
            if (type.Kind == ReflectedKind.Enum || type.BodyOpen < 0 || type.GeneratedBodyMacro != null) return;
            var indent = DetectIndent(file.Text, type.BodyOpen);
            result.Add(new Diagnostic(MissingGeneratedBody, DiagnosticSeverity.Error,
                    $"'{type.Name}' is missing GENERATED_BODY().", type.NameStart, type.Name.Length)
                .WithFix(new CodeFix("Add GENERATED_BODY()", new TextEdit(type.BodyOpen + 1, 0, "\n" + indent + "GENERATED_BODY()\n"))));
        }

        static string DetectIndent(string text, int bodyOpen)
        {
            var m = Regex.Match(text.Substring(bodyOpen + 1, Math.Min(400, text.Length - bodyOpen - 1)), @"\n([ \t]+)\S");
            return m.Success ? m.Groups[1].Value : "\t";
        }

        static void CheckPrefix(ReflectedType type, AnalysisContext context, List<Diagnostic> result)
        {
            if (string.IsNullOrEmpty(type.Name)) return;
            char expected;
            switch (type.Kind)
            {
                case ReflectedKind.Struct: expected = 'F'; break;
                case ReflectedKind.Enum: expected = 'E'; break;
                case ReflectedKind.Interface: expected = type.IsNativeInterfaceClass ? 'I' : 'U'; break;
                default:
                    var baseName = type.BaseType;
                    if (string.IsNullOrEmpty(baseName) || baseName.Length < 2) return;
                    if (baseName[0] == 'A' && char.IsUpper(baseName[1])) expected = 'A';
                    else if (baseName[0] == 'U' && char.IsUpper(baseName[1])) expected = 'U';
                    else return;
                    break;
            }
            if (type.Name[0] == expected && type.Name.Length > 1 && char.IsUpper(type.Name[1])) return;

            var severity = type.Kind == ReflectedKind.Enum ? DiagnosticSeverity.Suggestion : DiagnosticSeverity.Error;
            var what = type.Kind == ReflectedKind.Class ? $"classes deriving from {type.BaseType}" : type.Macro.Name + " types";
            result.Add(new Diagnostic(WrongPrefix, severity,
                $"'{type.Name}' has an invalid Unreal prefix: {what} must start with '{expected}'.", type.NameStart, type.Name.Length));
        }

        // ------------------------------------------------------------ specifiers

        static void CheckSpecifiers(ParsedFile file, ReflectionMacro macro, AnalysisContext context, List<Diagnostic> result)
        {
            var target = SpecifierCatalog.TargetForMacro(macro.Name);
            var catalog = context?.Catalog;

            foreach (var group in ExclusiveGroups)
            {
                var present = macro.Specifiers.Where(s => group.Contains(s.Key, StringComparer.OrdinalIgnoreCase)).ToList();
                foreach (var extra in present.Skip(1))
                {
                    result.Add(new Diagnostic(ConflictingSpecifiers, DiagnosticSeverity.Error,
                            $"'{extra.Key}' conflicts with '{present[0].Key}'.", extra.KeyStart, extra.Key.Length)
                        .WithFix(RemoveSpecifierFix(file, macro, extra)));
                }
            }

            if (catalog == null || catalog.Count == 0) return;
            foreach (var spec in macro.Specifiers)
            {
                var info = catalog.FindSpecifier(target, spec.Key);
                if (info == null)
                {
                    if (!catalog.IsAuthoritative) continue;
                    result.Add(new Diagnostic(UnknownSpecifier, DiagnosticSeverity.Warning,
                        $"Unknown {macro.Name} specifier '{spec.Key}'.", spec.KeyStart, spec.Key.Length));
                }
                else if (info.TakesValue && string.IsNullOrEmpty(spec.Value))
                {
                    result.Add(new Diagnostic(MissingSpecifierValue, DiagnosticSeverity.Error,
                        $"'{spec.Key}' requires a value ({spec.Key}=...).", spec.KeyStart, spec.Key.Length));
                }
            }
        }

        static CodeFix RemoveSpecifierFix(ParsedFile file, ReflectionMacro macro, Specifier spec)
        {
            // Remove the specifier together with the comma before it (or after it if it is first).
            var text = file.Text;
            int start = spec.Start, end = spec.End;
            int before = start - 1;
            while (before > macro.OpenParen && char.IsWhiteSpace(text[before])) before--;
            if (text[before] == ',') start = before;
            else
            {
                int after = end;
                while (after < macro.CloseParen && char.IsWhiteSpace(text[after])) after++;
                if (after < macro.CloseParen && text[after] == ',')
                {
                    end = after + 1;
                    while (end < macro.CloseParen && text[end] == ' ') end++;
                }
            }
            return new CodeFix($"Remove '{spec.Key}'", new TextEdit(start, end - start, ""));
        }

        // ------------------------------------------------------------ properties

        static void CheckProperty(ParsedFile file, ReflectedType type, ReflectedProperty property, AnalysisContext context, List<Diagnostic> result)
        {
            var macro = property.Macro;
            CheckSpecifiers(file, macro, context, result);

            bool blueprintAccess = macro.Has("BlueprintReadOnly") || macro.Has("BlueprintReadWrite");
            if (blueprintAccess && property.Access == AccessLevel.Private && !macro.IsMetaTrue("AllowPrivateAccess"))
            {
                var spec = macro.Get("BlueprintReadWrite") ?? macro.Get("BlueprintReadOnly");
                result.Add(new Diagnostic(PrivateBlueprintAccess, DiagnosticSeverity.Error,
                        $"{spec.Key} should not be used on private members unless meta=(AllowPrivateAccess=\"true\") is set.", spec.KeyStart, spec.Key.Length)
                    .WithFix(AddMetaFix(macro, "AllowPrivateAccess = \"true\"")));
            }

            var rawPointer = RawPointerType.Match(property.Type ?? "");
            if (rawPointer.Success)
            {
                var pointee = rawPointer.Groups["t"].Value;
                result.Add(new Diagnostic(RawObjectPointer, DiagnosticSeverity.Suggestion,
                        $"Use TObjectPtr<{pointee}> for UPROPERTY object pointers (UE5 convention, enables access tracking).",
                        property.TypeStart, property.TypeEnd - property.TypeStart)
                    .WithFix(new CodeFix($"Convert to TObjectPtr<{pointee}>",
                        new TextEdit(property.TypeStart, property.TypeEnd - property.TypeStart, $"TObjectPtr<{pointee}>"))));
            }

            if (macro.Has("BlueprintAssignable") && context?.Symbols != null)
            {
                var delegateDecl = context.Symbols.FindDelegate(property.Type?.Trim());
                if (delegateDecl != null && !(delegateDecl.IsDynamic && delegateDecl.IsMulticast))
                {
                    var spec = macro.Get("BlueprintAssignable");
                    result.Add(new Diagnostic(AssignableNotDynamic, DiagnosticSeverity.Error,
                        $"BlueprintAssignable requires a dynamic multicast delegate; '{property.Type}' is declared with {delegateDecl.Macro}.", spec.KeyStart, spec.Key.Length));
                }
            }
        }

        static CodeFix AddMetaFix(ReflectionMacro macro, string entry)
        {
            if (macro.MetaOpenParen >= 0 && macro.MetaCloseParen > macro.MetaOpenParen)
            {
                bool empty = macro.Meta.Count == 0;
                return new CodeFix($"Add meta {entry}", new TextEdit(macro.MetaCloseParen, 0, (empty ? "" : ", ") + entry));
            }
            bool hasArgs = macro.Specifiers.Count > 0;
            return new CodeFix($"Add meta=({entry})", new TextEdit(macro.CloseParen, 0, (hasArgs ? ", " : "") + $"meta = ({entry})"));
        }

        // ------------------------------------------------------------ functions

        static void CheckFunction(ParsedFile file, ReflectedType type, ReflectedFunction function, AnalysisContext context, List<Diagnostic> result)
        {
            var macro = function.Macro;
            CheckSpecifiers(file, macro, context, result);

            if (macro.Has("BlueprintPure") && function.ReturnsVoid && !function.Parameters.Any(p => p.IsNonConstReference))
            {
                var spec = macro.Get("BlueprintPure");
                result.Add(new Diagnostic(PureWithoutOutput, DiagnosticSeverity.Error,
                        "BlueprintPure functions must have a return value or an output (non-const reference) parameter.", spec.KeyStart, spec.Key.Length)
                    .WithFix(new CodeFix("Replace BlueprintPure with BlueprintCallable", new TextEdit(spec.KeyStart, spec.Key.Length, "BlueprintCallable"))));
            }

            if (function.IsOverride && macro.Specifiers.Count > 0)
            {
                result.Add(new Diagnostic(UFunctionOnOverride, DiagnosticSeverity.Warning,
                    $"'{function.Name}' overrides a parent function: UHT does not allow re-declaring UFUNCTION specifiers on overrides.", macro.Start, "UFUNCTION".Length));
            }

            bool isNative = macro.Has("BlueprintNativeEvent");
            bool isRpc = macro.Has("Server") || macro.Has("Client") || macro.Has("NetMulticast");
            if ((isNative || isRpc) && context?.Symbols != null && type.Name != null)
            {
                var implName = function.Name + "_Implementation";
                if (!context.Symbols.HasDefinition(type.Name, implName))
                {
                    var cpp = context.FindSourceFile(file.FilePath);
                    var d = new Diagnostic(MissingImplementation, DiagnosticSeverity.Warning,
                        $"'{function.Name}' is {(isNative ? "a BlueprintNativeEvent" : "an RPC")}: {type.Name}::{implName} is not defined.", function.NameStart, function.Name.Length);
                    if (cpp != null)
                        d.WithFix(new CodeFix($"Create {implName} in {Path.GetFileName(cpp)}", TextEdit.Append(cpp, BuildStub(type, function, implName, false))));
                    result.Add(d);
                }
                if (macro.Has("WithValidation") && !context.Symbols.HasDefinition(type.Name, function.Name + "_Validate"))
                {
                    var cpp = context.FindSourceFile(file.FilePath);
                    var validateName = function.Name + "_Validate";
                    var d = new Diagnostic(MissingImplementation, DiagnosticSeverity.Warning,
                        $"'{function.Name}' has WithValidation: {type.Name}::{validateName} is not defined.", function.NameStart, function.Name.Length);
                    if (cpp != null)
                        d.WithFix(new CodeFix($"Create {validateName} in {Path.GetFileName(cpp)}", TextEdit.Append(cpp, BuildStub(type, function, validateName, true))));
                    result.Add(d);
                }
            }
        }

        public static string BuildStub(ReflectedType type, ReflectedFunction function, string name, bool validate)
        {
            var parameters = string.Join(", ", function.Parameters.Select(p => (p.Type + " " + p.Name).Trim()));
            var returnType = validate ? "bool" : (string.IsNullOrEmpty(function.ReturnType) ? "void" : function.ReturnType);
            var sb = new StringBuilder();
            sb.Append("\n").Append(returnType).Append(' ').Append(type.Name).Append("::").Append(name)
              .Append('(').Append(parameters).Append(')').Append(function.IsConst ? " const" : "").Append("\n{\n");
            if (validate) sb.Append("\treturn true;\n");
            else if (!function.ReturnsVoid) sb.Append("\treturn {};\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        // ------------------------------------------------------------ replication

        static void CheckReplication(ParsedFile file, ReflectedType type, AnalysisContext context, List<Diagnostic> result)
        {
            if (type.Kind != ReflectedKind.Class) return;
            var replicated = type.Properties.Where(p => p.Macro.Has("Replicated") || p.Macro.Has("ReplicatedUsing")).ToList();
            if (replicated.Count == 0) return;

            foreach (var property in replicated)
            {
                var repUsing = property.Macro.Get("ReplicatedUsing");
                if (repUsing == null || string.IsNullOrEmpty(repUsing.Value)) continue;
                bool found = type.Functions.Any(f => f.Name == repUsing.Value)
                             || (context?.Symbols != null && context.Symbols.GetSelfAndBases(type).Skip(1).Any(t => t.Functions.Any(f => f.Name == repUsing.Value)));
                if (found) continue;

                int lineEnd = file.Text.IndexOf('\n', property.DeclarationEnd);
                lineEnd = lineEnd < 0 ? file.Text.Length : lineEnd + 1;
                var indent = Regex.Match(file.Text.Substring(property.Macro.Start - Math.Min(property.Macro.Start, 64), Math.Min(property.Macro.Start, 64)), @"([ \t]*)$").Groups[1].Value;
                var decl = $"\n{indent}UFUNCTION()\n{indent}void {repUsing.Value}();\n";
                result.Add(new Diagnostic(MissingRepNotify, DiagnosticSeverity.Error,
                        $"Replication notify function '{repUsing.Value}' not found: it must be a UFUNCTION of {type.Name}.", repUsing.ValueStart, Math.Max(1, repUsing.ValueEnd - repUsing.ValueStart))
                    .WithFix(new CodeFix($"Declare UFUNCTION() void {repUsing.Value}()", new TextEdit(lineEnd, 0, decl))));
            }

            int at = type.BodyOpen < 0 ? -1 : file.Text.IndexOf("GetLifetimeReplicatedProps", type.BodyOpen, StringComparison.Ordinal);
            bool declaresProps = at >= 0 && (type.BodyClose < 0 || at < type.BodyClose);
            bool definesProps = context?.Symbols != null && context.Symbols.HasDefinition(type.Name, "GetLifetimeReplicatedProps");
            if (!declaresProps && !definesProps)
            {
                var first = replicated[0];
                var spec = first.Macro.Get("Replicated") ?? first.Macro.Get("ReplicatedUsing");
                result.Add(new Diagnostic(MissingLifetimeProps, DiagnosticSeverity.Warning,
                    $"{type.Name} has replicated properties but does not override GetLifetimeReplicatedProps (DOREPLIFETIME).", spec.KeyStart, spec.Key.Length));
            }
        }
    }
}
