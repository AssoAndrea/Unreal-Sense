using System;
using System.Collections.Generic;

namespace UnrealSense.Navigation
{
    /// <summary>
    /// Fast subsequence matcher with IDE-style scoring: prefix and word-start ("camel hump") matches,
    /// consecutive runs and exact case score higher. "GAL" matches GetActorLocation, "gactloc" too.
    /// Allocation-free on the hot path; <see cref="CharMask"/> lets callers reject most candidates up front.
    /// </summary>
    public sealed class FuzzyMatcher
    {
        readonly string pattern;
        readonly string lowerPattern;
        readonly int[] matchPositions;
        public ulong Mask { get; }

        public FuzzyMatcher(string pattern)
        {
            this.pattern = pattern ?? string.Empty;
            lowerPattern = this.pattern.ToLowerInvariant();
            matchPositions = new int[this.pattern.Length];
            Mask = CharMask(this.pattern);
        }

        public bool IsEmpty => pattern.Length == 0;

        /// <summary>64-bit set of the characters a string contains (letters folded, digits, '_').</summary>
        public static ulong CharMask(string s)
        {
            ulong mask = 0;
            for (int i = 0; i < s.Length; i++)
                mask |= Bit(s[i]);
            return mask;
        }

        static ulong Bit(char c)
        {
            if (c >= 'a' && c <= 'z') return 1UL << (c - 'a');
            if (c >= 'A' && c <= 'Z') return 1UL << (c - 'A');
            if (c >= '0' && c <= '9') return 1UL << (26 + c - '0');
            if (c == '_') return 1UL << 36;
            if (c == '.') return 1UL << 37;
            if (c == '/' || c == '\\') return 1UL << 38;
            return 1UL << 39;
        }

        static bool IsWordStart(string s, int i)
        {
            if (i == 0) return true;
            char prev = s[i - 1], c = s[i];
            if (prev == '_' || prev == '.' || prev == '/' || prev == '\\' || prev == ':' || prev == ' ' || prev == '-') return true;
            if (char.IsUpper(c) && !char.IsUpper(prev)) return true;
            if (char.IsDigit(c) && !char.IsDigit(prev)) return true;
            // "UFUNCTIONFoo": the 'F' of Foo in an uppercase run followed by lowercase
            return char.IsUpper(c) && i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(prev);
        }

        /// <summary>Score (higher is better) or int.MinValue when <paramref name="candidate"/> does not match.</summary>
        public int Score(string candidate)
        {
            int n = pattern.Length;
            if (n == 0) return 0;
            if (candidate.Length < n) return int.MinValue;

            // Greedy pass preferring word starts: for each pattern char, look ahead for a word-start occurrence
            // before falling back to the next plain occurrence. Good enough and O(len).
            int pos = 0;
            for (int p = 0; p < n; p++)
            {
                char pc = lowerPattern[p];
                int found = -1, plain = -1;
                for (int i = pos; i < candidate.Length; i++)
                {
                    if (char.ToLowerInvariant(candidate[i]) != pc) continue;
                    if (plain < 0) plain = i;
                    // Keep consecutive runs: if the previous pattern char matched right before, take it.
                    if (p > 0 && i == matchPositions[p - 1] + 1) { found = i; break; }
                    if (IsWordStart(candidate, i)) { found = i; break; }
                }
                if (found < 0) found = plain;
                if (found < 0) return int.MinValue;
                matchPositions[p] = found;
                pos = found + 1;
            }

            int score = 0;
            for (int p = 0; p < n; p++)
            {
                int i = matchPositions[p];
                score += 10;
                if (IsWordStart(candidate, i)) score += 20;
                if (p > 0 && i == matchPositions[p - 1] + 1) score += 15;
                else if (p > 0) score -= Math.Min(10, i - matchPositions[p - 1] - 1);
                if (candidate[i] == pattern[p]) score += 2;
            }
            if (matchPositions[0] == 0) score += 25;
            if (candidate.Length == n) score += 40; // exact
            score -= Math.Min(30, (candidate.Length - n) / 2);
            return score;
        }

        /// <summary>Positions of the last successful <see cref="Score"/> call (for highlighting).</summary>
        public IReadOnlyList<int> LastMatchPositions => matchPositions;

        public int[] GetMatchPositions(string candidate)
        {
            return Score(candidate) == int.MinValue ? Array.Empty<int>() : (int[])matchPositions.Clone();
        }
    }
}
