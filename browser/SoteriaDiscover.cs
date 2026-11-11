using System;
using System.Collections.Generic;
using System.IO;

namespace Recognition.Browser
{
    // Finding SoteriaVault without being told where it is (so the link works for a dev build too, which has no Apps & Features entry to
    // read). Recognition knows its own repo root (…\Constellation\_Venture-Systems\recognition); SoteriaVault is a sibling repo in the
    // same Constellation tree (…\Constellation\_Fides-Systems\soteria-vault). This walks up from Recognition's root and, at each ancestor,
    // looks two levels down for a folder whose name looks like soteria-vault AND that actually publishes the Recognition connector
    // contract — so a folder is only accepted when it IS SoteriaVault for this browser, never a guess. Pure (the filesystem is injected),
    // read-only, and bounded; if nothing is found it returns null and the link stays "not configured" (SoteriaVault remains optional).
    internal static class SoteriaDiscover
    {
        // The file whose presence confirms a folder is the SoteriaVault that publishes Recognition's connector.
        public static readonly string[] MarkerParts = { "connectors", "contracts", "recognition.contract.v1.json" };

        public const int MaxAncestors = 6;
        public const int MaxPerLevel = 400;

        public static string MarkerPath(string root)
        {
            var p = root;
            foreach (var part in MarkerParts) p = Path.Combine(p, part);
            return p;
        }

        public static bool NameLooksLikeSoteria(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.Replace("-", "").Replace("_", "").ToLowerInvariant();
            return n.Contains("soteria");
        }

        /// <summary>
        /// The SoteriaVault folder in the Constellation tree, found from Recognition's <paramref name="repoRoot"/>, or null.
        /// <paramref name="hasFile"/> tests a file path; <paramref name="subdirs"/> returns a folder's immediate subdirectories (full
        /// paths). Both must never throw (return false / empty on access errors).
        /// </summary>
        public static string? FindDevRoot(string? repoRoot, Func<string, bool> hasFile, Func<string, IReadOnlyList<string>> subdirs)
        {
            if (string.IsNullOrWhiteSpace(repoRoot)) return null;
            string? dir = repoRoot;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int up = 0; up < MaxAncestors && dir != null && seen.Add(dir); up++)
            {
                var hit = ScanTwoDeep(dir, hasFile, subdirs);
                if (hit != null) return hit;
                dir = Parent(dir);
            }
            return null;
        }

        // At <root>, check each immediate subdir, and each of THOSE subdirs, for a soteria-named folder that has the marker.
        private static string? ScanTwoDeep(string root, Func<string, bool> hasFile, Func<string, IReadOnlyList<string>> subdirs)
        {
            int budget = MaxPerLevel;
            foreach (var group in subdirs(root))
            {
                if (budget-- <= 0) break;
                if (Accept(group, hasFile)) return group;
                int inner = MaxPerLevel;
                foreach (var repo in subdirs(group))
                {
                    if (inner-- <= 0) break;
                    if (Accept(repo, hasFile)) return repo;
                }
            }
            return null;
        }

        private static bool Accept(string folder, Func<string, bool> hasFile)
            => NameLooksLikeSoteria(LeafName(folder)) && hasFile(MarkerPath(folder));

        private static readonly char[] Separators = { '\\', '/' };

        // LeafName and Parent are deliberately pure string operations, not Path.GetFileName /
        // Path.GetDirectoryName: those rewrite separators to the host OS style (back-slash on Windows),
        // which silently breaks walking an injected tree whose paths use the other style — the discovery
        // then behaves differently on Windows than on Linux. Working off the separators actually present
        // keeps it identical on every OS and whether the caller passes Windows or POSIX paths, while real
        // Windows paths (all back-slash) still walk up correctly in production.
        private static string LeafName(string path)
        {
            var p = path.TrimEnd('\\', '/');
            int i = p.LastIndexOfAny(Separators);
            return i < 0 ? p : p.Substring(i + 1);
        }

        private static string? Parent(string path)
        {
            var p = path.TrimEnd('\\', '/');
            int i = p.LastIndexOfAny(Separators);
            if (i <= 0) return null;                                           // no separator, or only the leading root one
            var parent = p.Substring(0, i);
            return parent.Length == 2 && parent[1] == ':' ? null : parent;     // stop at a bare Windows drive root ("C:")
        }
    }
}
