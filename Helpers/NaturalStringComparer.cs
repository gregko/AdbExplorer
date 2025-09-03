using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AdbExplorer.Helpers
{
    public class NaturalStringComparer : IComparer<string>
    {
        private static readonly Regex NumberRegex = new Regex(@"\d+|\D+", RegexOptions.Compiled);

        public int Compare(string? x, string? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            var xParts = NumberRegex.Matches(x).Cast<Match>().Select(m => m.Value).ToArray();
            var yParts = NumberRegex.Matches(y).Cast<Match>().Select(m => m.Value).ToArray();

            int minLength = Math.Min(xParts.Length, yParts.Length);

            for (int i = 0; i < minLength; i++)
            {
                string xPart = xParts[i];
                string yPart = yParts[i];

                // Check if both parts are numeric
                if (IsNumeric(xPart) && IsNumeric(yPart))
                {
                    int xNum = int.Parse(xPart);
                    int yNum = int.Parse(yPart);
                    int numericComparison = xNum.CompareTo(yNum);
                    if (numericComparison != 0)
                        return numericComparison;
                }
                else
                {
                    // String comparison for non-numeric parts
                    int stringComparison = string.Compare(xPart, yPart, StringComparison.OrdinalIgnoreCase);
                    if (stringComparison != 0)
                        return stringComparison;
                }
            }

            // If all compared parts are equal, the shorter string comes first
            return xParts.Length.CompareTo(yParts.Length);
        }

        private static bool IsNumeric(string value)
        {
            return int.TryParse(value, out _);
        }
    }
}