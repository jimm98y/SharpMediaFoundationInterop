using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Compares two parsed parameter sets field by field.
    ///
    /// A parameter set written back out is not checked by the fact that it parses - read and write
    /// are generated from the same syntax, so a value that was never set writes a plausible
    /// default and reads back as that default. What catches it is comparing the values a decoder
    /// would end up with against the ones the encoder meant, which is what this does.
    /// </summary>
    public static class ParameterSetDiff
    {
        public static List<string> Compare(object expected, object actual)
        {
            var differences = new List<string>();
            if (expected == null || actual == null)
            {
                differences.Add("one side is missing");
                return differences;
            }

            foreach (var property in expected.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0))
            {
                object left, right;
                try
                {
                    left = property.GetValue(expected);
                    right = property.GetValue(actual);
                }
                catch (Exception)
                {
                    continue;   // a property that throws when the context is not set up is not a field
                }

                if (!Same(left, right))
                    differences.Add($"{property.Name}: {Describe(left)} against {Describe(right)}");
            }

            return differences;
        }

        private static bool Same(object left, object right)
        {
            if (left == null || right == null)
                return ReferenceEquals(left, right);

            if (left is string || left.GetType().IsPrimitive || left.GetType().IsEnum)
                return left.Equals(right);

            if (left is IEnumerable leftItems && right is IEnumerable rightItems)
            {
                var a = leftItems.Cast<object>().ToList();
                var b = rightItems.Cast<object>().ToList();
                return a.Count == b.Count && a.Zip(b, Same).All(same => same);
            }

            // Anything else is a nested syntax structure, compared by its own fields.
            return Compare(left, right).Count == 0;
        }

        private static string Describe(object value)
        {
            if (value == null)
                return "none";

            if (value is string || value.GetType().IsPrimitive)
                return value.ToString();

            if (value is IEnumerable items)
            {
                var list = items.Cast<object>().Take(6).Select(Describe).ToList();
                return "[" + string.Join(",", list) + "]";
            }

            return value.GetType().Name;
        }
    }
}
