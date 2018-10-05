using System;
using System.Collections.Generic;

namespace TinyJson
{
    /// <summary>
    /// JSON Utilities.
    /// </summary>
    public static class JSONUtilities
    {
        public static readonly HashSet<Type> NumericTypes = new HashSet<Type>
        {
            typeof(int),  typeof(long), typeof(short), typeof(sbyte),
            typeof(byte), typeof(ulong),   typeof(ushort),
            typeof(uint),
        };

        public static bool IsNullableType(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);
        }
    }
}
