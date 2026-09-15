// Compiled ONLY into net48 (Revit 2024 and earlier) configurations.
//
// The codebase uses C# 9 records and C# 11 `required` members. Both are compile-time
// features, but the compiler emits references to attribute types that ship in the
// .NET Core/5+ base class library and do not exist in .NET Framework 4.8. Declaring
// them here in the expected namespaces satisfies the compiler with no runtime cost
// and no behavioural difference.
//
// These are `internal`, so each assembly that needs them must compile its own copy -
// hence the <Compile Include Link> entries in RevitMcp.Core and RevitMcp.Addin.
// Duplicates across assemblies are fine; the compiler binds to the one in scope.

#if NETFRAMEWORK

namespace System.Runtime.CompilerServices
{
    /// <summary>Enables `init` accessors and positional records.</summary>
    internal static class IsExternalInit { }

    /// <summary>Marks a member emitted by the compiler for a `required` property.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    /// <summary>
    /// Emitted alongside `required` members so older compilers refuse to consume the
    /// assembly rather than silently ignoring the requirement.
    /// </summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

        public string FeatureName { get; }
        public bool IsOptional { get; init; }

        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>Tells the compiler a constructor sets all `required` members itself.</summary>
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}

namespace System.Diagnostics
{
    /// <summary>
    /// Process overloads that exist on .NET Core 3.0+ but not on .NET Framework 4.8.
    /// </summary>
    internal static class ProcessPolyfillExtensions
    {
        /// <summary>
        /// .NET Framework's <c>Process.Kill()</c> terminates only the target process,
        /// orphaning any children. <c>taskkill /T</c> terminates the process and its
        /// whole descendant tree, matching what the .NET Core overload does. If taskkill
        /// is unavailable or reports failure, this falls back to the plain kill so the
        /// caller still sees a thrown exception it can report.
        /// </summary>
        public static void Kill(this Process process, bool entireProcessTree)
        {
            if (!entireProcessTree)
            {
                process.Kill();
                return;
            }

            try
            {
                var psi = new ProcessStartInfo("taskkill", "/PID " + process.Id + " /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var killer = Process.Start(psi))
                {
                    if (killer != null)
                    {
                        killer.WaitForExit(5000);
                        if (killer.HasExited && killer.ExitCode == 0)
                            return;
                    }
                }
            }
            catch
            {
                // Fall through to the direct kill below.
            }

            process.Kill();
        }
    }
}

namespace System
{
    /// <summary>
    /// String overloads that exist on .NET Core 2.1+ but not on .NET Framework 4.8.
    /// <para>
    /// Declared in the <c>System</c> namespace so it is in scope everywhere via the
    /// implicit <c>using System;</c>, with no edits to calling code. On .NET 8+ this
    /// file is not compiled at all, and the real instance methods would win regardless,
    /// since instance methods take precedence over extension methods.
    /// </para>
    /// </summary>
    internal static class StringPolyfillExtensions
    {
        /// <summary>
        /// .NET Framework 4.8 only offers <c>Contains(string)</c> (ordinal) and
        /// <c>Contains(char)</c>. Routing through IndexOf preserves the comparison
        /// semantics the callers actually asked for - notably case-insensitive search.
        /// </summary>
        public static bool Contains(this string source, string value, StringComparison comparisonType)
            => source.IndexOf(value, comparisonType) >= 0;
    }
}

#endif
